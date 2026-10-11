using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Services
{
    /// <summary>
    /// Parses one downloaded HTML page and records the checks that only need that page.
    /// Checks that compare pages (duplicates, broken links) run in SiteAuditor once the crawl ends.
    /// </summary>
    public static class PageAnalyzer
    {
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            // English
            "a","about","above","after","again","against","all","am","an","and","any","are","as","at","be","because","been",
            "before","being","below","between","both","but","by","can","could","did","do","does","doing","down","during","each",
            "few","for","from","further","had","has","have","having","he","her","here","hers","herself","him","himself","his",
            "how","i","if","in","into","is","it","its","itself","just","me","more","most","my","myself","no","nor","not","now",
            "of","off","on","once","only","or","other","our","ours","ourselves","out","over","own","same","she","should","so",
            "some","such","than","that","the","their","theirs","them","themselves","then","there","these","they","this","those",
            "through","to","too","under","until","up","very","was","we","were","what","when","where","which","while","who",
            "whom","why","will","with","you","your","yours","yourself","yourselves","also","may","us","new","one","get","use",
            "via","per","etc","like","well","would","much","many","every","within","without","upon","can't","don't","it's",
            // Arabic
            "في","من","على","إلى","الى","عن","مع","هذا","هذه","ذلك","تلك","التي","الذي","الذين","اللذين","هو","هي","هم","أن","ان",
            "إن","كان","كانت","يكون","لا","ما","لم","لن","قد","ثم","أو","او","و","كل","بعد","قبل","حتى","عند","بين","أي","اي",
            "كما","لكن","بل","أيضا","ايضا","هناك","هنا","غير","منذ","نحو","خلال","حول","ضمن","لدى","إذا","اذا","فيه","فيها",
            "به","بها","له","لها","منه","منها","عليه","عليها","التى","وهو","وهي","وفي","ومن","الى"
        };

        public static void Analyze(PageResult page, string html, Uri pageUri, AuditSettings settings, Func<Uri, bool> isInternal)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var root = doc.DocumentNode;

            page.Title = Clean(root.SelectSingleNode("//title")?.InnerText);
            page.MetaDescription = Clean(Meta(root, "description"));
            page.MetaRobots = Clean(Meta(root, "robots"));
            page.Lang = root.SelectSingleNode("//html")?.GetAttributeValue("lang", "")?.Trim() ?? "";
            page.HasViewport = Meta(root, "viewport") != null;
            page.HasOpenGraph = root.SelectSingleNode("//meta[@property='og:title' or @property='og:image']") != null;
            page.HasStructuredData =
                root.SelectSingleNode("//script[@type='application/ld+json']") != null ||
                root.SelectSingleNode("//*[@itemscope]") != null ||
                root.SelectSingleNode("//*[@typeof]") != null;
            page.HasHreflang = root.SelectSingleNode("//link[@hreflang]") != null;

            var baseHref = root.SelectSingleNode("//base[@href]")?.GetAttributeValue("href", "");
            var baseUri = !string.IsNullOrWhiteSpace(baseHref) && Uri.TryCreate(pageUri, baseHref, out var b) ? b : pageUri;

            var canonical = root.SelectSingleNode("//link[translate(@rel,'CANONICAL','canonical')='canonical']")?.GetAttributeValue("href", "");
            if (!string.IsNullOrWhiteSpace(canonical) && Uri.TryCreate(baseUri, WebUtility.HtmlDecode(canonical.Trim()), out var canonUri))
                page.Canonical = canonUri.AbsoluteUri;

            page.H1 = Texts(root, "//h1");
            page.H2 = Texts(root, "//h2");

            // Images (src, or the lazy-loading data-src when src is a placeholder)
            var images = (root.SelectNodes("//img") ?? Enumerable.Empty<HtmlNode>()).ToList();
            page.ImageCount = images.Count;
            page.ImagesMissingAlt = images.Count(i => i.Attributes["alt"] == null);
            var seenImages = new HashSet<string>();
            foreach (var img in images)
            {
                var src = WebUtility.HtmlDecode(img.GetAttributeValue("src", "")).Trim();
                if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    src = WebUtility.HtmlDecode(img.GetAttributeValue("data-src", img.GetAttributeValue("data-lazy-src", ""))).Trim();
                if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(baseUri, src, out var imgUri) || (imgUri.Scheme != Uri.UriSchemeHttp && imgUri.Scheme != Uri.UriSchemeHttps)) continue;
                var imgUrl = new UriBuilder(imgUri) { Fragment = "" }.Uri.AbsoluteUri;
                if (!seenImages.Add(imgUrl)) continue;
                page.Images.Add(new ImageInfo
                {
                    Url = imgUrl,
                    HasAlt = img.Attributes["alt"] != null,
                    Alt = Truncate(Clean(img.GetAttributeValue("alt", "")), 120),
                    IsInternal = isInternal(imgUri)
                });
            }

            // hreflang alternates
            foreach (var link in root.SelectNodes("//link[@hreflang][@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                if (!link.GetAttributeValue("rel", "").Contains("alternate", StringComparison.OrdinalIgnoreCase)) continue;
                var href = WebUtility.HtmlDecode(link.GetAttributeValue("href", "")).Trim();
                if (Uri.TryCreate(baseUri, href, out var alt))
                    page.Hreflangs.Add(new HreflangLink { Lang = link.GetAttributeValue("hreflang", "").Trim(), Url = alt.AbsoluteUri });
            }

            // Links
            var byUrl = new Dictionary<string, LinkInfo>();
            foreach (var a in root.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var href = WebUtility.HtmlDecode(a.GetAttributeValue("href", "")).Trim();
                if (href.Length == 0 || href.StartsWith('#') ||
                    Regex.IsMatch(href, @"^(mailto|tel|javascript|data|sms|whatsapp|skype|ftp):", RegexOptions.IgnoreCase)) continue;
                if (!Uri.TryCreate(baseUri, href, out var target)) continue;
                if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) continue;
                var url = new UriBuilder(target) { Fragment = "" }.Uri.AbsoluteUri;
                var location = LinkLocation(a);
                if (byUrl.TryGetValue(url, out var existing))
                {
                    // The same URL in content and in the menu counts as a navigation link
                    if (existing.Location == "Content" && location != "Content") existing.Location = location;
                    continue;
                }
                var info = new LinkInfo
                {
                    Url = url,
                    Text = Truncate(Clean(a.InnerText), 120),
                    IsInternal = isInternal(target),
                    NoFollow = a.GetAttributeValue("rel", "").Contains("nofollow", StringComparison.OrdinalIgnoreCase),
                    Location = location
                };
                byUrl[url] = info;
                page.Links.Add(info);
            }
            page.InternalLinkCount = page.Links.Count(l => l.IsInternal);
            page.ExternalLinkCount = page.Links.Count - page.InternalLinkCount;

            // Mixed content: insecure sub-resources on an HTTPS page
            int insecure = 0;
            if (pageUri.Scheme == Uri.UriSchemeHttps)
            {
                insecure = (root.SelectNodes("//script[@src]|//img[@src]|//iframe[@src]|//link[@rel='stylesheet'][@href]|//video[@src]|//audio[@src]|//source[@src]")
                            ?? Enumerable.Empty<HtmlNode>())
                    .Count(n => (n.GetAttributeValue("src", null) ?? n.GetAttributeValue("href", "")).TrimStart()
                        .StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            }

            // Visible text (drop scripts, styles, templates)
            foreach (var n in root.SelectNodes("//script|//style|//noscript|//template|//svg") ?? Enumerable.Empty<HtmlNode>())
                n.Remove();
            var bodyNode = root.SelectSingleNode("//body") ?? root;
            var text = Clean(bodyNode.InnerText);
            var words = Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}'\-]*").Select(m => m.Value).ToList();
            page.WordCount = words.Count;
            page.TextToHtmlRatio = html.Length == 0 ? 0 : Math.Round(100.0 * text.Length / html.Length, 1);
            page.TopKeywords = TopKeywords(words);
            page.ContentHash = words.Count < 20 ? "" :
                Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join(' ', words).ToLowerInvariant())));

            AddPageIssues(page, pageUri, settings, insecure);
        }

        private static void AddPageIssues(PageResult page, Uri pageUri, AuditSettings s, int insecureResources)
        {
            void Add(string id, string detail = "") => page.Issues.Add(new PageIssue { IssueId = id, Detail = detail });

            if (pageUri.Scheme == Uri.UriSchemeHttp) Add(IssueCatalog.NotHttps);
            if (insecureResources > 0) Add(IssueCatalog.MixedContent, $"{insecureResources} insecure resource(s)");

            if (page.Title.Length == 0) Add(IssueCatalog.MissingTitle);
            else if (page.Title.Length > s.TitleMax) Add(IssueCatalog.TitleTooLong, $"{page.Title.Length} characters");
            else if (page.Title.Length < s.TitleMin) Add(IssueCatalog.TitleTooShort, $"{page.Title.Length} characters");

            if (page.MetaDescription.Length == 0) Add(IssueCatalog.MissingDescription);
            else if (page.MetaDescription.Length > s.DescriptionMax) Add(IssueCatalog.DescriptionTooLong, $"{page.MetaDescription.Length} characters");
            else if (page.MetaDescription.Length < s.DescriptionMin) Add(IssueCatalog.DescriptionTooShort, $"{page.MetaDescription.Length} characters");

            if (page.H1.Count == 0) Add(IssueCatalog.MissingH1);
            else if (page.H1.Count > 1) Add(IssueCatalog.MultipleH1, $"{page.H1.Count} H1 tags");
            if (page.H1.Count > 0 && page.Title.Length > 0 && string.Equals(page.H1[0], page.Title, StringComparison.OrdinalIgnoreCase))
                Add(IssueCatalog.TitleEqualsH1);

            bool noindex = page.MetaRobots.Contains("noindex", StringComparison.OrdinalIgnoreCase);
            if (noindex) Add(IssueCatalog.NoIndex, page.MetaRobots);

            if (page.WordCount < s.MinWordCount && !noindex) Add(IssueCatalog.LowWordCount, $"{page.WordCount} words");
            if (page.TextToHtmlRatio < 10) Add(IssueCatalog.LowTextRatio, $"{page.TextToHtmlRatio}%");
            if (page.ImagesMissingAlt > 0) Add(IssueCatalog.ImagesMissingAlt, $"{page.ImagesMissingAlt} of {page.ImageCount} images");
            if (!page.HasViewport) Add(IssueCatalog.MissingViewport);
            if (page.Lang.Length == 0) Add(IssueCatalog.MissingLang);

            if (page.Canonical.Length == 0) Add(IssueCatalog.MissingCanonical);
            else if (!SameUrl(page.Canonical, page.FinalUrl)) Add(IssueCatalog.CanonicalMismatch, page.Canonical);

            int nofollowInternal = page.Links.Count(l => l.IsInternal && l.NoFollow);
            if (nofollowInternal > 0) Add(IssueCatalog.NoFollowLinks, $"{nofollowInternal} link(s)");
            if (page.Links.Count > 300) Add(IssueCatalog.TooManyLinks, $"{page.Links.Count} links");
            if (page.InternalLinkCount == 0) Add(IssueCatalog.NoInternalLinks);
            if (!page.HasOpenGraph) Add(IssueCatalog.MissingOpenGraph);
            if (!page.HasStructuredData) Add(IssueCatalog.MissingStructuredData);

            var hreflangProblems = HreflangConflicts(page);
            if (hreflangProblems.Count > 0) Add(IssueCatalog.HreflangConflict, string.Join("; ", hreflangProblems));
        }

        private static readonly string[] MenuWords =
            { "menu", "navbar", "navigation", "topnav", "leftnav", "sidenav", "mainnav", "megamenu", "quicklaunch", "listmenu", "zz1_", "zz2_" };

        /// <summary>
        /// Where a link sits on the page, from its ancestors: Menu (nav, role=navigation, menu/navbar classes, SharePoint
        /// top navigation and Quick Launch), Header, Footer, Breadcrumb, Sidebar, or Content when none applies.
        /// The nearest matching ancestor wins.
        /// </summary>
        private static string LinkLocation(HtmlNode a)
        {
            for (var n = a.ParentNode; n != null && n.NodeType == HtmlNodeType.Element && n.Name is not ("body" or "html"); n = n.ParentNode)
            {
                var tokens = (n.GetAttributeValue("class", "") + " " + n.GetAttributeValue("id", ""))
                    .ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var role = n.GetAttributeValue("role", "").ToLowerInvariant();
                var aria = n.GetAttributeValue("aria-label", "").ToLowerInvariant();

                if (aria.Contains("breadcrumb") || tokens.Any(t => t.Contains("breadcrumb"))) return "Breadcrumb";
                if (n.Name == "footer" || tokens.Any(t => (t.StartsWith("footer") || t.EndsWith("footer") || t.EndsWith("footerarea")) &&
                                                            !t.StartsWith("card") && !t.StartsWith("panel") && !t.StartsWith("modal")))
                    return "Footer";
                if (n.Name == "nav" || role is "navigation" or "menubar" or "menu" ||
                    tokens.Any(t => t == "nav" || MenuWords.Any(w => t.Contains(w))))
                    return "Menu";
                if ((n.Name == "header" && !n.Ancestors("article").Any()) ||
                    tokens.Any(t => t == "header" || t.StartsWith("header-") || t.StartsWith("header_") || t.Contains("masthead") ||
                                    t is "site-header" or "main-header" or "top-header" or "suitebar" or "s4-titlerow" or "s4-ribbonrow"))
                    return "Header";
                if (n.Name == "aside" || tokens.Any(t => t.Contains("sidebar") || t.Contains("side-bar")))
                    return "Sidebar";
            }
            return "Content";
        }

        // ISO 639-1/2 language, optional script, optional ISO 3166-1 alpha-2 or UN M.49 region; or x-default
        private static readonly Regex HreflangCode = new(@"^(x-default|[a-z]{2,3}(-[a-z]{4})?(-([a-z]{2}|\d{3}))?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Problems visible in the page's own hreflang tags (return links are checked across pages in SiteAuditor).</summary>
        private static List<string> HreflangConflicts(PageResult page)
        {
            var problems = new List<string>();
            if (page.Hreflangs.Count == 0) return problems;

            foreach (var h in page.Hreflangs.Where(h => !HreflangCode.IsMatch(h.Lang)))
                problems.Add($"invalid code \"{h.Lang}\"");
            // A very common mistake: "uk" is Ukrainian; the United Kingdom is GB
            foreach (var h in page.Hreflangs.Where(h => h.Lang.EndsWith("-uk", StringComparison.OrdinalIgnoreCase)))
                problems.Add($"\"{h.Lang}\" uses region UK (use GB for the United Kingdom)");

            foreach (var g in page.Hreflangs.GroupBy(h => h.Lang.ToLowerInvariant()).Where(g => g.Select(h => h.Url.TrimEnd('/')).Distinct().Count() > 1))
                problems.Add($"\"{g.Key}\" points to {g.Select(h => h.Url).Distinct().Count()} different URLs");

            var self = page.Hreflangs.FirstOrDefault(h => SameUrl(h.Url, page.FinalUrl) ||
                                                         (page.Canonical.Length > 0 && SameUrl(h.Url, page.Canonical)));
            if (self == null)
                problems.Add("no self-referencing hreflang");
            else if (page.Lang.Length > 0 && !self.Lang.Equals("x-default", StringComparison.OrdinalIgnoreCase) &&
                     !Primary(self.Lang).Equals(Primary(page.Lang), StringComparison.OrdinalIgnoreCase))
                problems.Add($"self hreflang \"{self.Lang}\" conflicts with <html lang=\"{page.Lang}\">");

            return problems;

            static string Primary(string code) => code.Split('-', '_')[0];
        }

        private static List<KeywordStat> TopKeywords(List<string> words)
        {
            var lower = words.Select(w => w.ToLowerInvariant()).ToList();
            var meaningful = lower.Where(w => w.Length > 2 && !StopWords.Contains(w) && !w.All(char.IsDigit)).ToList();
            if (meaningful.Count == 0) return new List<KeywordStat>();

            var singles = meaningful.GroupBy(w => w).Select(g => (g.Key, Count: g.Count()));
            var bigrams = new List<string>();
            for (int i = 0; i + 1 < lower.Count; i++)
            {
                string a = lower[i], b = lower[i + 1];
                if (a.Length > 2 && b.Length > 2 && !StopWords.Contains(a) && !StopWords.Contains(b)) bigrams.Add(a + " " + b);
            }
            var pairs = bigrams.GroupBy(w => w).Where(g => g.Count() > 1).Select(g => (g.Key, Count: g.Count()));

            return singles.OrderByDescending(x => x.Count).Take(12)
                .Concat(pairs.OrderByDescending(x => x.Count).Take(8))
                .OrderByDescending(x => x.Count)
                .Select(x => new KeywordStat { Keyword = x.Key, Count = x.Count, Density = Math.Round(100.0 * x.Count / words.Count, 2) })
                .ToList();
        }

        public static bool SameUrl(string a, string b)
        {
            static string Key(string u) => u.TrimEnd('/').Replace("://www.", "://", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
            return Key(a) == Key(b);
        }

        private static string? Meta(HtmlNode root, string name) =>
            root.SelectSingleNode($"//meta[translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{name}']")
                ?.GetAttributeValue("content", "");

        private static List<string> Texts(HtmlNode root, string xpath) =>
            (root.SelectNodes(xpath) ?? Enumerable.Empty<HtmlNode>()).Select(n => Clean(n.InnerText)).Where(t => t.Length > 0).ToList();

        private static string Clean(string? s) =>
            s == null ? "" : Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ").Trim();

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
