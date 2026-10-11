namespace SEOAuditStudio.Models
{
    /// <summary>Every issue the auditor reports, with its severity and fix advice.</summary>
    public static class IssueCatalog
    {
        // Ids are stable: saved audits and history comparisons refer to them.
        public const string Http4xx = "http-4xx";
        public const string Http5xx = "http-5xx";
        public const string FetchFailed = "fetch-failed";
        public const string BrokenInternalLink = "broken-internal-link";
        public const string BrokenExternalLink = "broken-external-link";
        public const string MissingTitle = "missing-title";
        public const string DuplicateTitle = "duplicate-title";
        public const string MissingDescription = "missing-description";
        public const string DuplicateDescription = "duplicate-description";
        public const string DuplicateContent = "duplicate-content";
        public const string MixedContent = "mixed-content";
        public const string NotHttps = "not-https";
        public const string MissingH1 = "missing-h1";
        public const string MultipleH1 = "multiple-h1";
        public const string TitleTooLong = "title-too-long";
        public const string TitleTooShort = "title-too-short";
        public const string DescriptionTooLong = "description-too-long";
        public const string DescriptionTooShort = "description-too-short";
        public const string LowWordCount = "low-word-count";
        public const string LowTextRatio = "low-text-ratio";
        public const string ImagesMissingAlt = "images-missing-alt";
        public const string SlowPage = "slow-page";
        public const string LargePage = "large-page";
        public const string RedirectChain = "redirect-chain";
        public const string TemporaryRedirect = "temporary-redirect";
        public const string NoCompression = "no-compression";
        public const string MissingViewport = "missing-viewport";
        public const string MissingLang = "missing-lang";
        public const string MissingCanonical = "missing-canonical";
        public const string CanonicalMismatch = "canonical-mismatch";
        public const string NoIndex = "noindex";
        public const string NoFollowLinks = "nofollow-internal-links";
        public const string TooManyLinks = "too-many-links";
        public const string NoInternalLinks = "no-internal-links";
        public const string MissingOpenGraph = "missing-open-graph";
        public const string MissingStructuredData = "missing-structured-data";
        public const string UrlTooLong = "url-too-long";
        public const string UrlUnderscores = "url-underscores";
        public const string UrlUppercase = "url-uppercase";
        public const string TitleEqualsH1 = "title-equals-h1";
        public const string Redirected = "redirected";
        public const string NonHtml = "non-html";
        public const string BrokenInternalImage = "broken-internal-image";
        public const string BrokenExternalImage = "broken-external-image";
        public const string SitemapIncorrectPage = "sitemap-incorrect-page";
        public const string HreflangConflict = "hreflang-conflict";
        public const string IncorrectHreflangLink = "incorrect-hreflang-link";

        private static readonly Dictionary<string, IssueDefinition> Items = new[]
        {
            // ---------- Errors ----------
            Def(Http4xx, "Pages returned 4XX status code", Severity.Error, "Crawlability",
                "The page could not be found or access was refused (404, 403, 410...). Visitors and search engines hit a dead end.",
                "Restore the page, or 301-redirect it to the most relevant live page and update links pointing to it."),
            Def(Http5xx, "Pages returned 5XX status code", Severity.Error, "Crawlability",
                "The server failed to answer the request. Repeated 5XX errors make search engines crawl less and drop pages.",
                "Check server logs for the failing URL, fix the application error or capacity problem, then re-test."),
            Def(FetchFailed, "Pages could not be fetched", Severity.Error, "Crawlability",
                "The request failed before an HTTP status came back (DNS failure, timeout, connection or TLS error).",
                "Verify the domain resolves, the certificate is valid and the server answers within the timeout."),
            Def(BrokenInternalLink, "Broken internal links", Severity.Error, "Links",
                "Links on the page point to URLs on your own site that return an error.",
                "Update each link to a working URL or remove it; redirect deleted pages."),
            Def(MissingTitle, "Missing title tag", Severity.Error, "On-page",
                "The page has no <title>. The title is the main text shown in search results.",
                "Add a unique, descriptive <title> of about 30-60 characters that includes the page's main keyword."),
            Def(DuplicateTitle, "Duplicate title tags", Severity.Error, "On-page",
                "Several pages share the same title, so search engines struggle to decide which to rank.",
                "Give every page a unique title that reflects its own content."),
            Def(DuplicateContent, "Duplicate content", Severity.Error, "Content",
                "Pages have identical text content. Search engines may pick one and ignore the others.",
                "Merge the pages, make their content distinct, or point duplicates to the main page with rel=canonical."),
            Def(MixedContent, "Mixed content", Severity.Error, "Security",
                "An HTTPS page loads scripts, styles or images over insecure HTTP. Browsers block or warn about it.",
                "Load every resource over HTTPS (update hard-coded http:// URLs)."),
            Def(NotHttps, "Page not served over HTTPS", Severity.Error, "Security",
                "The page is served over plain HTTP. Browsers mark it 'Not secure' and HTTPS is a ranking signal.",
                "Install a TLS certificate and 301-redirect all HTTP URLs to HTTPS."),

            Def(BrokenInternalImage, "Broken internal images", Severity.Error, "Links",
                "Images hosted on your own site return an error, so visitors see a broken image and image search cannot index it.",
                "Restore the image file or update the <img src> to a working URL; remove images that are no longer needed."),
            Def(SitemapIncorrectPage, "Incorrect pages found in sitemap.xml", Severity.Error, "Crawlability",
                "The XML sitemap lists URLs that should not be there: broken (4XX/5XX), redirected, blocked by noindex or robots.txt, or not the canonical version.",
                "Keep only live, indexable, canonical 200 URLs in the sitemap. Regenerate it after removing or redirecting pages."),
            Def(HreflangConflict, "Hreflang conflicts within page source code", Severity.Error, "International",
                "The page's hreflang tags contradict each other: invalid language/region codes, the same language pointing to different URLs, no self-referencing hreflang, or a hreflang that disagrees with <html lang>.",
                "Use ISO 639-1 language codes (optionally with ISO 3166-1 region, e.g. ar-SA, en-GB), one URL per language, include a self-referencing hreflang, and match <html lang>."),
            Def(IncorrectHreflangLink, "Incorrect hreflang links", Severity.Error, "International",
                "Hreflang tags point to URLs that are broken, redirected or not canonical, or the alternate page does not link back (missing return link).",
                "Point every hreflang to the final, canonical 200 URL, and make each language version link back to all the others."),

            // ---------- Warnings ----------
            Def(BrokenExternalImage, "Broken external images", Severity.Warning, "Links",
                "Images loaded from other websites return an error.",
                "Host the image yourself or replace the URL with a working one."),
            Def(BrokenExternalLink, "Broken external links", Severity.Warning, "Links",
                "Links to other websites return an error.",
                "Replace the links with working URLs or remove them."),
            Def(MissingDescription, "Missing meta description", Severity.Warning, "On-page",
                "No meta description: search engines pick a snippet themselves, often a poor one.",
                "Write a unique meta description of about 70-160 characters that summarises the page."),
            Def(DuplicateDescription, "Duplicate meta descriptions", Severity.Warning, "On-page",
                "Several pages share the same meta description.",
                "Write a unique description for each page."),
            Def(MissingH1, "Missing H1 heading", Severity.Warning, "On-page",
                "The page has no <h1>, the main heading search engines use to understand the topic.",
                "Add one <h1> that describes the page content."),
            Def(TitleTooLong, "Title too long", Severity.Warning, "On-page",
                "Long titles are cut off in search results.",
                "Shorten the title to about 60 characters, keeping the key words first."),
            Def(TitleTooShort, "Title too short", Severity.Warning, "On-page",
                "Very short titles waste space in search results and rarely describe the page well.",
                "Expand the title to about 30-60 characters."),
            Def(LowWordCount, "Low word count", Severity.Warning, "Content",
                "The page has little text, which search engines may treat as thin content.",
                "Add useful, original content, or noindex pages that are thin on purpose."),
            Def(ImagesMissingAlt, "Images without alt attributes", Severity.Warning, "Accessibility",
                "Images have no alt text, which screen readers and image search rely on.",
                "Add a short alt description to each meaningful image (alt=\"\" for decorative ones)."),
            Def(SlowPage, "Slow page load", Severity.Warning, "Performance",
                "The HTML took long to download. Slow pages hurt users and Core Web Vitals.",
                "Enable caching and compression, optimise server code and database queries, use a CDN."),
            Def(LargePage, "Large HTML page size", Severity.Warning, "Performance",
                "The HTML document is very large and slow to download and parse.",
                "Remove inline scripts/styles and unused markup; paginate very long pages."),
            Def(RedirectChain, "Redirect chains", Severity.Warning, "Crawlability",
                "The URL redirects more than once before reaching the final page, wasting crawl budget and link equity.",
                "Redirect directly to the final URL and update internal links to point to it."),
            Def(TemporaryRedirect, "Temporary redirects", Severity.Warning, "Crawlability",
                "302/307 redirects tell search engines the move is temporary, so ranking signals may not transfer.",
                "Use a 301/308 redirect when the move is permanent."),
            Def(NoCompression, "Compression not enabled", Severity.Warning, "Performance",
                "The HTML is sent without gzip or Brotli compression.",
                "Enable gzip/Brotli compression for text responses on the web server."),
            Def(MissingViewport, "Missing viewport meta tag", Severity.Warning, "Mobile",
                "Without <meta name=\"viewport\"> the page is not rendered properly on phones.",
                "Add <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">."),
            Def(CanonicalMismatch, "Canonical points to another URL", Severity.Warning, "Indexability",
                "The page's rel=canonical names a different URL, so this page will not be indexed itself.",
                "Make sure that is intended; otherwise make the canonical self-referencing."),
            Def(MultipleH1, "Multiple H1 headings", Severity.Warning, "On-page",
                "The page has more than one <h1>, which blurs its main topic.",
                "Keep one <h1> and use <h2>-<h6> for sub-sections."),
            Def(LowTextRatio, "Low text-to-HTML ratio", Severity.Warning, "Content",
                "Visible text is under 10% of the HTML: the page is mostly markup and code.",
                "Move inline scripts and styles to files, remove unused markup, add content."),
            Def(TooManyLinks, "Too many on-page links", Severity.Warning, "Links",
                "The page has more than 300 links, diluting the value passed to each.",
                "Keep the most important links; trim navigation and footer link lists."),

            // ---------- Notices ----------
            Def(DescriptionTooLong, "Meta description too long", Severity.Notice, "On-page",
                "Long descriptions are truncated in search results.",
                "Keep descriptions under about 160 characters."),
            Def(DescriptionTooShort, "Meta description too short", Severity.Notice, "On-page",
                "Very short descriptions do little to attract clicks.",
                "Expand the description to about 70-160 characters."),
            Def(MissingLang, "Missing language attribute", Severity.Notice, "International",
                "The <html> element has no lang attribute.",
                "Add the page language, for example <html lang=\"en\"> or <html lang=\"ar\">."),
            Def(MissingCanonical, "Missing canonical tag", Severity.Notice, "Indexability",
                "No rel=canonical: search engines guess the preferred URL among duplicates (parameters, http/https, www).",
                "Add a self-referencing <link rel=\"canonical\" href=\"...\">."),
            Def(NoIndex, "Page blocked by noindex", Severity.Notice, "Indexability",
                "A meta robots or X-Robots-Tag noindex keeps the page out of search results.",
                "Remove noindex if the page should rank."),
            Def(NoFollowLinks, "Nofollow internal links", Severity.Notice, "Links",
                "Internal links carry rel=nofollow, so they pass no value inside your own site.",
                "Remove rel=nofollow from internal links."),
            Def(NoInternalLinks, "Page has no internal links", Severity.Notice, "Links",
                "The page links to no other page on the site, a dead end for users and crawlers.",
                "Add links to related pages."),
            Def(MissingOpenGraph, "Missing Open Graph tags", Severity.Notice, "Social",
                "No og:title / og:image tags: shares on social networks look plain.",
                "Add og:title, og:description, og:image and og:url meta tags."),
            Def(MissingStructuredData, "No structured data", Severity.Notice, "Markup",
                "No JSON-LD, microdata or RDFa found, so the page cannot get rich results.",
                "Add schema.org markup (Organization, Article, Product, BreadcrumbList...) as JSON-LD."),
            Def(UrlTooLong, "URL too long", Severity.Notice, "URLs",
                "URLs longer than 115 characters are hard to read and share.",
                "Use short, descriptive URLs."),
            Def(UrlUnderscores, "Underscores in URL", Severity.Notice, "URLs",
                "Search engines treat hyphens, not underscores, as word separators.",
                "Use hyphens in URLs (redirect the old ones)."),
            Def(UrlUppercase, "Uppercase characters in URL", Severity.Notice, "URLs",
                "URLs are case-sensitive; mixed case invites duplicate URLs.",
                "Use lowercase URLs and redirect mixed-case variants."),
            Def(TitleEqualsH1, "Title and H1 are identical", Severity.Notice, "On-page",
                "The title and H1 are word-for-word the same, a missed chance to target more phrasing.",
                "Vary the H1 slightly from the title."),
            Def(Redirected, "URL redirects", Severity.Notice, "Crawlability",
                "The audited URL redirects to another address.",
                "Link straight to the final URL."),
            Def(NonHtml, "Non-HTML resource", Severity.Notice, "Crawlability",
                "The URL returned a non-HTML file (PDF, image, ...), so on-page checks were skipped.",
                "No action needed unless an HTML page was expected."),
        }.ToDictionary(d => d.Id);

        public static IEnumerable<IssueDefinition> All => Items.Values;

        public static IssueDefinition Get(string id) =>
            Items.TryGetValue(id, out var d) ? d
                : new IssueDefinition { Id = id, Title = id, Severity = Severity.Notice, Category = "Other" };

        private static IssueDefinition Def(string id, string title, Severity severity, string category, string description, string fix) =>
            new() { Id = id, Title = title, Severity = severity, Category = category, Description = description, HowToFix = fix };
    }
}
