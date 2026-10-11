using System.Text.Json.Serialization;

namespace SEOAuditStudio.Models
{
    public enum Severity
    {
        Error = 0,
        Warning = 1,
        Notice = 2
    }

    public enum AuditMode
    {
        /// <summary>Follow internal links from the start URL, up to MaxPages / MaxDepth.</summary>
        CrawlSite = 0,
        /// <summary>Audit exactly the URLs supplied (pasted, CSV or Excel) without following links.</summary>
        UrlList = 1
    }

    /// <summary>A kind of problem the auditor can report (see IssueCatalog).</summary>
    public sealed class IssueDefinition
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public Severity Severity { get; init; }
        public string Category { get; init; } = "";
        public string Description { get; init; } = "";
        public string HowToFix { get; init; } = "";
    }

    /// <summary>One occurrence of an issue on one page.</summary>
    public sealed class PageIssue
    {
        public string IssueId { get; set; } = "";
        public string Detail { get; set; } = "";
        /// <summary>The URLs the issue is about (each broken link, image or hreflang target), so they can be opened and copied.</summary>
        public List<IssueTarget> Targets { get; set; } = new();
    }

    public sealed class IssueTarget
    {
        public string Url { get; set; } = "";
        public int? Status { get; set; }
        /// <summary>"HTTP 404", "SSL: certificate expired", "redirects", "no return link"...</summary>
        public string Reason { get; set; } = "";
    }

    public sealed class LinkInfo
    {
        public string Url { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsInternal { get; set; }
        public bool NoFollow { get; set; }
        /// <summary>Where the link sits on the page: Menu, Header, Footer, Breadcrumb, Sidebar or Content ("" in audits made before this was recorded).</summary>
        public string Location { get; set; } = "";
        public int? StatusCode { get; set; }
        /// <summary>Why the request failed when StatusCode is 0 (DNS, SSL, timeout...).</summary>
        public string? Error { get; set; }
    }

    public sealed class ImageInfo
    {
        public string Url { get; set; } = "";
        public string Alt { get; set; } = "";
        public bool HasAlt { get; set; }
        public bool IsInternal { get; set; }
        public int? StatusCode { get; set; }
        /// <summary>Why the request failed when StatusCode is 0 (DNS, SSL, timeout...).</summary>
        public string? Error { get; set; }
    }

    /// <summary>A &lt;link rel="alternate" hreflang="..."&gt; declared by a page.</summary>
    public sealed class HreflangLink
    {
        public string Lang { get; set; } = "";
        public string Url { get; set; } = "";
        public int? StatusCode { get; set; }
        /// <summary>Why the request failed when StatusCode is 0 (DNS, SSL, timeout...).</summary>
        public string? Error { get; set; }
    }

    public sealed class KeywordStat
    {
        public string Keyword { get; set; } = "";
        public int Count { get; set; }
        public double Density { get; set; }
    }

    public sealed class PageResult
    {
        public string Url { get; set; } = "";
        public string FinalUrl { get; set; } = "";
        public int Depth { get; set; }
        public int StatusCode { get; set; }
        public string? Error { get; set; }
        public List<string> RedirectChain { get; set; } = new();
        public long ResponseTimeMs { get; set; }
        public long SizeBytes { get; set; }
        public string ContentType { get; set; } = "";
        public string ContentEncoding { get; set; } = "";

        public string Title { get; set; } = "";
        public string MetaDescription { get; set; } = "";
        public string MetaRobots { get; set; } = "";
        public string Canonical { get; set; } = "";
        public string Lang { get; set; } = "";
        public List<string> H1 { get; set; } = new();
        public List<string> H2 { get; set; } = new();
        public int WordCount { get; set; }
        public double TextToHtmlRatio { get; set; }
        public int ImageCount { get; set; }
        public int ImagesMissingAlt { get; set; }
        public int InternalLinkCount { get; set; }
        public int ExternalLinkCount { get; set; }
        public bool HasViewport { get; set; }
        public bool HasOpenGraph { get; set; }
        public bool HasStructuredData { get; set; }
        public bool HasHreflang { get; set; }
        /// <summary>Hash of the normalised visible text, used to find duplicate content.</summary>
        public string ContentHash { get; set; } = "";
        public bool HasHsts { get; set; }
        public List<LinkInfo> Links { get; set; } = new();
        public List<ImageInfo> Images { get; set; } = new();
        public List<HreflangLink> Hreflangs { get; set; } = new();
        /// <summary>The URL is listed in the site's XML sitemap.</summary>
        public bool InSitemap { get; set; }
        /// <summary>How the crawler first reached the page: Start URL, URL list, Sitemap or Link.</summary>
        public string FoundVia { get; set; } = "";
        /// <summary>For FoundVia = Link: the page whose link the crawler followed.</summary>
        public string FoundFrom { get; set; } = "";
        /// <summary>How visitors reach the page (sitemap, navigation, links, click path); computed when the audit is shown.</summary>
        [JsonIgnore] public PageOrigin? Origin { get; set; }
        [JsonIgnore] public string OriginSummary => Origin?.Summary ?? "";
        public List<KeywordStat> TopKeywords { get; set; } = new();
        public List<PageIssue> Issues { get; set; } = new();

        /// <summary>0-100: 100 minus penalties for the errors and warnings on this page.</summary>
        public int Score { get; set; }

        [JsonIgnore] public int ErrorCount => CountBy(Severity.Error);
        [JsonIgnore] public int WarningCount => CountBy(Severity.Warning);
        [JsonIgnore] public int NoticeCount => CountBy(Severity.Notice);
        [JsonIgnore] public string StatusText => Error != null && StatusCode == 0 ? "Failed" : StatusCode.ToString();
        [JsonIgnore] public string SizeText => SizeBytes >= 1024 * 1024 ? $"{SizeBytes / 1048576.0:0.0} MB" : $"{SizeBytes / 1024.0:0.#} KB";
        [JsonIgnore] public string H1Text => string.Join(" | ", H1);
        [JsonIgnore] public bool IsHtml => ContentType.Contains("html", StringComparison.OrdinalIgnoreCase);

        private int CountBy(Severity s) => Issues.Count(i => IssueCatalog.Get(i.IssueId).Severity == s);
    }

    /// <summary>Checks that apply to the whole site rather than a page (robots.txt, sitemap, SSL...).</summary>
    public sealed class SiteCheck
    {
        public string Name { get; set; } = "";
        public bool Passed { get; set; }
        public Severity Severity { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>Issue type plus the pages it was found on; what the Issues view and reports list.</summary>
    public sealed class IssueSummary
    {
        public IssueDefinition Definition { get; set; } = new();
        public List<PageIssueRef> Pages { get; set; } = new();
        public int Count => Pages.Count;
        /// <summary>Change against the previous audit of the same site (null when there is none).</summary>
        public int? Delta { get; set; }
        public string DeltaText => Delta is null or 0 ? "" : Delta > 0 ? $"+{Delta}" : Delta.ToString()!;
    }

    public sealed class PageIssueRef
    {
        public string Url { get; set; } = "";
        public string Detail { get; set; } = "";
        public List<IssueTarget> Targets { get; set; } = new();
        /// <summary>Crawled pages that link to this URL (Url = linking page, Reason = anchor text).</summary>
        public List<IssueTarget> LinkedFrom { get; set; } = new();
        /// <summary>Shown when nothing links to the page: how it entered the audit.</summary>
        public string FoundVia { get; set; } = "";
    }

    public sealed class AuditResult
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string StartUrl { get; set; } = "";
        public AuditMode Mode { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public bool Cancelled { get; set; }
        public List<PageResult> Pages { get; set; } = new();
        public List<SiteCheck> SiteChecks { get; set; } = new();
        public List<PageSpeedResult> PageSpeed { get; set; } = new();
        public int SiteHealth { get; set; }

        /// <summary>The URLs a URL-list audit was asked to check, so a stopped list audit can be continued.</summary>
        public List<string> InputUrls { get; set; } = new();
        /// <summary>Time spent actually auditing (pauses and the gap before a Continue are not counted).</summary>
        public double ActiveSeconds { get; set; }
        /// <summary>The page limit stopped the crawl while there were still URLs to visit.</summary>
        public bool HitPageLimit { get; set; }

        [JsonIgnore] public TimeSpan Duration => ActiveSeconds > 0 ? TimeSpan.FromSeconds(ActiveSeconds) : FinishedAt - StartedAt;
        /// <summary>
        /// Continue can add pages: any site crawl (it picks up the links found but not yet visited), or a URL-list
        /// audit that has URLs left. Audits saved before Continue existed qualify too.
        /// </summary>
        [JsonIgnore] public bool CanContinue => Mode == AuditMode.CrawlSite || InputUrls.Count > Pages.Count;
        [JsonIgnore] public int Errors => Pages.Sum(p => p.ErrorCount) + SiteChecks.Count(c => !c.Passed && c.Severity == Severity.Error);
        [JsonIgnore] public int Warnings => Pages.Sum(p => p.WarningCount) + SiteChecks.Count(c => !c.Passed && c.Severity == Severity.Warning);
        [JsonIgnore] public int Notices => Pages.Sum(p => p.NoticeCount) + SiteChecks.Count(c => !c.Passed && c.Severity == Severity.Notice);

        public List<IssueSummary> BuildIssueSummaries()
        {
            var inlinks = BuildInlinks();
            return Pages
                .SelectMany(p => p.Issues.Select(i => (Page: p, Issue: i)))
                .GroupBy(x => x.Issue.IssueId)
                .Select(g => new IssueSummary
                {
                    Definition = IssueCatalog.Get(g.Key),
                    Pages = g.Select(x => new PageIssueRef
                    {
                        Url = x.Page.Url,
                        Detail = x.Issue.Detail,
                        Targets = x.Issue.Targets,
                        LinkedFrom = InlinkIssueIds.Contains(g.Key) ? InlinksFor(x.Page, inlinks) : new(),
                        FoundVia = InlinkIssueIds.Contains(g.Key) ? FoundVia(x.Page) : ""
                    }).ToList()
                })
                .OrderBy(s => s.Definition.Severity)
                .ThenByDescending(s => s.Count)
                .ToList();
        }

        /// <summary>Issues about a page's own URL (error status, redirects): the report shows which pages link to it.</summary>
        public static readonly HashSet<string> InlinkIssueIds = new()
        {
            IssueCatalog.Http4xx, IssueCatalog.Http5xx, IssueCatalog.FetchFailed,
            IssueCatalog.RedirectChain, IssueCatalog.TemporaryRedirect, IssueCatalog.Redirected
        };

        /// <summary>Comparison key for URLs: case-insensitive scheme and host, no trailing slash, no fragment.</summary>
        public static string UrlKey(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u)
                ? $"{u.Scheme}://{u.Host.ToLowerInvariant()}{(u.IsDefaultPort ? "" : ":" + u.Port)}{u.AbsolutePath.TrimEnd('/')}{u.Query}"
                : url.TrimEnd('/');

        /// <summary>
        /// Every internal link target → the crawled pages that link to it (with the anchor text).
        /// Built from the saved links, so it also works for audits saved before this existed.
        /// </summary>
        public Dictionary<string, List<IssueTarget>> BuildInlinks()
        {
            var map = new Dictionary<string, List<IssueTarget>>(StringComparer.Ordinal);
            foreach (var p in Pages)
            {
                foreach (var link in p.Links.Where(l => l.IsInternal))
                {
                    var key = UrlKey(link.Url);
                    if (!map.TryGetValue(key, out var list)) map[key] = list = new List<IssueTarget>();
                    if (!list.Any(t => t.Url == p.Url))
                        list.Add(new IssueTarget { Url = p.Url, Reason = link.Text.Length > 0 ? link.Text : "(no anchor text)" });
                }
            }
            return map;
        }

        public static List<IssueTarget> InlinksFor(PageResult page, Dictionary<string, List<IssueTarget>> inlinks)
        {
            var result = new List<IssueTarget>();
            foreach (var key in new[] { UrlKey(page.Url), UrlKey(page.FinalUrl) }.Distinct())
                if (inlinks.TryGetValue(key, out var list))
                    result.AddRange(list.Where(t => t.Url != page.Url && !result.Any(r => r.Url == t.Url)));
            return result;
        }

        /// <summary>How a page with no incoming links got into the audit.</summary>
        public string FoundVia(PageResult page) =>
            page.InSitemap ? "Only in sitemap.xml (no crawled page links to it)"
            : page.Depth == 0 ? (Mode == AuditMode.UrlList ? "From your URL list" : "Start URL")
            : "No crawled page links to it";
    }

    public sealed class PageSpeedResult
    {
        public string Url { get; set; } = "";
        public string Strategy { get; set; } = "mobile";
        public DateTime TestedAt { get; set; }
        public int? PerformanceScore { get; set; }
        public int? AccessibilityScore { get; set; }
        public int? BestPracticesScore { get; set; }
        public int? SeoScore { get; set; }
        public string Fcp { get; set; } = "";
        public string Lcp { get; set; } = "";
        public string Cls { get; set; } = "";
        public string Tbt { get; set; } = "";
        public string SpeedIndex { get; set; } = "";
        /// <summary>Field data (real Chrome users) when Google has it for the URL.</summary>
        public string FieldLcp { get; set; } = "";
        public string FieldInp { get; set; } = "";
        public string FieldCls { get; set; } = "";
        public string FieldCategory { get; set; } = "";
        public List<string> Opportunities { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class DomainMetrics
    {
        public string Domain { get; set; } = "";
        public string Database { get; set; } = "us";
        public long? Rank { get; set; }
        public long? OrganicKeywords { get; set; }
        public long? OrganicTraffic { get; set; }
        public decimal? OrganicCost { get; set; }
        public long? PaidKeywords { get; set; }
        public long? PaidTraffic { get; set; }
        public int? AuthorityScore { get; set; }
        public long? Backlinks { get; set; }
        public long? ReferringDomains { get; set; }
        public List<OrganicKeyword> TopKeywords { get; set; } = new();
        public List<string> Competitors { get; set; } = new();
    }

    public sealed class OrganicKeyword
    {
        public string Keyword { get; set; } = "";
        public int Position { get; set; }
        public long SearchVolume { get; set; }
        public decimal Cpc { get; set; }
        public string Url { get; set; } = "";
        public double TrafficPercent { get; set; }
    }

    public sealed class AuditSettings
    {
        public int MaxPages { get; set; } = 500;
        public int MaxDepth { get; set; } = 10;
        public int Concurrency { get; set; } = 6;
        public int TimeoutSeconds { get; set; } = 30;
        public int CrawlDelayMs { get; set; } = 0;
        public bool RespectRobotsTxt { get; set; } = true;
        public bool CheckExternalLinks { get; set; } = true;
        public bool IncludeSubdomains { get; set; }
        public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SEOAuditStudio/1.0 (+site audit)";
        public int SlowPageMs { get; set; } = 3000;
        public int LargePageKb { get; set; } = 2048;
        public int MinWordCount { get; set; } = 200;
        public int TitleMin { get; set; } = 30;
        public int TitleMax { get; set; } = 60;
        public int DescriptionMin { get; set; } = 70;
        public int DescriptionMax { get; set; } = 160;
        public string PageSpeedApiKey { get; set; } = "";
        public string SemrushApiKey { get; set; } = "";
        public string SemrushDatabase { get; set; } = "us";
    }
}
