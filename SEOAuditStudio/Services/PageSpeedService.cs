using System.Text.Json;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Services
{
    /// <summary>
    /// Google PageSpeed Insights (Lighthouse lab data + Chrome UX Report field data).
    /// Works without an API key at a low rate limit; a free key from Google Cloud raises it.
    /// </summary>
    public sealed class PageSpeedService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

        public async Task<PageSpeedResult> RunAsync(string url, string strategy, string apiKey, CancellationToken ct)
        {
            var result = new PageSpeedResult { Url = url, Strategy = strategy, TestedAt = DateTime.Now };
            var query = $"url={Uri.EscapeDataString(url)}&strategy={strategy}" +
                        "&category=performance&category=accessibility&category=best-practices&category=seo";
            if (!string.IsNullOrWhiteSpace(apiKey)) query += "&key=" + Uri.EscapeDataString(apiKey.Trim());

            try
            {
                using var resp = await Http.GetAsync("https://www.googleapis.com/pagespeedonline/v5/runPagespeed?" + query, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    result.Error = err.TryGetProperty("message", out var m) ? m.GetString() : $"HTTP {(int)resp.StatusCode}";
                    if ((int)resp.StatusCode == 429)
                        result.Error += " — add a free PageSpeed API key in Settings to raise the limit.";
                    return result;
                }

                if (root.TryGetProperty("lighthouseResult", out var lh))
                {
                    if (lh.TryGetProperty("categories", out var cats))
                    {
                        result.PerformanceScore = Score(cats, "performance");
                        result.AccessibilityScore = Score(cats, "accessibility");
                        result.BestPracticesScore = Score(cats, "best-practices");
                        result.SeoScore = Score(cats, "seo");
                    }
                    if (lh.TryGetProperty("audits", out var audits))
                    {
                        result.Fcp = Display(audits, "first-contentful-paint");
                        result.Lcp = Display(audits, "largest-contentful-paint");
                        result.Cls = Display(audits, "cumulative-layout-shift");
                        result.Tbt = Display(audits, "total-blocking-time");
                        result.SpeedIndex = Display(audits, "speed-index");

                        foreach (var a in audits.EnumerateObject())
                        {
                            var v = a.Value;
                            if (!v.TryGetProperty("details", out var det) || !det.TryGetProperty("type", out var t) || t.GetString() != "opportunity") continue;
                            if (v.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.GetDouble() >= 0.9) continue;
                            var title = v.TryGetProperty("title", out var ti) ? ti.GetString() : a.Name;
                            var dv = v.TryGetProperty("displayValue", out var d) ? d.GetString() : "";
                            result.Opportunities.Add(string.IsNullOrEmpty(dv) ? title! : $"{title} — {dv}");
                        }
                    }
                }

                if (root.TryGetProperty("loadingExperience", out var le) && le.TryGetProperty("metrics", out var metrics))
                {
                    result.FieldLcp = Percentile(metrics, "LARGEST_CONTENTFUL_PAINT_MS", v => $"{v / 1000.0:0.0} s");
                    result.FieldInp = Percentile(metrics, "INTERACTION_TO_NEXT_PAINT", v => $"{v} ms");
                    result.FieldCls = Percentile(metrics, "CUMULATIVE_LAYOUT_SHIFT_SCORE", v => $"{v / 100.0:0.00}");
                    if (le.TryGetProperty("overall_category", out var oc)) result.FieldCategory = oc.GetString() ?? "";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result.Error = SiteAuditor.Describe(ex);
            }
            return result;
        }

        private static int? Score(JsonElement cats, string name) =>
            cats.TryGetProperty(name, out var c) && c.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(s.GetDouble() * 100) : null;

        private static string Display(JsonElement audits, string name) =>
            audits.TryGetProperty(name, out var a) && a.TryGetProperty("displayValue", out var d) ? d.GetString() ?? "" : "";

        private static string Percentile(JsonElement metrics, string name, Func<long, string> format) =>
            metrics.TryGetProperty(name, out var m) && m.TryGetProperty("percentile", out var p) && p.TryGetInt64(out var v)
                ? format(v) + (m.TryGetProperty("category", out var c) ? $" ({c.GetString()})" : "") : "";
    }
}
