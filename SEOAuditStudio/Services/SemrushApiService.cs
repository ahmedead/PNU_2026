using System.Globalization;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Services
{
    /// <summary>
    /// Optional: domain metrics (traffic, keywords, Authority Score, backlinks) from the Semrush Analytics API.
    /// These numbers come from Semrush's own index and cannot be measured by crawling, so they need a
    /// Semrush API key (Business plan or API units). Every call spends API units.
    /// </summary>
    public sealed class SemrushApiService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public async Task<DomainMetrics> GetDomainAsync(string domain, string database, string apiKey, Action<string>? log, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Enter your Semrush API key in Settings to load domain metrics.");

            domain = CleanDomain(domain);
            var key = Uri.EscapeDataString(apiKey.Trim());
            var db = string.IsNullOrWhiteSpace(database) ? "us" : database.Trim().ToLowerInvariant();
            var metrics = new DomainMetrics { Domain = domain, Database = db };

            // Overview: Rank, organic/paid keywords and traffic
            var rank = await QueryAsync($"https://api.semrush.com/?type=domain_rank&key={key}&export_columns=Dn,Rk,Or,Ot,Oc,Ad,At&domain={domain}&database={db}", ct);
            if (rank.Count > 0)
            {
                var r = rank[0];
                metrics.Rank = Long(r, 1);
                metrics.OrganicKeywords = Long(r, 2);
                metrics.OrganicTraffic = Long(r, 3);
                metrics.OrganicCost = Dec(r, 4);
                metrics.PaidKeywords = Long(r, 5);
                metrics.PaidTraffic = Long(r, 6);
            }

            try
            {
                var bl = await QueryAsync($"https://api.semrush.com/analytics/v1/?key={key}&type=backlinks_overview&target={domain}&target_type=root_domain&export_columns=ascore,total,domains_num", ct);
                if (bl.Count > 0)
                {
                    metrics.AuthorityScore = (int?)Long(bl[0], 0);
                    metrics.Backlinks = Long(bl[0], 1);
                    metrics.ReferringDomains = Long(bl[0], 2);
                }
            }
            catch (Exception ex) { log?.Invoke("Semrush backlinks: " + ex.Message); }

            try
            {
                var kw = await QueryAsync($"https://api.semrush.com/?type=domain_organic&key={key}&display_limit=50&display_sort=tr_desc&export_columns=Ph,Po,Nq,Cp,Ur,Tr&domain={domain}&database={db}", ct);
                metrics.TopKeywords = kw.Select(r => new OrganicKeyword
                {
                    Keyword = Str(r, 0),
                    Position = (int)(Long(r, 1) ?? 0),
                    SearchVolume = Long(r, 2) ?? 0,
                    Cpc = Dec(r, 3) ?? 0,
                    Url = Str(r, 4),
                    TrafficPercent = (double)(Dec(r, 5) ?? 0)
                }).ToList();
            }
            catch (Exception ex) { log?.Invoke("Semrush keywords: " + ex.Message); }

            try
            {
                var comp = await QueryAsync($"https://api.semrush.com/?type=domain_organic_organic&key={key}&display_limit=10&export_columns=Dn,Cr,Np&domain={domain}&database={db}", ct);
                metrics.Competitors = comp.Select(r => $"{Str(r, 0)}  (relevance {Str(r, 1)}, {Str(r, 2)} common keywords)").ToList();
            }
            catch (Exception ex) { log?.Invoke("Semrush competitors: " + ex.Message); }

            return metrics;
        }

        /// <summary>Semrush returns semicolon-separated text with a header row, or "ERROR nn :: message".</summary>
        private static async Task<List<string[]>> QueryAsync(string url, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(url, ct);
            var text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            if (text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("NOTHING FOUND", StringComparison.OrdinalIgnoreCase)) return new List<string[]>();
                throw new InvalidOperationException("Semrush: " + text);
            }
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Semrush returned HTTP {(int)resp.StatusCode}: {text}");
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Skip(1)
                       .Select(l => l.TrimEnd('\r').Split(';'))
                       .ToList();
        }

        public static string CleanDomain(string input)
        {
            var v = input.Trim();
            if (Uri.TryCreate(v.Contains("://") ? v : "https://" + v, UriKind.Absolute, out var u)) v = u.Host;
            return v.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? v[4..] : v;
        }

        private static string Str(string[] r, int i) => i < r.Length ? r[i].Trim() : "";
        private static long? Long(string[] r, int i) => long.TryParse(Str(r, i), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
        private static decimal? Dec(string[] r, int i) => decimal.TryParse(Str(r, i), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
