namespace SEOAuditStudio.Models
{
    public enum OriginKind
    {
        StartPage,
        Navigation,
        ContentOnly,
        LinkedUnknownPlace,
        SitemapOnly,
        Orphan
    }

    /// <summary>A crawled page that links to the page in question.</summary>
    public sealed class Inlink
    {
        public string Url { get; init; } = "";
        public string Anchor { get; init; } = "";
        /// <summary>Menu, Header, Footer, Breadcrumb, Sidebar, Content, or "Unknown" for audits made before this was recorded.</summary>
        public string Location { get; init; } = "";
    }

    /// <summary>One click on the shortest path from the start page.</summary>
    public sealed class PathStep
    {
        public int Number { get; init; }
        public string Url { get; init; } = "";
        /// <summary>How this page is reached from the previous step: "Menu link "About"", "Content link "Partners"", "Redirect"...</summary>
        public string Via { get; init; } = "";
    }

    /// <summary>How visitors and search engines reach a page; helps decide whether an old page can go.</summary>
    public sealed class PageOrigin
    {
        public bool InSitemap { get; init; }
        public bool InNavigation { get; init; }
        public bool LocationsKnown { get; init; }
        public int NavigationLinks { get; init; }
        public int ContentLinks { get; init; }
        public List<Inlink> Inlinks { get; init; } = new();
        public List<PathStep> ClickPath { get; init; } = new();
        /// <summary>Clicks from the start page, or null when it cannot be reached by clicking.</summary>
        public int? ClickDepth { get; init; }
        public OriginKind Kind { get; init; }
        /// <summary>Short label for the Pages grid.</summary>
        public string Summary { get; init; } = "";
        /// <summary>Plain-language explanation for the page detail panel.</summary>
        public string Explanation { get; init; } = "";
        /// <summary>How the crawler itself first found the page.</summary>
        public string CrawlerFoundVia { get; init; } = "";

        public string SitemapText => InSitemap ? "Yes" : "No";
        public string NavigationText => !LocationsKnown && !InNavigation ? "Unknown*" : InNavigation ? $"Yes ({NavigationLinks} page(s))" : "No";
        public string ClickDepthText => ClickDepth is int d ? (d == 0 ? "Start page" : $"{d} click(s)") : "Not reachable by clicking";
        public bool IsCandidateForRemoval => Kind is OriginKind.Orphan or OriginKind.SitemapOnly;
    }

    /// <summary>
    /// Works out, for every crawled page, whether it is in sitemap.xml and the site navigation, every page linking to it
    /// (with where the link sits), and the shortest click path from the start page. Built from the saved links, so it
    /// also works for audits saved before link locations were recorded.
    /// </summary>
    public static class PageOrigins
    {
        public static readonly HashSet<string> NavigationLocations = new() { "Menu", "Header", "Footer", "Breadcrumb", "Sidebar" };

        public static void Compute(AuditResult audit)
        {
            var byKey = new Dictionary<string, PageResult>(StringComparer.Ordinal);
            foreach (var p in audit.Pages.OrderBy(p => p.RedirectChain.Count)) // a page served directly wins over a redirect to it
            {
                byKey.TryAdd(AuditResult.UrlKey(p.Url), p);
                byKey.TryAdd(AuditResult.UrlKey(p.FinalUrl), p);
            }
            // IIS / SharePoint paths are case-insensitive: /pages/x.aspx and /Pages/X.aspx are the same page
            var byLowerKey = new Dictionary<string, PageResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byKey) byLowerKey.TryAdd(kv.Key, kv.Value);
            PageResult? Find(string url)
            {
                var key = AuditResult.UrlKey(url);
                return byKey.TryGetValue(key, out var p) ? p : byLowerKey.TryGetValue(key, out var q) ? q : null;
            }

            // Incoming links per page
            var inlinks = new Dictionary<PageResult, List<Inlink>>();
            foreach (var source in audit.Pages)
            {
                foreach (var link in source.Links.Where(l => l.IsInternal))
                {
                    var target = Find(link.Url);
                    if (target == null || target == source) continue;
                    if (!inlinks.TryGetValue(target, out var list)) inlinks[target] = list = new List<Inlink>();
                    if (list.Any(i => i.Url == source.Url)) continue;
                    list.Add(new Inlink
                    {
                        Url = source.Url,
                        Anchor = link.Text.Length > 0 ? link.Text : "(no anchor text)",
                        Location = link.Location.Length > 0 ? link.Location : "Unknown"
                    });
                }
            }

            // Links to a URL that redirects reach the page it redirects to: count them there too
            var startTargets = new HashSet<PageResult>();
            foreach (var redirect in audit.Pages.Where(p => p.RedirectChain.Count > 0))
            {
                var final = Find(redirect.FinalUrl);
                if (final == null || final == redirect) continue;
                if (redirect.Depth == 0) startTargets.Add(final);
                if (!inlinks.TryGetValue(redirect, out var viaRedirect)) continue;
                if (!inlinks.TryGetValue(final, out var list)) inlinks[final] = list = new List<Inlink>();
                foreach (var i in viaRedirect.Where(i => i.Url != final.Url && !list.Any(x => x.Url == i.Url)))
                    list.Add(new Inlink { Url = i.Url, Anchor = $"{i.Anchor} (via redirect from {redirect.Url})", Location = i.Location });
            }

            // Shortest click path from the start page(s), following links (and redirects to their final page)
            var parent = new Dictionary<PageResult, (PageResult? From, string Via)>();
            var queue = new Queue<PageResult>();
            foreach (var start in audit.Pages.Where(p => p.Depth == 0))
            {
                if (parent.TryAdd(start, (null, "Start URL"))) queue.Enqueue(start);
            }
            while (queue.Count > 0)
            {
                var page = queue.Dequeue();
                if (page.RedirectChain.Count > 0 && Find(page.FinalUrl) is PageResult final && final != page && parent.TryAdd(final, (page, "Redirect")))
                    queue.Enqueue(final);
                foreach (var link in page.Links.Where(l => l.IsInternal && !l.NoFollow))
                {
                    var target = Find(link.Url);
                    if (target == null || parent.ContainsKey(target)) continue;
                    var where = link.Location.Length > 0 ? link.Location : "Page";
                    var text = link.Text.Length > 0 ? $" \"{Trim(link.Text, 60)}\"" : "";
                    parent[target] = (page, $"{where} link{text}");
                    queue.Enqueue(target);
                }
            }

            bool locationsKnown = audit.Pages.SelectMany(p => p.Links).Any(l => l.Location.Length > 0);
            var sitemapIssue = new HashSet<PageResult>(audit.Pages.Where(p => p.Issues.Any(i => i.IssueId == IssueCatalog.SitemapIncorrectPage)));

            foreach (var page in audit.Pages)
            {
                var incoming = inlinks.TryGetValue(page, out var l) ? l.OrderBy(i => NavigationLocations.Contains(i.Location) ? 0 : 1).ToList() : new List<Inlink>();
                int nav = incoming.Count(i => NavigationLocations.Contains(i.Location));
                int content = incoming.Count(i => i.Location == "Content");
                bool inSitemap = page.InSitemap || page.FoundVia == "Sitemap" || sitemapIssue.Contains(page);

                var path = new List<PathStep>();
                if (parent.ContainsKey(page))
                {
                    var chain = new List<(PageResult Page, string Via)>();
                    for (var cur = page; cur != null; cur = parent[cur].From)
                        chain.Add((cur, parent[cur].Via));
                    chain.Reverse();
                    path = chain.Select((c, i) => new PathStep { Number = i, Url = c.Page.Url, Via = c.Via }).ToList();
                }
                int? depth = path.Count > 0 ? path.Count - 1 : null;

                OriginKind kind;
                string summary, explanation;
                if ((page.Depth == 0 && (page.FoundVia is "" or "Start URL" or "URL list")) || startTargets.Contains(page))
                {
                    kind = OriginKind.StartPage;
                    summary = audit.Mode == AuditMode.UrlList ? "From your list" : "Start page";
                    explanation = startTargets.Contains(page) && page.Depth != 0 ? $"The start URL {audit.StartUrl} redirects here, so this is the site's home page."
                                : audit.Mode == AuditMode.UrlList ? "This URL came from your list." : "This is the page the crawl started from.";
                    if (incoming.Count > 0) explanation += $" {incoming.Count} crawled page(s) link to it.";
                }
                else if (nav > 0)
                {
                    kind = OriginKind.Navigation;
                    var places = string.Join(", ", incoming.Where(i => NavigationLocations.Contains(i.Location)).Select(i => i.Location.ToLowerInvariant()).Distinct());
                    summary = $"Navigation ({places})";
                    explanation = $"Visitors reach it from the site navigation ({places}) on {nav} page(s)" +
                                  (content > 0 ? $", and from links in the content of {content} page(s)." : ".");
                }
                else if (incoming.Count > 0 && content == incoming.Count)
                {
                    kind = OriginKind.ContentOnly;
                    summary = $"Content links only ({content})";
                    explanation = $"Not in any menu, header or footer: only links inside the content of {content} page(s) lead here" +
                                  (inSitemap ? ", plus sitemap.xml." : ", and it is not in sitemap.xml.");
                }
                else if (incoming.Count > 0)
                {
                    kind = OriginKind.LinkedUnknownPlace;
                    summary = $"Linked from {incoming.Count} page(s)";
                    explanation = $"{incoming.Count} crawled page(s) link here. This audit was made before link positions were recorded, so " +
                                  "it cannot tell menu links from content links — run the audit again to see them.";
                }
                else if (inSitemap)
                {
                    kind = OriginKind.SitemapOnly;
                    summary = "Sitemap only";
                    explanation = "No crawled page links here; search engines find it only through sitemap.xml. Visitors cannot click their way to it.";
                }
                else
                {
                    kind = OriginKind.Orphan;
                    summary = "Orphan";
                    explanation = "No crawled page links here and it is not in sitemap.xml" +
                                  (page.FoundVia == "Link" && page.FoundFrom.Length > 0 ? $" (the crawler reached it from {page.FoundFrom}, which redirects or was re-checked)." : ".");
                }
                if (kind is OriginKind.SitemapOnly or OriginKind.Orphan && audit.CanContinue && audit.Mode == AuditMode.CrawlSite)
                    explanation += " Pages the crawl has not reached yet could still link here.";

                page.Origin = new PageOrigin
                {
                    InSitemap = inSitemap,
                    InNavigation = nav > 0,
                    LocationsKnown = locationsKnown,
                    NavigationLinks = nav,
                    ContentLinks = content,
                    Inlinks = incoming,
                    ClickPath = path,
                    ClickDepth = depth,
                    Kind = kind,
                    Summary = summary,
                    Explanation = explanation,
                    CrawlerFoundVia = page.FoundVia switch
                    {
                        "Sitemap" => "sitemap.xml",
                        "Link" => page.FoundFrom.Length > 0 ? $"a link on {page.FoundFrom}" : "a link",
                        "Start URL" => "the start URL",
                        "URL list" => "your URL list",
                        _ => "not recorded (audit made before this was tracked)"
                    }
                };
            }
        }

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
