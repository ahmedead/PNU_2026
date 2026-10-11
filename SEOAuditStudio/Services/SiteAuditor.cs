using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Services
{
    public sealed class AuditProgress
    {
        public string Phase { get; init; } = "";
        public int Done { get; init; }
        public int Total { get; init; }
        public string CurrentUrl { get; init; } = "";
    }

    /// <summary>
    /// Crawls a site (or audits a fixed list of URLs), runs the page checks, then the checks that compare
    /// pages (duplicates, broken links) and the site-wide checks (robots.txt, sitemap, HTTPS, SSL).
    /// </summary>
    public sealed class SiteAuditor
    {
        private static readonly Regex AssetExtension = new(
            @"\.(jpe?g|png|gif|webp|svg|ico|bmp|tiff?|css|js|mjs|json|xml|txt|pdf|docx?|xlsx?|pptx?|zip|rar|7z|gz|tar|mp[34]|avi|mov|wmv|webm|ogg|wav|woff2?|ttf|eot|otf|exe|msi|dmg|apk|csv|rss|atom)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const int MaxHtmlBytes = 15 * 1024 * 1024;
        private const int MaxLinkChecks = 6000;
        private const int MaxSitemapChecks = 1000;
        private const int MaxVerifications = 400;
        private const int VerifyDelayMs = 250;

        public event Action<string>? Log;
        public event Action<AuditProgress>? Progress;

        private AuditSettings _s = new();
        private HttpClient _http = null!;
        private string _rootHost = "";
        private List<string> _sitemapUrls = new();
        private readonly HashSet<string> _siteHosts = new(StringComparer.OrdinalIgnoreCase);

        private PauseGate _pause = new();
        private const string PublicCheckName = "Audited through the public internet";

        /// <summary>
        /// Runs an audit. With <paramref name="resume"/>, continues that audit instead: the pages already crawled are
        /// kept, the crawl picks up the links they found but nobody visited yet (plus sitemap URLs, or the rest of the
        /// URL list), and all cross-page checks are recomputed over the merged set. <paramref name="pause"/> lets the
        /// caller pause and resume while it runs.
        /// </summary>
        public async Task<AuditResult> RunAsync(AuditMode mode, IReadOnlyList<string> urls, AuditSettings settings, CancellationToken ct,
                                                AuditResult? resume = null, PauseGate? pause = null)
        {
            if (resume != null)
            {
                mode = resume.Mode;
                urls = mode == AuditMode.CrawlSite ? new[] { resume.StartUrl } : resume.InputUrls;
                if (urls.Count == 0)
                    throw new InvalidOperationException("This URL-list audit was saved before Continue was available, so its URL list is unknown. Run it again as a new audit.");
            }
            if (urls.Count == 0) throw new ArgumentException("No URLs to audit.");
            _s = settings;
            _pause = pause ?? new PauseGate();

            // Active time excludes pauses
            var active = System.Diagnostics.Stopwatch.StartNew();
            void OnPauseChanged(bool paused) { if (paused) active.Stop(); else active.Start(); }
            _pause.StateChanged += OnPauseChanged;

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // redirects are followed by hand to record the chain
                AutomaticDecompression = DecompressionMethods.None, // decompressed by hand so compression can be checked
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                MaxConnectionsPerServer = Math.Max(2, settings.Concurrency),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(Math.Min(15, settings.TimeoutSeconds))
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,ar;q=0.8");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            _http = http;

            var startUri = new Uri(urls[0]);
            _rootHost = StripWww(startUri.Host);
            _siteHosts.Clear();
            foreach (var u in urls) if (Uri.TryCreate(u, UriKind.Absolute, out var x)) _siteHosts.Add(StripWww(x.Host));

            var result = resume ?? new AuditResult
            {
                StartUrl = urls[0],
                Mode = mode,
                StartedAt = DateTime.Now,
                InputUrls = mode == AuditMode.UrlList ? urls.ToList() : new List<string>(),
                Name = mode == AuditMode.CrawlSite ? startUri.Host : $"{urls.Count} URLs ({startUri.Host}{(_siteHosts.Count > 1 ? $" +{_siteHosts.Count - 1} hosts" : "")})"
            };
            double previousSeconds = resume == null ? 0 : (resume.ActiveSeconds > 0 ? resume.ActiveSeconds : (resume.FinishedAt - resume.StartedAt).TotalSeconds);
            int alreadyCrawled = result.Pages.Count;
            if (resume != null)
            {
                result.Cancelled = false;
                result.HitPageLimit = false;
                // A start URL that redirected (http → https, www) makes its final host part of the site too
                foreach (var p in result.Pages.Where(p => p.Depth == 0))
                    if (Uri.TryCreate(p.FinalUrl, UriKind.Absolute, out var f)) _siteHosts.Add(StripWww(f.Host));
                Log?.Invoke($"Continuing the audit of {result.Name}: {alreadyCrawled:N0} page(s) already crawled.");
            }

            try
            {
                // robots.txt and sitemap of the main host decide what the crawl may visit and seed it
                Report("Reading robots.txt", 0, 0, startUri.GetLeftPart(UriPartial.Authority));
                var robots = await LoadRobotsAsync(startUri, ct);
                result.SiteChecks.RemoveAll(c => c.Name == PublicCheckName);
                await CheckPublicAddressAsync(result, startUri.Host, ct);

                // The sitemap seeds the crawl, and every URL in it is validated afterwards ("incorrect pages in sitemap.xml")
                _sitemapUrls = await LoadSitemapUrlsAsync(startUri, robots, 5000, ct);
                var seeds = resume != null ? ResumeSeeds(result)
                    : urls.Select(u => (Url: u, Depth: 0, Via: mode == AuditMode.UrlList ? "URL list" : "Start URL", From: "")).ToList();
                if (mode == AuditMode.CrawlSite)
                {
                    if (_sitemapUrls.Count > 0 && resume == null) Log?.Invoke($"Sitemap lists {_sitemapUrls.Count} URL(s); they are added to the crawl queue.");
                    seeds.AddRange(_sitemapUrls.Select(u => (u, 1, "Sitemap", "")));
                }

                // A continued crawl may visit up to "Max pages" more pages; a URL list just finishes its list
                int maxPages = mode == AuditMode.UrlList ? urls.Count : alreadyCrawled + _s.MaxPages;
                await CrawlAsync(mode, seeds, robots, result, maxPages, ct);
                await RecheckFailedPagesAsync(result, ct);

                DropContentOfRedirects(result);

                if (!ct.IsCancellationRequested)
                {
                    // Cross-page results from an earlier session are recomputed over the merged set of pages
                    ResetDerivedResults(result);
                    var status = await CheckLinksAsync(result, mode, ct);
                    AddCrossPageIssues(result);
                    AddHreflangLinkIssues(result, status);
                    AddSitemapIssues(result, robots, status, mode);
                    await RunSiteChecksAsync(result, robots, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                _pause.StateChanged -= OnPauseChanged;
            }

            result.Cancelled = ct.IsCancellationRequested;
            if (result.Cancelled)
            {
                // Still compare what was crawled so the partial report is consistent
                AddCrossPageIssues(result);
                Log?.Invoke("Audit stopped; the report covers the pages crawled so far. Use Continue to crawl the rest.");
            }
            else if (result.HitPageLimit)
            {
                Log?.Invoke($"Reached the page limit with URLs still to visit. Use Continue to crawl up to {_s.MaxPages:N0} more pages.");
            }
            if (resume != null) Log?.Invoke($"This session added {result.Pages.Count - alreadyCrawled:N0} page(s); {result.Pages.Count:N0} in total.");
            ComputeScores(result);
            result.FinishedAt = DateTime.Now;
            result.ActiveSeconds = previousSeconds + active.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>
        /// Rebuilds the crawl queue of a saved audit: in a site crawl, every internal link on the crawled pages that was
        /// not visited yet; in a URL-list audit, the URLs of the list not audited yet.
        /// </summary>
        private List<(string Url, int Depth, string Via, string From)> ResumeSeeds(AuditResult result)
        {
            var seeds = new List<(string Url, int Depth, string Via, string From)>();
            if (result.Mode == AuditMode.UrlList)
            {
                var done = new HashSet<string>(result.Pages.Select(p => p.Url), StringComparer.Ordinal);
                seeds.AddRange(result.InputUrls.Where(u => !done.Contains(u)).Select(u => (u, 0, "URL list", "")));
                return seeds;
            }
            seeds.Add((result.StartUrl, 0, "Start URL", ""));
            foreach (var page in result.Pages.Where(p => p.Depth < _s.MaxDepth).OrderBy(p => p.Depth))
                foreach (var link in page.Links.Where(l => l.IsInternal && !l.NoFollow))
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var lu) && !AssetExtension.IsMatch(lu.AbsolutePath))
                        seeds.Add((link.Url, page.Depth + 1, "Link", page.Url));
            return seeds;
        }

        /// <summary>Issues and checks computed across pages after the crawl; a continued audit recomputes them.</summary>
        private static readonly HashSet<string> DerivedIssueIds = new()
        {
            IssueCatalog.BrokenInternalLink, IssueCatalog.BrokenExternalLink, IssueCatalog.BrokenInternalImage,
            IssueCatalog.BrokenExternalImage, IssueCatalog.IncorrectHreflangLink, IssueCatalog.SitemapIncorrectPage,
            IssueCatalog.DuplicateTitle, IssueCatalog.DuplicateDescription, IssueCatalog.DuplicateContent
        };

        private static void ResetDerivedResults(AuditResult result)
        {
            foreach (var p in result.Pages)
            {
                p.Issues.RemoveAll(i => DerivedIssueIds.Contains(i.IssueId));
                p.InSitemap = false;
            }
            result.SiteChecks.RemoveAll(c => c.Name != PublicCheckName);
        }

        // ---------------------------------------------------------------- crawl

        private async Task CrawlAsync(AuditMode mode, List<(string Url, int Depth, string Via, string From)> seeds, RobotsTxt robots, AuditResult result,
                                      int maxPages, CancellationToken ct)
        {
            var pending = new Queue<(string Url, int Depth)>();
            var queued = new HashSet<string>(StringComparer.Ordinal);
            // How each queued URL was found (first time wins): Start URL, URL list, Sitemap, or Link + the linking page
            var foundVia = new Dictionary<string, (string Via, string From)>(StringComparer.Ordinal);

            // Pages from an earlier session count as visited
            foreach (var p in result.Pages)
            {
                if (Uri.TryCreate(p.Url, UriKind.Absolute, out var u1)) queued.Add(Key(u1));
                if (Uri.TryCreate(p.FinalUrl, UriKind.Absolute, out var u2)) queued.Add(Key(u2));
            }

            int delayMs = _s.CrawlDelayMs;
            if (_s.RespectRobotsTxt && robots.CrawlDelaySeconds is int rd && rd > 0)
            {
                delayMs = Math.Max(delayMs, Math.Min(rd, 10) * 1000);
                Log?.Invoke($"robots.txt asks for a crawl delay of {rd}s; crawling one page at a time.");
            }
            int concurrency = delayMs > 0 ? 1 : Math.Max(1, _s.Concurrency);

            void Enqueue(string url, int depth, string via, string from)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
                var key = Key(uri);
                if (!queued.Add(key)) return;
                foundVia[key] = (via, from);
                if (mode == AuditMode.CrawlSite)
                {
                    if (_s.RespectRobotsTxt && !robots.IsAllowed(uri)) return;
                }
                pending.Enqueue((uri.AbsoluteUri, depth));
            }

            foreach (var (url, depth, via, from) in seeds) Enqueue(url, depth, via, from);
            if (result.Pages.Count > 0)
                Log?.Invoke(pending.Count == 0 ? "No URLs left to crawl." : $"{pending.Count:N0} URL(s) found but not crawled yet are queued.");

            var running = new List<Task<PageResult>>();
            while ((pending.Count > 0 || running.Count > 0))
            {
                // Paused: let requests in flight finish, then wait here until Resume (or Stop)
                if (_pause.IsPaused && running.Count == 0)
                {
                    Report("Paused", result.Pages.Count, Math.Min(maxPages, result.Pages.Count + pending.Count), "");
                    await _pause.WaitIfPausedAsync(ct);
                }

                while (!ct.IsCancellationRequested && !_pause.IsPaused && running.Count < concurrency && pending.Count > 0 &&
                       result.Pages.Count + running.Count < maxPages)
                {
                    var (url, depth) = pending.Dequeue();
                    running.Add(FetchPageAsync(url, depth, ct));
                    if (delayMs > 0) await Task.Delay(delayMs, CancellationToken.None);
                }
                if (running.Count == 0)
                {
                    if (_pause.IsPaused && pending.Count > 0 && result.Pages.Count < maxPages) continue;
                    break;
                }

                var done = await Task.WhenAny(running);
                running.Remove(done);
                PageResult page;
                try { page = await done; }
                catch (OperationCanceledException) { continue; }

                if (Uri.TryCreate(page.Url, UriKind.Absolute, out var pu) && foundVia.TryGetValue(Key(pu), out var fv))
                {
                    page.FoundVia = fv.Via;
                    page.FoundFrom = fv.From;
                }
                result.Pages.Add(page);
                Report(_pause.IsPaused ? "Pausing (finishing requests in progress)" : "Crawling",
                       result.Pages.Count, Math.Min(maxPages, result.Pages.Count + pending.Count + running.Count), page.Url);

                if (mode == AuditMode.CrawlSite && page.Depth < _s.MaxDepth)
                {
                    // The final URL counts as visited so redirects to it are not crawled twice
                    if (Uri.TryCreate(page.FinalUrl, UriKind.Absolute, out var fin)) queued.Add(Key(fin));
                    foreach (var link in page.Links.Where(l => l.IsInternal && !l.NoFollow))
                    {
                        if (Uri.TryCreate(link.Url, UriKind.Absolute, out var lu) && !AssetExtension.IsMatch(lu.AbsolutePath))
                            Enqueue(link.Url, page.Depth + 1, "Link", page.Url);
                    }
                }
            }
            // Stopped by the limit while URLs remained: a later Continue can pick them up
            result.HitPageLimit = !ct.IsCancellationRequested && pending.Count > 0 && result.Pages.Count >= maxPages;
        }

        private async Task<PageResult> FetchPageAsync(string url, int depth, CancellationToken ct)
        {
            var page = new PageResult { Url = url, FinalUrl = url, Depth = depth };
            try
            {
                var fetch = await FetchWithRetryAsync(url, ct);
                page.StatusCode = fetch.StatusCode;
                page.FinalUrl = fetch.FinalUrl;
                page.RedirectChain = fetch.Chain;
                page.ResponseTimeMs = fetch.ElapsedMs;
                page.ContentType = fetch.ContentType;
                page.ContentEncoding = fetch.ContentEncoding;
                page.SizeBytes = fetch.Body?.Length ?? fetch.ContentLength ?? 0;
                page.HasHsts = fetch.Hsts;
                AddHttpIssues(page, fetch);

                if (fetch.StatusCode is >= 200 and < 300 && page.IsHtml && fetch.Body != null)
                {
                    var html = DecodeHtml(fetch.Body, fetch.Charset);
                    var finalUri = new Uri(page.FinalUrl);
                    await Task.Run(() => PageAnalyzer.Analyze(page, html, finalUri, _s, IsInternal), ct);
                    if (fetch.XRobotsNoIndex && !page.Issues.Any(i => i.IssueId == IssueCatalog.NoIndex))
                        page.Issues.Add(new PageIssue { IssueId = IssueCatalog.NoIndex, Detail = "X-Robots-Tag header" });
                }
                else if (fetch.StatusCode is >= 200 and < 300 && !page.IsHtml)
                {
                    page.Issues.Add(new PageIssue { IssueId = IssueCatalog.NonHtml, Detail = page.ContentType });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                page.Error = Describe(ex);
                page.Issues.Add(new PageIssue { IssueId = IssueCatalog.FetchFailed, Detail = page.Error });
            }
            return page;
        }

        /// <summary>
        /// Pages that still ended in 429/5xx or a timeout during the crawl are fetched again at the end, slowly and
        /// one at a time, once the crawl's load is gone. Only failures that persist are reported.
        /// </summary>
        private async Task RecheckFailedPagesAsync(AuditResult result, CancellationToken ct)
        {
            var failed = result.Pages.Select((p, i) => (p, i))
                .Where(x => (x.p.StatusCode is 429 or 502 or 503 or 504) ||
                            (x.p.StatusCode == 0 && x.p.Error is string e && !e.StartsWith("DNS") && !e.StartsWith("SSL")))
                .Take(MaxVerifications).ToList();
            if (failed.Count == 0 || ct.IsCancellationRequested) return;

            Log?.Invoke($"Re-checking {failed.Count:N0} page(s) that failed during the crawl, one at a time...");
            int recovered = 0, n = 0;
            foreach (var (page, index) in failed)
            {
                ct.ThrowIfCancellationRequested();
                if (_pause.IsPaused) { Report("Paused", n, failed.Count, ""); await _pause.WaitIfPausedAsync(ct); }
                Report("Re-checking failed pages", ++n, failed.Count, page.Url);
                await Task.Delay(VerifyDelayMs * 4, ct);
                var again = await FetchPageAsync(page.Url, page.Depth, ct);
                again.FoundVia = page.FoundVia;
                again.FoundFrom = page.FoundFrom;
                if (again.StatusCode is >= 200 and < 400) recovered++;
                result.Pages[index] = again;
            }
            if (recovered > 0)
                Log?.Invoke($"{recovered:N0} of them loaded normally on the second pass (the server was busy or throttling).");
        }

        /// <summary>
        /// A busy or throttling server answers 429/502/503/504 or times out although the page is fine:
        /// wait and ask again (twice) before recording the failure. DNS and certificate errors are not retried.
        /// </summary>
        private async Task<FetchResult> FetchWithRetryAsync(string url, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var f = await FetchAsync(url, readBody: true, ct);
                    if (attempt >= 2 || f.StatusCode is not (429 or 502 or 503 or 504)) return f;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (attempt < 2 && !Describe(ex).StartsWith("DNS") && !Describe(ex).StartsWith("SSL")) { }
                await Task.Delay((attempt + 1) * 3000, ct);
            }
        }

        private void AddHttpIssues(PageResult page, FetchResult f)
        {
            void Add(string id, string detail = "") => page.Issues.Add(new PageIssue { IssueId = id, Detail = detail });

            if (f.StatusCode is >= 400 and < 500) Add(IssueCatalog.Http4xx, $"HTTP {f.StatusCode}");
            else if (f.StatusCode >= 500) Add(IssueCatalog.Http5xx, $"HTTP {f.StatusCode}");
            else if (f.StatusCode is >= 300 and < 400) Add(IssueCatalog.Http4xx, $"Too many redirects (HTTP {f.StatusCode})");

            if (f.Chain.Count > 1) Add(IssueCatalog.RedirectChain, string.Join(" → ", f.Chain.Append(f.FinalUrl)));
            else if (f.Chain.Count == 1) Add(IssueCatalog.Redirected, $"→ {f.FinalUrl}");
            if (f.TemporaryRedirect) Add(IssueCatalog.TemporaryRedirect);

            if (f.ElapsedMs > _s.SlowPageMs) Add(IssueCatalog.SlowPage, $"{f.ElapsedMs:N0} ms");
            if (page.SizeBytes > _s.LargePageKb * 1024L) Add(IssueCatalog.LargePage, page.SizeText);
            if (page.IsHtml && f.StatusCode == 200 && page.SizeBytes > 1400 && string.IsNullOrEmpty(f.ContentEncoding))
                Add(IssueCatalog.NoCompression);

            if (Uri.TryCreate(page.Url, UriKind.Absolute, out var u))
            {
                if (page.Url.Length > 115) Add(IssueCatalog.UrlTooLong, $"{page.Url.Length} characters");
                if (u.AbsolutePath.Contains('_')) Add(IssueCatalog.UrlUnderscores);
                if (u.AbsolutePath.Any(char.IsUpper)) Add(IssueCatalog.UrlUppercase);
            }
        }

        // ---------------------------------------------------------------- links, images, hreflang, sitemap

        /// <summary>Final status of every URL seen in the audit, whether reaching it took a redirect, and why it failed.</summary>
        private sealed class UrlStatus
        {
            public Dictionary<string, int> Code { get; } = new(StringComparer.Ordinal);
            public HashSet<string> Redirected { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Error { get; } = new(StringComparer.Ordinal);
        }

        private readonly record struct CheckResult(int Status, bool Redirected, string? Error);

        /// <summary>Hosts that answer HEAD wrongly (SharePoint returns 404 to HEAD for every page): use GET for them.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _headUnreliable = new(StringComparer.OrdinalIgnoreCase);

        // Throttling, anti-bot and auth answers say nothing about whether the target exists
        private static bool IsInconclusive(int s) => s is 401 or 403 or 429 or 999;
        private static bool InternalBroken(int s) => s == 0 || (s >= 400 && !IsInconclusive(s));
        private static bool ExternalBroken(int s) => s is 0 or 404 or 410 || (s >= 500 && s <= 599);
        private static bool Retryable(int s) => s is 0 or 429 or 502 or 503 or 504;

        /// <summary>
        /// Gets a status for every link, image, hreflang target (and, in a site crawl, sitemap URL) that was not crawled,
        /// re-checks anything that looks broken slowly, one at a time, then flags broken links and images.
        /// </summary>
        private async Task<UrlStatus> CheckLinksAsync(AuditResult result, AuditMode mode, CancellationToken ct)
        {
            var status = new UrlStatus();
            var known = status.Code;
            foreach (var p in result.Pages)
            {
                known[p.Url] = p.StatusCode;
                if (p.RedirectChain.Count > 0) status.Redirected.Add(p.Url);
                if (p.Error != null) status.Error[p.Url] = p.Error;
                if (!known.ContainsKey(p.FinalUrl)) known[p.FinalUrl] = p.StatusCode;
            }
            // A continued audit already knows the status of links checked in an earlier session; only clearly
            // working ones are reused, anything that looked broken is checked again
            foreach (var p in result.Pages)
            {
                foreach (var l in p.Links) if (l.StatusCode is int s1 && s1 is >= 200 and < 400) known.TryAdd(l.Url, s1);
                foreach (var i in p.Images) if (i.StatusCode is int s2 && s2 is >= 200 and < 400) known.TryAdd(i.Url, s2);
            }

            var referenced = result.Pages.SelectMany(p => p.Links.Where(l => l.IsInternal || _s.CheckExternalLinks).Select(l => l.Url))
                .Concat(result.Pages.SelectMany(p => p.Images.Where(i => i.IsInternal || _s.CheckExternalLinks).Select(i => i.Url)))
                .Concat(result.Pages.SelectMany(p => p.Hreflangs.Select(h => h.Url)))
                .Where(u => !known.ContainsKey(u)).Distinct().Take(MaxLinkChecks).ToList();
            // Uncrawled sitemap URLs are validated only in a site crawl, and only up to a limit, to keep the load reasonable
            var sitemapExtra = mode == AuditMode.CrawlSite
                ? _sitemapUrls.Where(u => !known.ContainsKey(u)).Except(referenced).Distinct().Take(MaxSitemapChecks).ToList()
                : new List<string>();
            var toCheck = referenced.Concat(sitemapExtra).ToList();

            if (toCheck.Count > 0)
            {
                Log?.Invoke($"Checking {toCheck.Count:N0} linked URL(s), images and sitemap entries that were not crawled...");
                int done = 0;
                // Same parallelism as the crawl: doubling it got real pages answered with 503 by throttling servers
                using var gate = new SemaphoreSlim(Math.Max(1, _s.Concurrency));
                var tasks = toCheck.Select(async url =>
                {
                    await _pause.WaitIfPausedAsync(ct);
                    await gate.WaitAsync(ct);
                    try
                    {
                        var r = await CheckStatusAsync(url, ct);
                        lock (status) Record(status, url, r);
                    }
                    finally
                    {
                        gate.Release();
                        int n = Interlocked.Increment(ref done);
                        if (n % 10 == 0 || n == toCheck.Count) Report("Checking links and images", n, toCheck.Count, url);
                    }
                });
                await Task.WhenAll(tasks);
            }

            await VerifyBrokenAsync(result, status, ct);

            foreach (var page in result.Pages)
            {
                foreach (var link in page.Links)
                {
                    if (known.TryGetValue(link.Url, out var st)) link.StatusCode = st;
                    link.Error = status.Error.GetValueOrDefault(link.Url);
                }
                foreach (var img in page.Images)
                {
                    if (known.TryGetValue(img.Url, out var st)) img.StatusCode = st;
                    img.Error = status.Error.GetValueOrDefault(img.Url);
                }
                foreach (var h in page.Hreflangs)
                {
                    if (known.TryGetValue(h.Url, out var st)) h.StatusCode = st;
                    h.Error = status.Error.GetValueOrDefault(h.Url);
                }

                var brokenInternal = page.Links.Where(l => l.IsInternal && l.StatusCode is int s && InternalBroken(s)).ToList();
                var brokenExternal = page.Links.Where(l => !l.IsInternal && l.StatusCode is int s && ExternalBroken(s)).ToList();
                if (brokenInternal.Count > 0)
                    page.Issues.Add(BrokenIssue(IssueCatalog.BrokenInternalLink, brokenInternal.Select(l => (l.Url, l.StatusCode, l.Error))));
                if (brokenExternal.Count > 0)
                    page.Issues.Add(BrokenIssue(IssueCatalog.BrokenExternalLink, brokenExternal.Select(l => (l.Url, l.StatusCode, l.Error))));

                var brokenImgInternal = page.Images.Where(i => i.IsInternal && i.StatusCode is int s && InternalBroken(s)).ToList();
                var brokenImgExternal = page.Images.Where(i => !i.IsInternal && i.StatusCode is int s && ExternalBroken(s)).ToList();
                if (brokenImgInternal.Count > 0)
                    page.Issues.Add(BrokenIssue(IssueCatalog.BrokenInternalImage, brokenImgInternal.Select(i => (i.Url, i.StatusCode, i.Error))));
                if (brokenImgExternal.Count > 0)
                    page.Issues.Add(BrokenIssue(IssueCatalog.BrokenExternalImage, brokenImgExternal.Select(i => (i.Url, i.StatusCode, i.Error))));
            }
            return status;
        }

        private static void Record(UrlStatus status, string url, CheckResult r)
        {
            status.Code[url] = r.Status;
            if (r.Redirected) status.Redirected.Add(url); else status.Redirected.Remove(url);
            if (r.Error != null) status.Error[url] = r.Error; else status.Error.Remove(url);
        }

        /// <summary>
        /// Before anything is reported as broken, ask again slowly, one request at a time: a busy or throttling
        /// server answers 503 or times out under load although the page is fine.
        /// </summary>
        private async Task VerifyBrokenAsync(AuditResult result, UrlStatus status, CancellationToken ct)
        {
            var internalUrls = new HashSet<string>(result.Pages.SelectMany(p => p.Links.Where(l => l.IsInternal).Select(l => l.Url)
                .Concat(p.Images.Where(i => i.IsInternal).Select(i => i.Url))), StringComparer.Ordinal);
            var crawled = new HashSet<string>(result.Pages.Select(p => p.Url), StringComparer.Ordinal);

            var suspects = status.Code
                .Where(kv => !crawled.Contains(kv.Key) &&
                             (internalUrls.Contains(kv.Key) || _sitemapUrls.Contains(kv.Key) ? InternalBroken(kv.Value) : ExternalBroken(kv.Value)) &&
                             // DNS and certificate failures are definite; re-asking will not change them
                             !(status.Error.GetValueOrDefault(kv.Key) is string e && (e.StartsWith("DNS") || e.StartsWith("SSL"))))
                .Select(kv => kv.Key)
                .Take(MaxVerifications)
                .ToList();
            if (suspects.Count == 0) return;

            Log?.Invoke($"Re-checking {suspects.Count:N0} URL(s) that looked broken, one at a time...");
            int fixedCount = 0, n = 0;
            foreach (var url in suspects)
            {
                ct.ThrowIfCancellationRequested();
                if (_pause.IsPaused) { Report("Paused", n, suspects.Count, ""); await _pause.WaitIfPausedAsync(ct); }
                Report("Confirming broken links", ++n, suspects.Count, url);
                await Task.Delay(VerifyDelayMs, ct);
                var r = await CheckStatusAsync(url, ct, forceGet: true, timeoutSeconds: Math.Max(30, _s.TimeoutSeconds));
                if (r.Status != status.Code[url] && !InternalBroken(r.Status)) fixedCount++;
                Record(status, url, r);
            }
            if (fixedCount > 0)
                Log?.Invoke($"{fixedCount:N0} of them answered normally on the second try (the server was busy or throttling), so they are not reported.");
        }

        private static string Why(int? s, string? e) => s == 0 ? (e ?? "failed") : $"HTTP {s}";

        private static string DescribeUrls(IEnumerable<(string Url, int? Status, string? Error)> items)
        {
            var list = items.ToList();
            return string.Join("; ", list.Take(3).Select(x => $"{x.Url} ({Why(x.Status, x.Error)})"))
                   + (list.Count > 3 ? $" and {list.Count - 3} more" : "");
        }

        /// <summary>A broken link/image issue: a short text summary plus every broken URL as a target.</summary>
        private static PageIssue BrokenIssue(string issueId, IEnumerable<(string Url, int? Status, string? Error)> items)
        {
            var list = items.ToList();
            return new PageIssue
            {
                IssueId = issueId,
                Detail = DescribeUrls(list),
                Targets = list.Select(x => new IssueTarget { Url = x.Url, Status = x.Status, Reason = Why(x.Status, x.Error) }).ToList()
            };
        }

        /// <summary>Hreflang targets must be live, final (not redirected), canonical, and link back to this page.</summary>
        private static void AddHreflangLinkIssues(AuditResult result, UrlStatus status)
        {
            var byUrl = new Dictionary<string, PageResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in result.Pages)
            {
                byUrl.TryAdd(p.Url.TrimEnd('/'), p);
                byUrl.TryAdd(p.FinalUrl.TrimEnd('/'), p);
            }

            // A page whose canonical points elsewhere is not indexed, so its hreflang set is not evaluated (as Semrush does)
            foreach (var page in result.Pages.Where(p => p.Hreflangs.Count > 0 && IsSelfCanonical(p)))
            {
                var problems = new List<string>();
                var targets = new List<IssueTarget>();
                void Problem(HreflangLink h, string reason)
                {
                    problems.Add($"{h.Lang} → {h.Url} {reason}");
                    targets.Add(new IssueTarget { Url = h.Url, Status = h.StatusCode, Reason = $"hreflang {h.Lang}: {reason}" });
                }
                foreach (var h in page.Hreflangs)
                {
                    if (PageAnalyzer.SameUrl(h.Url, page.FinalUrl)) continue; // self reference
                    if (h.StatusCode is int s && InternalBroken(s))
                    {
                        Problem(h, $"is broken ({(s == 0 ? h.Error ?? "failed" : "HTTP " + s)})");
                        continue;
                    }
                    if (status.Redirected.Contains(h.Url)) { Problem(h, "redirects"); continue; }
                    if (!byUrl.TryGetValue(h.Url.TrimEnd('/'), out var target) || target.StatusCode != 200 || !target.IsHtml) continue;
                    if (!IsSelfCanonical(target))
                        Problem(h, $"is not canonical (canonical is {target.Canonical})");
                    else if (!target.Hreflangs.Any(b => PageAnalyzer.SameUrl(b.Url, page.FinalUrl) || PageAnalyzer.SameUrl(b.Url, page.Url)))
                        Problem(h, "has no return link to this page");
                }
                if (problems.Count > 0)
                    page.Issues.Add(new PageIssue
                    {
                        IssueId = IssueCatalog.IncorrectHreflangLink,
                        Detail = string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? $" and {problems.Count - 4} more" : ""),
                        Targets = targets
                    });
            }
        }

        private static bool IsSelfCanonical(PageResult p) =>
            p.Canonical.Length == 0 || PageAnalyzer.SameUrl(p.Canonical, p.FinalUrl);

        /// <summary>
        /// The sitemap should list only live, indexable, canonical URLs that robots.txt allows.
        /// Crawled sitemap URLs get a page issue; uncrawled ones that were checked are counted in a site check.
        /// </summary>
        private void AddSitemapIssues(AuditResult result, RobotsTxt robots, UrlStatus status, AuditMode mode)
        {
            if (_sitemapUrls.Count == 0) return;
            var pagesByKey = new Dictionary<string, PageResult>();
            foreach (var p in result.Pages)
                if (Uri.TryCreate(p.Url, UriKind.Absolute, out var u)) pagesByKey.TryAdd(Key(u), p);

            int incorrect = 0, evaluated = 0;
            var examples = new List<string>();
            var distinct = _sitemapUrls.Distinct().ToList();
            foreach (var url in distinct)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                string? reason = null;
                if (pagesByKey.TryGetValue(Key(uri), out var page))
                {
                    evaluated++;
                    page.InSitemap = true;
                    if (page.StatusCode == 0) reason = "could not be fetched" + (page.Error != null ? $" ({page.Error})" : "");
                    else if (page.StatusCode >= 400) reason = $"returns HTTP {page.StatusCode}";
                    else if (page.RedirectChain.Count > 0) reason = $"redirects to {page.FinalUrl}";
                    else if (page.MetaRobots.Contains("noindex", StringComparison.OrdinalIgnoreCase) ||
                             page.Issues.Any(i => i.IssueId == IssueCatalog.NoIndex)) reason = "is noindex";
                    else if (page.IsHtml && !IsSelfCanonical(page))
                        reason = $"is not canonical (canonical is {page.Canonical})";
                    if (reason != null)
                        page.Issues.Add(new PageIssue { IssueId = IssueCatalog.SitemapIncorrectPage, Detail = "Listed in sitemap.xml but " + reason });
                }
                else if (mode == AuditMode.CrawlSite)
                {
                    if (!robots.IsAllowed(uri)) { evaluated++; reason = "is blocked by robots.txt"; }
                    else if (status.Code.TryGetValue(url, out var s))
                    {
                        evaluated++;
                        if (InternalBroken(s)) reason = s == 0 ? "could not be fetched" : $"returns HTTP {s}";
                        else if (status.Redirected.Contains(url)) reason = "redirects";
                    }
                }
                if (reason != null)
                {
                    incorrect++;
                    if (examples.Count < 3) examples.Add($"{url} {reason}");
                }
            }

            var scope = evaluated < distinct.Count ? $" (checked {evaluated:N0} of {distinct.Count:N0} sitemap URLs)" : "";
            result.SiteChecks.Add(new SiteCheck
            {
                Name = "Sitemap lists only valid pages",
                Passed = incorrect == 0,
                Severity = Severity.Error,
                Detail = incorrect == 0
                    ? $"All checked sitemap URLs are live, indexable and canonical{scope}"
                    : $"{incorrect:N0} incorrect sitemap URL(s){scope}, e.g. {string.Join("; ", examples)}"
            });
        }

        private async Task<CheckResult> CheckStatusAsync(string url, CancellationToken ct, bool forceGet = false, int? timeoutSeconds = null)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
            CheckResult r = default;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    // Back off before retrying a throttled / busy / timed-out request (Retry-After is honoured when sent)
                    var wait = r.Error?.StartsWith("retry-after:") == true && int.TryParse(r.Error[12..], out var ra)
                        ? Math.Min(ra, 30) * 1000 : attempt * 2500;
                    await Task.Delay(wait, ct);
                }
                r = await CheckOnceAsync(url, host, forceGet, timeoutSeconds ?? Math.Min(20, _s.TimeoutSeconds), ct);
                if (!Retryable(r.Status) || r.Error?.StartsWith("DNS") == true || r.Error?.StartsWith("SSL") == true) break;
            }
            if (r.Error?.StartsWith("retry-after:") == true) r = r with { Error = null };
            return r;
        }

        private async Task<CheckResult> CheckOnceAsync(string url, string host, bool forceGet, int timeoutSeconds, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                bool useGet = forceGet || _headUnreliable.ContainsKey(host);
                if (!useGet)
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    var h = await FollowAsync(head, cts.Token);
                    if (h.Status is not (405 or 403 or 501 or 400 or 404)) return h;
                }
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                var g = await FollowAsync(get, cts.Token);
                // HEAD said 4xx but GET works: this host mishandles HEAD, so skip HEAD for it from now on
                if (!useGet && g.Status is >= 200 and < 400) _headUnreliable.TryAdd(host, true);
                return g;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new CheckResult(0, false, Describe(ex)); }
        }

        private async Task<CheckResult> FollowAsync(HttpRequestMessage first, CancellationToken ct)
        {
            var request = first;
            for (int hop = 0; hop < 10; hop++)
            {
                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                int code = (int)resp.StatusCode;
                if (code is >= 300 and < 400 && resp.Headers.Location != null)
                {
                    var next = new Uri(request.RequestUri!, resp.Headers.Location);
                    request = new HttpRequestMessage(first.Method, next);
                    continue;
                }
                var retryAfter = resp.Headers.RetryAfter?.Delta is TimeSpan d ? $"retry-after:{(int)d.TotalSeconds}" : null;
                return new CheckResult(code, hop > 0, Retryable(code) ? retryAfter : null);
            }
            return new CheckResult(310, true, "too many redirects");
        }

        // ---------------------------------------------------------------- cross-page checks

        private static readonly HashSet<string> RedirectLevelIssues = new()
        {
            IssueCatalog.Redirected, IssueCatalog.RedirectChain, IssueCatalog.TemporaryRedirect, IssueCatalog.SlowPage,
            IssueCatalog.UrlTooLong, IssueCatalog.UrlUnderscores, IssueCatalog.UrlUppercase, IssueCatalog.NotHttps
        };

        /// <summary>
        /// A URL that redirects to a page crawled in its own right would repeat that page's content issues
        /// (and its links/images/hreflang). Keep only the redirect-level issues on it, as Semrush does.
        /// </summary>
        private static void DropContentOfRedirects(AuditResult result)
        {
            var direct = new HashSet<string>(result.Pages.Where(p => p.RedirectChain.Count == 0).Select(p => p.Url.TrimEnd('/')),
                                             StringComparer.OrdinalIgnoreCase);
            foreach (var p in result.Pages.Where(p => p.RedirectChain.Count > 0 && direct.Contains(p.FinalUrl.TrimEnd('/'))))
            {
                p.Issues.RemoveAll(i => !RedirectLevelIssues.Contains(i.IssueId));
                p.Links.Clear();
                p.Images.Clear();
                p.Hreflangs.Clear();
                p.InternalLinkCount = p.ExternalLinkCount = 0;
            }
        }

        private static void AddCrossPageIssues(AuditResult result)
        {
            // Avoid adding twice if called again after a cancel
            var crossIds = new[] { IssueCatalog.DuplicateTitle, IssueCatalog.DuplicateDescription, IssueCatalog.DuplicateContent };
            foreach (var p in result.Pages) p.Issues.RemoveAll(i => crossIds.Contains(i.IssueId));

            var indexable = result.Pages.Where(p => p.StatusCode == 200 && p.IsHtml && p.RedirectChain.Count == 0 &&
                                                   !p.MetaRobots.Contains("noindex", StringComparison.OrdinalIgnoreCase) &&
                                                   // Pages that name another URL as canonical are already consolidated, so they are not duplicates (as Semrush)
                                                   IsSelfCanonical(p)).ToList();
            void Flag(Func<PageResult, string> key, string issueId, string what)
            {
                foreach (var g in indexable.Where(p => key(p).Length > 0).GroupBy(p => key(p).ToLowerInvariant()).Where(g => g.Count() > 1))
                {
                    var members = g.ToList();
                    foreach (var p in members)
                    {
                        var others = members.Where(o => o != p).Select(o => o.Url).ToList();
                        p.Issues.Add(new PageIssue
                        {
                            IssueId = issueId,
                            Detail = $"Same {what} as {others.Count} other page(s): {string.Join(", ", others.Take(2))}{(others.Count > 2 ? "…" : "")}"
                        });
                    }
                }
            }
            Flag(p => p.Title, IssueCatalog.DuplicateTitle, "title");
            Flag(p => p.MetaDescription, IssueCatalog.DuplicateDescription, "description");
            Flag(p => p.ContentHash, IssueCatalog.DuplicateContent, "content");
        }

        // ---------------------------------------------------------------- site checks

        private async Task RunSiteChecksAsync(AuditResult result, RobotsTxt robots, CancellationToken ct)
        {
            Report("Site-wide checks", 0, 0, "");
            var start = new Uri(result.Pages.FirstOrDefault()?.FinalUrl is { Length: > 0 } f ? f : result.StartUrl);
            var origin = start.GetLeftPart(UriPartial.Authority);
            var checks = result.SiteChecks;

            // robots.txt
            var robotsFetch = await TryFetchAsync(origin + "/robots.txt", true, ct);
            bool robotsOk = robotsFetch?.StatusCode == 200;
            checks.Add(new SiteCheck { Name = "robots.txt file", Passed = robotsOk, Severity = Severity.Warning,
                Detail = robotsOk ? $"Found at {origin}/robots.txt" : $"Not found (HTTP {robotsFetch?.StatusCode.ToString() ?? "failed"})" });
            bool homeBlocked = robotsOk && !robots.IsAllowed(start);
            checks.Add(new SiteCheck { Name = "Homepage allowed by robots.txt", Passed = !homeBlocked, Severity = Severity.Error,
                Detail = homeBlocked ? "robots.txt disallows the start page for all crawlers" : "Crawling allowed" });

            // Sitemap
            var sitemapUrl = robots.Sitemaps.FirstOrDefault();
            FetchResult? sm = null;
            if (sitemapUrl == null)
            {
                foreach (var candidate in new[] { "/sitemap.xml", "/sitemap_index.xml", "/sitemap-index.xml" })
                {
                    sm = await TryFetchAsync(origin + candidate, false, ct);
                    if (sm?.StatusCode == 200) { sitemapUrl = origin + candidate; break; }
                }
            }
            else sm = await TryFetchAsync(sitemapUrl, false, ct);
            bool sitemapOk = sm?.StatusCode == 200;
            checks.Add(new SiteCheck { Name = "XML sitemap", Passed = sitemapOk, Severity = Severity.Warning,
                Detail = sitemapOk ? $"Found: {sitemapUrl}" + (robots.Sitemaps.Count == 0 ? " (not declared in robots.txt)" : "")
                                   : "No sitemap.xml found and none declared in robots.txt" });

            // HTTPS, HTTP→HTTPS redirect and HSTS
            var httpsHome = await TryFetchAsync($"https://{start.Host}/", false, ct);
            bool httpsOk = httpsHome != null && httpsHome.StatusCode is > 0 and < 400;
            checks.Add(new SiteCheck { Name = "HTTPS available", Passed = httpsOk, Severity = Severity.Error,
                Detail = httpsOk ? "Site answers over HTTPS" : "Site does not answer over HTTPS" });
            if (httpsOk)
            {
                var httpHome = await TryFetchAsync($"http://{start.Host}/", false, ct);
                bool redirects = httpHome != null && httpHome.FinalUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                checks.Add(new SiteCheck { Name = "HTTP redirects to HTTPS", Passed = redirects, Severity = Severity.Error,
                    Detail = redirects ? $"http:// → {httpHome!.FinalUrl}" : "http:// version does not redirect to https://" });
                checks.Add(new SiteCheck { Name = "HSTS (Strict-Transport-Security)", Passed = httpsHome!.Hsts, Severity = Severity.Notice,
                    Detail = httpsHome.Hsts ? "Header present" : "Header missing" });
                checks.Add(await CheckCertificateAsync(start.Host, ct));
            }

            // www / non-www consolidation
            var bare = StripWww(start.Host);
            if (bare.Count(c => c == '.') >= 1)
            {
                var a = await TryFetchAsync($"{start.Scheme}://{bare}/", false, ct);
                var b = await TryFetchAsync($"{start.Scheme}://www.{bare}/", false, ct);
                if (a != null && b != null && a.StatusCode is > 0 and < 400 && b.StatusCode is > 0 and < 400)
                {
                    bool same = new Uri(a.FinalUrl).Host.Equals(new Uri(b.FinalUrl).Host, StringComparison.OrdinalIgnoreCase);
                    checks.Add(new SiteCheck { Name = "www and non-www consolidated", Passed = same, Severity = Severity.Warning,
                        Detail = same ? $"Both resolve to {new Uri(a.FinalUrl).Host}" : "www and non-www both serve pages without redirecting to one version" });
                }
            }

            // Soft 404: a made-up URL must return 404/410
            var probe = await TryFetchAsync($"{origin}/seo-audit-404-check-{Guid.NewGuid():N}", false, ct);
            if (probe != null)
            {
                bool proper = probe.StatusCode is 404 or 410;
                checks.Add(new SiteCheck { Name = "Missing pages return 404", Passed = proper, Severity = Severity.Warning,
                    Detail = proper ? $"HTTP {probe.StatusCode}" : $"A non-existent URL returned HTTP {probe.StatusCode} (soft 404)" });
            }

            // Average response time
            var ok = result.Pages.Where(p => p.StatusCode == 200).ToList();
            if (ok.Count > 0)
            {
                var avg = ok.Average(p => p.ResponseTimeMs);
                checks.Add(new SiteCheck { Name = "Average response time", Passed = avg <= 1000, Severity = Severity.Notice,
                    Detail = $"{avg:N0} ms over {ok.Count} page(s)" });
            }
        }

        private async Task<SiteCheck> CheckCertificateAsync(string host, CancellationToken ct)
        {
            X509Certificate2? cert = null;
            SslPolicyErrors errors = SslPolicyErrors.None;
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, c, _, e) =>
                        {
                            if (c != null) cert = new X509Certificate2(c);
                            errors = e;
                            return true;
                        }
                    }
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                using var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"https://{host}/"), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (cert == null)
            {
                return new SiteCheck { Name = "SSL certificate", Passed = false, Severity = Severity.Error, Detail = Describe(ex) };
            }
            catch { /* certificate already captured */ }

            if (cert == null)
                return new SiteCheck { Name = "SSL certificate", Passed = false, Severity = Severity.Error, Detail = "No certificate received" };

            var days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
            var issuer = cert.GetNameInfo(X509NameType.SimpleName, true);
            if (errors != SslPolicyErrors.None)
                return new SiteCheck { Name = "SSL certificate", Passed = false, Severity = Severity.Error, Detail = $"Invalid certificate: {errors} (issuer {issuer})" };
            if (days < 0)
                return new SiteCheck { Name = "SSL certificate", Passed = false, Severity = Severity.Error, Detail = $"Expired on {cert.NotAfter:yyyy-MM-dd}" };
            if (days < 30)
                return new SiteCheck { Name = "SSL certificate", Passed = false, Severity = Severity.Warning, Detail = $"Expires in {days} day(s) on {cert.NotAfter:yyyy-MM-dd} (issuer {issuer})" };
            return new SiteCheck { Name = "SSL certificate", Passed = true, Severity = Severity.Error, Detail = $"Valid until {cert.NotAfter:yyyy-MM-dd} ({days} days, issuer {issuer})" };
        }

        // ---------------------------------------------------------------- robots / sitemap

        private async Task<RobotsTxt> LoadRobotsAsync(Uri start, CancellationToken ct)
        {
            var f = await TryFetchAsync(start.GetLeftPart(UriPartial.Authority) + "/robots.txt", true, ct);
            if (f?.StatusCode == 200 && f.Body != null)
            {
                var robots = RobotsTxt.Parse(Encoding.UTF8.GetString(f.Body));
                // The start URL may redirect to another host (http→https, www); follow it for the root host
                if (Uri.TryCreate(f.FinalUrl, UriKind.Absolute, out var fu)) _siteHosts.Add(StripWww(fu.Host));
                return robots;
            }
            return RobotsTxt.Empty;
        }

        private async Task<List<string>> LoadSitemapUrlsAsync(Uri start, RobotsTxt robots, int limit, CancellationToken ct)
        {
            var origin = start.GetLeftPart(UriPartial.Authority);
            var toVisit = new Queue<string>(robots.Sitemaps.Count > 0 ? robots.Sitemaps : new List<string> { origin + "/sitemap.xml" });
            var visited = new HashSet<string>();
            var urls = new List<string>();
            while (toVisit.Count > 0 && urls.Count < limit && visited.Count < 20)
            {
                var sm = toVisit.Dequeue();
                if (!visited.Add(sm)) continue;
                var f = await TryFetchAsync(sm, true, ct);
                if (f?.StatusCode != 200 || f.Body == null) continue;
                string xml;
                try
                {
                    var bytes = f.Body;
                    if (sm.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
                        bytes = Decompress(bytes, "gzip");
                    xml = Encoding.UTF8.GetString(bytes);
                }
                catch { continue; }

                bool isIndex = xml.Contains("<sitemapindex", StringComparison.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(xml, @"<loc>\s*(.*?)\s*</loc>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    var loc = WebUtility.HtmlDecode(m.Groups[1].Value.Replace("<![CDATA[", "").Replace("]]>", "")).Trim();
                    if (isIndex) toVisit.Enqueue(loc);
                    else if (Uri.TryCreate(loc, UriKind.Absolute, out var lu) && IsInternal(lu)) urls.Add(lu.AbsoluteUri);
                    if (urls.Count >= limit) break;
                }
            }
            return urls;
        }

        // ---------------------------------------------------------------- HTTP

        private sealed class FetchResult
        {
            public int StatusCode;
            public string FinalUrl = "";
            public List<string> Chain = new();
            public bool TemporaryRedirect;
            public long ElapsedMs;
            public string ContentType = "";
            public string ContentEncoding = "";
            public string? Charset;
            public long? ContentLength;
            public byte[]? Body;
            public bool Hsts;
            public bool XRobotsNoIndex;
        }

        private async Task<FetchResult?> TryFetchAsync(string url, bool readBody, CancellationToken ct)
        {
            try { return await FetchAsync(url, readBody, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        private async Task<FetchResult> FetchAsync(string url, bool readBody, CancellationToken ct)
        {
            var result = new FetchResult();
            var current = new Uri(url);
            for (int hop = 0; ; hop++)
            {
                var sw = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                int code = (int)resp.StatusCode;

                if (code is >= 300 and < 400 && resp.Headers.Location != null && hop < 10)
                {
                    result.Chain.Add($"{current.AbsoluteUri} ({code})");
                    if (code is 302 or 303 or 307) result.TemporaryRedirect = true;
                    current = new Uri(current, resp.Headers.Location);
                    continue;
                }

                result.StatusCode = code;
                result.FinalUrl = current.AbsoluteUri;
                result.ContentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                result.Charset = resp.Content.Headers.ContentType?.CharSet;
                result.ContentEncoding = string.Join(",", resp.Content.Headers.ContentEncoding);
                result.ContentLength = resp.Content.Headers.ContentLength;
                result.Hsts = resp.Headers.Contains("Strict-Transport-Security");
                result.XRobotsNoIndex = resp.Headers.TryGetValues("X-Robots-Tag", out var xr) &&
                                        xr.Any(v => v.Contains("noindex", StringComparison.OrdinalIgnoreCase));

                bool wantBody = readBody || result.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase);
                if (wantBody && code is >= 200 and < 300)
                {
                    var raw = await ReadLimitedAsync(resp.Content, MaxHtmlBytes, ct);
                    result.Body = Decompress(raw, result.ContentEncoding);
                }
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }
        }

        private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
        {
            await using var stream = await content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > limit) break;
            }
            return ms.ToArray();
        }

        private static byte[] Decompress(byte[] data, string encoding)
        {
            if (string.IsNullOrEmpty(encoding) || data.Length == 0) return data;
            var enc = encoding.Split(',').Last().Trim().ToLowerInvariant();
            try
            {
                using var input = new MemoryStream(data);
                using Stream decoder = enc switch
                {
                    "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    "deflate" => data.Length > 1 && data[0] == 0x78 ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress),
                    _ => input
                };
                if (decoder == input) return data;
                using var output = new MemoryStream();
                decoder.CopyTo(output);
                return output.ToArray();
            }
            catch { return data; }
        }

        private static string DecodeHtml(byte[] body, string? charset)
        {
            Encoding enc = Encoding.UTF8;
            if (string.IsNullOrEmpty(charset))
            {
                // Look for <meta charset> in the first bytes
                var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048));
                var m = Regex.Match(head, @"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) charset = m.Groups[1].Value;
            }
            if (!string.IsNullOrEmpty(charset))
            {
                try { enc = Encoding.GetEncoding(charset.Trim('"')); } catch { enc = Encoding.UTF8; }
            }
            return enc.GetString(body);
        }

        // ---------------------------------------------------------------- scoring

        private static void ComputeScores(AuditResult result)
        {
            foreach (var p in result.Pages)
            {
                var ids = p.Issues.Select(i => i.IssueId).Distinct().Select(IssueCatalog.Get).ToList();
                int penalty = ids.Count(d => d.Severity == Severity.Error) * 12 + ids.Count(d => d.Severity == Severity.Warning) * 4;
                p.Score = Math.Clamp(100 - penalty, 0, 100);
            }
            double avg = result.Pages.Count == 0 ? 0 : result.Pages.Average(p => p.Score);
            int sitePenalty = result.SiteChecks.Where(c => !c.Passed).Sum(c => c.Severity switch
            {
                Severity.Error => 6,
                Severity.Warning => 2,
                _ => 0
            });
            result.SiteHealth = (int)Math.Clamp(Math.Round(avg - sitePenalty), 0, 100);
        }

        // ---------------------------------------------------------------- helpers

        private bool IsInternal(Uri uri)
        {
            var host = StripWww(uri.Host);
            if (_siteHosts.Contains(host)) return true;
            return _s.IncludeSubdomains && host.EndsWith("." + _rootHost, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripWww(string host) =>
            host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

        private static string Key(Uri uri) =>
            $"{uri.Scheme}://{StripWww(uri.Host).ToLowerInvariant()}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}";

        private void Report(string phase, int done, int total, string url) =>
            Progress?.Invoke(new AuditProgress { Phase = phase, Done = done, Total = total, CurrentUrl = url });

        /// <summary>
        /// Plain reason for a failed request. Starts with "DNS", "SSL", "Timed out" or "Connection" so the
        /// link checker can tell definite failures (DNS, certificate) from ones worth retrying.
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex is TaskCanceledException or TimeoutException) return "Timed out";
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;

            if (inner is System.Net.Sockets.SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or
                        System.Net.Sockets.SocketError.TryAgain => "DNS: host name could not be resolved",
                    System.Net.Sockets.SocketError.ConnectionRefused => "Connection refused",
                    System.Net.Sockets.SocketError.TimedOut => "Timed out (connection)",
                    System.Net.Sockets.SocketError.ConnectionReset => "Connection reset by the server",
                    _ => "Connection failed: " + se.Message
                };
            }
            if (inner is System.Security.Authentication.AuthenticationException || ex.ToString().Contains("RemoteCertificate"))
            {
                var msg = ex.ToString();
                if (msg.Contains("NotTimeValid", StringComparison.OrdinalIgnoreCase) || msg.Contains("expired", StringComparison.OrdinalIgnoreCase))
                    return "SSL: certificate expired";
                if (msg.Contains("RemoteCertificateNameMismatch", StringComparison.OrdinalIgnoreCase))
                    return "SSL: certificate is for a different host name";
                if (msg.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase) || msg.Contains("PartialChain", StringComparison.OrdinalIgnoreCase))
                    return "SSL: certificate not trusted";
                return "SSL: " + inner.Message;
            }
            if (inner is IOException && inner.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
                return "Connection reset by the server";
            return inner == ex ? ex.Message : $"{ex.Message} ({inner.Message})";
        }

        // 10/8, 172.16/12, 192.168/16, 127/8, 100.64/10 (CGNAT), IPv6 unique-local and link-local
        private static bool IsPrivate(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        /// <summary>
        /// When the site resolves to an internal address (split DNS inside an organisation), the audit sees the
        /// internal servers, not what the public and search engines see. Say so in the report.
        /// </summary>
        private async Task CheckPublicAddressAsync(AuditResult result, string host, CancellationToken ct)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                var priv = addresses.Where(IsPrivate).ToList();
                if (addresses.Length == 0) return;
                bool isPrivate = priv.Count == addresses.Length;
                result.SiteChecks.Add(new SiteCheck
                {
                    Name = "Audited through the public internet",
                    Passed = !isPrivate,
                    Severity = Severity.Notice,
                    Detail = isPrivate
                        ? $"{host} resolves to {priv[0]}, a private network address. This computer reaches the site through an internal network, so status codes, speed, certificates and external links may differ from what the public and Google see. For a public view, run the audit from outside that network."
                        : $"{host} resolves to {string.Join(", ", addresses.Take(2))}"
                });
                if (isPrivate)
                    Log?.Invoke($"Note: {host} resolves to the private address {priv[0]} — the audit sees the internal network's view of the site.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* DNS failure is reported by the crawl itself */ }
        }
    }
}
