# SEO Audit Studio

A Windows desktop app (WPF, .NET 8) for technical SEO site audits, in the style of Semrush Site Audit.
It crawls a website, or tests a list of URLs pasted in or imported from CSV or Excel. It then reports a
Site Health score with errors, warnings and notices, and exports the results to PDF, Excel or CSV.

## Features

| Area | What it does |
|---|---|
| **Input** | Crawl a site from one URL (follows internal links, seeds from `sitemap.xml`, respects `robots.txt`), or audit a fixed URL list. Lists can be pasted, imported from `.csv` / `.txt` / `.xlsx` (the column headed *URL / Link / Address / Website* is used, or else the column with the most URLs), or loaded from `/sitemap.xml`. |
| **Page checks (40+)** | HTTP 4xx/5xx and failed requests, redirect chains, temporary redirects, titles and meta descriptions (missing, duplicate, too long or short), H1 (missing, multiple), duplicate content, low word count, text/HTML ratio, images without alt, broken internal and external links, nofollow internal links, broken internal and external images, hreflang conflicts and incorrect hreflang links (invalid codes, duplicates, no self-reference, broken/redirected targets, missing return links), canonical (missing or pointing elsewhere), noindex (meta and `X-Robots-Tag`), viewport, `lang`, Open Graph, structured data, mixed content, HTTPS, compression, slow pages, large pages, URL hygiene. |
| **Site checks** | robots.txt, XML sitemap, incorrect pages in the sitemap (broken, redirected, noindex, non-canonical or blocked), HTTPS availability, HTTP→HTTPS redirect, SSL certificate validity and expiry, HSTS, www/non-www consolidation, soft 404s, average response time. |
| **Dashboard** | Site Health ring, error/warning/notice totals, change since the previous audit of the same site, thematic reports (Crawlability, On-page, Content, Links, Performance, Security...), status-code chart, top issues, site checks. |
| **Issues** | Every issue type with a description, how to fix it and the affected pages (exportable). Filter by severity or search. |
| **Crawled pages** | Sortable and filterable table. Each page has a detail panel: its issues, on-page data, top keywords (English and Arabic stop words removed) and links with their status. |
| **Performance** | Google PageSpeed Insights: Lighthouse scores, LCP/CLS/TBT/FCP, real-user Core Web Vitals and opportunities. Run it on one URL or on the audit's top 5 pages. |
| **Domain overview** | *Optional, needs a Semrush API key:* Authority Score, organic traffic and keywords, traffic value, backlinks, referring domains, top keywords, competitors. |
| **History** | Every audit is saved automatically. Open, compare (re-run the same site to see deltas) or delete audits. The latest audit reopens at startup. |
| **Export** | PDF report, multi-sheet Excel workbook (Summary, Issues, Issue details, Pages, Links, Keywords, PageSpeed, Domain), CSV of pages, CSV of issues, and the affected pages of a single issue. |

### How each page is reached ("Found via")

For every crawled page, the **Found via** tab, the Pages column and the Excel **Inlinks** sheet show:

- Whether the page is in **sitemap.xml**.
- Whether it is in the **site navigation**: menu, header, footer, breadcrumb or sidebar. This is detected from `<nav>`/`<header>`/`<footer>`/`<aside>`, ARIA roles, common class names and SharePoint's top navigation and Quick Launch.
- **Every page that links to it**, with where the link sits and its anchor text. Links to a URL that redirects count for the page it redirects to.
- The **shortest click path** from the start page.
- A verdict: *Navigation*, *Content links only*, *Sitemap only* or *Orphan*.

The Pages filters **Not in navigation** and **Orphan / sitemap only** list the pages to review before deleting or linking them.
"Sitemap only" and "Orphan" are relative to the pages crawled, so pages the crawl didn't reach could still link there. Continue or a higher *Max pages* gives a complete picture.
Link positions are recorded by audits run from this version on. Older audits still show sitemap status, incoming links and click paths.

### Pause, stop and continue

- **Pause / Resume** (while an audit runs): requests already in progress finish, then nothing more is sent until you resume. Paused time isn't counted in the audit's duration.
- **Stop & save**: the pages crawled so far are saved as a report. Closing the window during an audit offers to do the same.
- **Continue** (audit header, Dashboard banner or History): keeps the pages already crawled and picks up where the audit left off.
  - A site crawl follows the links that were found but not visited yet, plus the sitemap, and visits up to *Max pages* more pages.
  - A URL-list audit audits the URLs of its list that weren't done yet.
  - Broken links, duplicates, hreflang and sitemap checks are then recomputed over all pages, and the audit is saved in place.
  - Continue rebuilds its queue from the saved links, so it also works for audits saved before it existed. The exception is URL-list audits from before, whose list wasn't saved.

### How broken links are confirmed

Busy or throttling servers often answer 503, 429 or time out when a page is actually fine. To avoid
reporting those pages as broken, the audit works like this:

- Link checks use the same number of parallel requests as the crawl.
- Hosts that answer HEAD incorrectly (SharePoint returns 404 to HEAD) get GET instead.
- Throttled requests and timeouts are retried with a pause, and `Retry-After` is honoured.
- Anything that still looks broken is requested again at the end, slowly and one at a time. Only failures that persist are reported.
- Anti-bot and access answers (999 from LinkedIn, 429, 401, 403) are never counted as broken.
- Failed requests give their real reason: "DNS: host name could not be resolved", "SSL: certificate expired", "Timed out"...

Pages whose canonical points to another URL are already consolidated. They are left out of the
duplicate title, description and content checks, and out of the hreflang return-link checks, as Semrush does.

If the site resolves to a private address (an organisation's internal DNS), a notice says so. In that case
the audit sees the internal servers, which can differ from what the public and Google see.

### What crawling can and cannot measure

The site audit, link checks and on-page data come from crawling the site, so they work for any site with
no account. **Traffic, ranking keywords, Authority Score and backlinks** are different: Semrush, Ahrefs and
similar tools estimate them from their own web-wide databases, and crawling one site cannot produce them.
The *Domain overview* page shows them when you enter your own Semrush API key in Settings. Each request
spends Semrush API units.

## Build and run

```bash
dotnet build
dotnet run
```

Release: one self-contained `.exe` that does not need .NET installed:

```bash
dotnet publish -c Release
# → bin\Release\net8.0-windows\win-x64\publish\SEOAuditStudio.exe
```

## Settings and data

Everything is stored under `%AppData%\SEOAuditStudio`:

- `settings.json`: crawler limits, check thresholds and API keys (keys are DPAPI-encrypted for the current Windows user)
- `audits\*.json`: saved audits, plus `index.json` for the History list

Optional API keys:

- **Google PageSpeed Insights**: free, from Google Cloud Console → *APIs & Services* → enable *PageSpeed Insights API* → create an API key. Without a key, Google allows only a few tests.
- **Semrush**: from your Semrush subscription (*Subscription info* → *API units*). It is needed only for Domain overview.

## Project layout

```
Models/        AuditModels (results, settings), IssueCatalog (every check: severity, description, fix), ViewRows
Services/      SiteAuditor (crawler + checks), PageAnalyzer (HTML parsing), RobotsTxt,
               UrlInputService (paste/CSV/Excel import), PageSpeedService, SemrushApiService,
               ExportService (PDF/Excel/CSV), StorageService (settings + history)
ViewModels/    MainViewModel split by feature: .Audit, .Results, .Tools, .Export
Controls/      ScoreRing (circular score gauge)
```

Adding a check: add an entry to `IssueCatalog`, then raise it in `PageAnalyzer` (single-page checks)
or `SiteAuditor` (HTTP, cross-page and site-wide checks). Scoring, the UI and all exports pick it up automatically.

## Libraries

HtmlAgilityPack (HTML parsing), ClosedXML (Excel), CsvHelper (CSV import), QuestPDF (PDF) and
CommunityToolkit.Mvvm. QuestPDF's Community license is free for individuals, non-profits and companies
with under $1M annual revenue. Larger companies need a commercial QuestPDF license
(https://www.questpdf.com/license/).
