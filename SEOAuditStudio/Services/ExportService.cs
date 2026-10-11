using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Services
{
    /// <summary>Writes an audit as CSV, a multi-sheet Excel workbook, or a PDF report.</summary>
    public static class ExportService
    {
        static ExportService()
        {
            // Free for individuals and companies under $1M annual revenue; see https://www.questpdf.com/license/
            QuestPDF.Settings.License = LicenseType.Community;
            // Windows-only app: Segoe UI / Tahoma are always installed and, unlike the bundled Lato, cover Arabic text
            QuestPDF.Settings.UseSystemFonts = true;
            // Never fail an export over a font: fall back to whatever font is available
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        }

        // ------------------------------------------------------------------ tables shared by all formats

        private static readonly string[] PageColumns =
        {
            "URL", "Final URL", "Status", "Depth", "Score", "Errors", "Warnings", "Notices", "Response (ms)", "Size (KB)",
            "Title", "Title length", "Meta description", "Description length", "H1", "H1 count", "Words", "Text/HTML %",
            "Images", "Images without alt", "Internal links", "External links", "Canonical", "Meta robots", "Lang",
            "Viewport", "Open Graph", "Structured data", "Compression", "In sitemap", "Hreflang", "Broken images", "Redirects", "Error",
            "Found via", "In navigation", "Linked from (pages)", "Clicks from start", "Click path", "Crawler found it via", "How visitors reach it"
        };

        private static object[] PageRow(PageResult p) => new object[]
        {
            p.Url, p.FinalUrl, p.StatusText, p.Depth, p.Score, p.ErrorCount, p.WarningCount, p.NoticeCount, p.ResponseTimeMs,
            Math.Round(p.SizeBytes / 1024.0, 1), p.Title, p.Title.Length, p.MetaDescription, p.MetaDescription.Length, p.H1Text,
            p.H1.Count, p.WordCount, p.TextToHtmlRatio, p.ImageCount, p.ImagesMissingAlt, p.InternalLinkCount, p.ExternalLinkCount,
            p.Canonical, p.MetaRobots, p.Lang, YesNo(p.HasViewport), YesNo(p.HasOpenGraph), YesNo(p.HasStructuredData),
            string.IsNullOrEmpty(p.ContentEncoding) ? "none" : p.ContentEncoding, YesNo(p.Origin?.InSitemap ?? p.InSitemap), string.Join(", ", p.Hreflangs.Select(h => h.Lang)),
            p.Issues.Count(i => i.IssueId is IssueCatalog.BrokenInternalImage or IssueCatalog.BrokenExternalImage) > 0 ? "Yes" : "No", string.Join(" → ", p.RedirectChain), p.Error ?? "",
            p.Origin?.Summary ?? "", p.Origin?.NavigationText ?? "", p.Origin?.Inlinks.Count ?? 0,
            p.Origin?.ClickDepth?.ToString() ?? "not reachable", string.Join(" → ", p.Origin?.ClickPath.Skip(1).Select(s => $"{s.Via}: {s.Url}") ?? Enumerable.Empty<string>()),
            p.Origin?.CrawlerFoundVia ?? "", p.Origin?.Explanation ?? ""
        };

        private static readonly string[] IssueColumns = { "Severity", "Category", "Issue", "Page URL", "Broken / target URL", "Linked from page", "Anchor text", "Detail", "How to fix" };

        // Broken links, images and hreflang targets get one row per URL; 4XX/5XX/redirected pages one row per page
        // linking to them, so each can be filtered, opened and fixed at its source
        private static IEnumerable<object[]> IssueRows(AuditResult a)
        {
            var inlinks = a.BuildInlinks();
            return a.Pages.SelectMany(p => p.Issues.Select(i => (p, i, d: IssueCatalog.Get(i.IssueId))))
                .OrderBy(x => x.d.Severity).ThenBy(x => x.d.Title).ThenBy(x => x.p.Url)
                .SelectMany(x =>
                {
                    object[] Row(string target, string source, string anchor, string detail) =>
                        new object[] { x.d.Severity.ToString(), x.d.Category, x.d.Title, x.p.Url, target, source, anchor, detail, x.d.HowToFix };
                    if (x.i.Targets.Count > 0)
                        return x.i.Targets.Select(t => Row(t.Url, "", "", t.Reason));
                    if (AuditResult.InlinkIssueIds.Contains(x.i.IssueId))
                    {
                        var from = AuditResult.InlinksFor(x.p, inlinks);
                        return from.Count > 0
                            ? from.Select(s => Row("", s.Url, s.Reason, x.i.Detail))
                            : new[] { Row("", "", "", $"{x.i.Detail} — {a.FoundVia(x.p)}") };
                    }
                    return new[] { Row("", "", "", x.i.Detail) };
                });
        }

        private static string YesNo(bool b) => b ? "Yes" : "No";

        // ------------------------------------------------------------------ CSV

        /// <summary>Origins (sitemap, navigation, inlinks, click path) are computed when an audit is shown; make sure they exist.</summary>
        private static AuditResult Prepared(AuditResult a) { EnsureOrigins(a); return a; }

        private static void EnsureOrigins(AuditResult a)
        {
            if (a.Pages.Any(p => p.Origin == null)) PageOrigins.Compute(a);
        }

        public static void ExportPagesCsv(AuditResult a, string path) =>
            WriteCsv(path, PageColumns, Prepared(a).Pages.Select(PageRow));

        public static void ExportIssuesCsv(AuditResult a, string path) =>
            WriteCsv(path, IssueColumns, IssueRows(a));

        public static void ExportRowsCsv(string path, string[] headers, IEnumerable<object[]> rows) => WriteCsv(path, headers, rows);

        private static void WriteCsv(string path, string[] headers, IEnumerable<object[]> rows)
        {
            // UTF-8 with BOM so Excel shows Arabic and other non-Latin text correctly
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine(string.Join(",", headers.Select(Escape)));
            foreach (var r in rows) w.WriteLine(string.Join(",", r.Select(v => Escape(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""))));
        }

        private static string Escape(string v)
        {
            // Neutralise spreadsheet formulas in crawled text (CSV injection)
            if (v.Length > 0 && "=+-@".Contains(v[0]) && !double.TryParse(v, out _)) v = "'" + v;
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // ------------------------------------------------------------------ Excel

        public static void ExportExcel(AuditResult a, string path, DomainMetrics? domain = null)
        {
            EnsureOrigins(a);
            using var wb = new XLWorkbook();

            // Summary
            var ws = wb.Worksheets.Add("Summary");
            ws.Cell(1, 1).Value = "Site Audit Report";
            ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(18);
            var rows = new (string, object)[]
            {
                ("Project", a.Name), ("Start URL", a.StartUrl), ("Mode", a.Mode == AuditMode.CrawlSite ? "Site crawl" : "URL list"),
                ("Date", a.StartedAt.ToString("yyyy-MM-dd HH:mm")), ("Duration", a.Duration.ToString(@"hh\:mm\:ss")),
                ("Pages crawled", a.Pages.Count), ("Site Health", a.SiteHealth + "%"),
                ("Errors", a.Errors), ("Warnings", a.Warnings), ("Notices", a.Notices),
                ("Status", a.Cancelled ? "Stopped before completion" : "Completed")
            };
            int r = 3;
            foreach (var (k, v) in rows)
            {
                ws.Cell(r, 1).Value = k;
                ws.Cell(r, 1).Style.Font.SetBold();
                ws.Cell(r, 2).Value = XLCellValue.FromObject(v);
                r++;
            }
            ws.Cell(r + 1, 1).Value = "Site-wide checks";
            ws.Cell(r + 1, 1).Style.Font.SetBold().Font.SetFontSize(13);
            AddTable(ws, r + 2, new[] { "Check", "Result", "Severity", "Detail" },
                a.SiteChecks.Select(c => new object[] { c.Name, c.Passed ? "Passed" : "Failed", c.Severity.ToString(), c.Detail }), "SiteChecks");
            ws.Column(1).Width = 34;
            ws.Column(2).Width = 60;

            // Issues summary
            var summaries = a.BuildIssueSummaries();
            AddSheet(wb, "Issues", new[] { "Severity", "Category", "Issue", "Pages affected", "Description", "How to fix" },
                summaries.Select(s => new object[] { s.Definition.Severity.ToString(), s.Definition.Category, s.Definition.Title, s.Count, s.Definition.Description, s.Definition.HowToFix }));

            AddSheet(wb, "Issue details", IssueColumns, IssueRows(a));
            AddSheet(wb, "Pages", PageColumns, a.Pages.Select(PageRow));
            AddSheet(wb, "Links", new[] { "Page", "Link URL", "Anchor text", "Where on page", "Type", "Nofollow", "Status", "Error" },
                a.Pages.SelectMany(p => p.Links.Select(l => new object[] { p.Url, l.Url, l.Text, l.Location, l.IsInternal ? "Internal" : "External", YesNo(l.NoFollow), l.StatusCode?.ToString() ?? "", l.Error ?? "" })));
            // For each crawled page: how visitors reach it and every page that links to it
            AddSheet(wb, "Inlinks", new[] { "Page", "How visitors reach it", "In sitemap", "In navigation", "Linked from page", "Where on that page", "Anchor text" },
                a.Pages.SelectMany(p => p.Origin!.Inlinks.Count > 0
                    ? p.Origin.Inlinks.Select(i => new object[] { p.Url, p.Origin.Summary, p.Origin.SitemapText, p.Origin.NavigationText, i.Url, i.Location, i.Anchor })
                    : new[] { new object[] { p.Url, p.Origin.Summary, p.Origin.SitemapText, p.Origin.NavigationText, "", "", "" } }));
            AddSheet(wb, "Images", new[] { "Page", "Image URL", "Alt text", "Has alt", "Type", "Status", "Error" },
                a.Pages.SelectMany(p => p.Images.Select(i => new object[] { p.Url, i.Url, i.Alt, YesNo(i.HasAlt), i.IsInternal ? "Internal" : "External", i.StatusCode?.ToString() ?? "", i.Error ?? "" })));
            if (a.Pages.Any(p => p.Hreflangs.Count > 0))
                AddSheet(wb, "Hreflang", new[] { "Page", "hreflang", "Alternate URL", "Status", "Error" },
                    a.Pages.SelectMany(p => p.Hreflangs.Select(h => new object[] { p.Url, h.Lang, h.Url, h.StatusCode?.ToString() ?? "", h.Error ?? "" })));
            AddSheet(wb, "Keywords", new[] { "Page", "Keyword", "Count", "Density %" },
                a.Pages.SelectMany(p => p.TopKeywords.Select(k => new object[] { p.Url, k.Keyword, k.Count, k.Density })));

            if (a.PageSpeed.Count > 0)
            {
                AddSheet(wb, "PageSpeed", new[] { "URL", "Device", "Performance", "Accessibility", "Best practices", "SEO", "FCP", "LCP", "CLS", "TBT", "Speed Index", "Field LCP", "Field INP", "Field CLS", "Top opportunities", "Error" },
                    a.PageSpeed.Select(p => new object[] { p.Url, p.Strategy, p.PerformanceScore?.ToString() ?? "", p.AccessibilityScore?.ToString() ?? "", p.BestPracticesScore?.ToString() ?? "", p.SeoScore?.ToString() ?? "",
                        p.Fcp, p.Lcp, p.Cls, p.Tbt, p.SpeedIndex, p.FieldLcp, p.FieldInp, p.FieldCls, string.Join("; ", p.Opportunities.Take(5)), p.Error ?? "" }));
            }

            if (domain != null)
            {
                AddSheet(wb, "Domain overview", new[] { "Metric", "Value" }, new[]
                {
                    new object[] { "Domain", domain.Domain }, new object[] { "Database", domain.Database },
                    new object[] { "Authority Score", domain.AuthorityScore?.ToString() ?? "" },
                    new object[] { "Organic keywords", domain.OrganicKeywords?.ToString() ?? "" },
                    new object[] { "Organic traffic (monthly)", domain.OrganicTraffic?.ToString() ?? "" },
                    new object[] { "Organic traffic cost ($)", domain.OrganicCost?.ToString() ?? "" },
                    new object[] { "Paid keywords", domain.PaidKeywords?.ToString() ?? "" },
                    new object[] { "Backlinks", domain.Backlinks?.ToString() ?? "" },
                    new object[] { "Referring domains", domain.ReferringDomains?.ToString() ?? "" },
                });
                AddSheet(wb, "Organic keywords", new[] { "Keyword", "Position", "Volume", "CPC", "Traffic %", "URL" },
                    domain.TopKeywords.Select(k => new object[] { k.Keyword, k.Position, k.SearchVolume, k.Cpc, k.TrafficPercent, k.Url }));
            }

            wb.SaveAs(path);
        }

        public static void ExportRowsExcel(string path, string sheetName, string[] headers, IEnumerable<object[]> rows)
        {
            using var wb = new XLWorkbook();
            AddSheet(wb, sheetName, headers, rows);
            wb.SaveAs(path);
        }

        private static void AddSheet(XLWorkbook wb, string name, string[] headers, IEnumerable<object[]> rows)
        {
            var ws = wb.Worksheets.Add(name);
            AddTable(ws, 1, headers, rows, name.Replace(" ", ""));
            ws.SheetView.FreezeRows(1);
        }

        private const int MaxHyperlinksPerSheet = 60000;

        private static void AddTable(IXLWorksheet ws, int startRow, string[] headers, IEnumerable<object[]> rows, string tableName)
        {
            for (int c = 0; c < headers.Length; c++) ws.Cell(startRow, c + 1).Value = headers[c];
            // URL columns become clickable hyperlinks (Excel allows about 65,000 per sheet)
            var urlColumns = headers.Select((h, i) => (h, i)).Where(x => x.h.Contains("URL") || x.h.Contains("page", StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToHashSet();
            int links = 0;
            int r = startRow + 1;
            foreach (var row in rows)
            {
                for (int c = 0; c < row.Length; c++)
                {
                    var v = row[c];
                    // Excel caps a cell at 32,767 characters
                    if (v is string s && s.Length > 32000) v = s[..32000];
                    var cell = ws.Cell(r, c + 1);
                    cell.Value = XLCellValue.FromObject(v);
                    if (urlColumns.Contains(c) && links < MaxHyperlinksPerSheet && v is string u &&
                        u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && u.Length < 2000 && Uri.TryCreate(u, UriKind.Absolute, out var uri))
                    {
                        cell.SetHyperlink(new XLHyperlink(uri));
                        links++;
                    }
                }
                r++;
            }
            var range = ws.Range(startRow, 1, Math.Max(r - 1, startRow + 1), headers.Length);
            var table = range.CreateTable(tableName);
            table.Theme = XLTableTheme.TableStyleMedium2;
            for (int c = 1; c <= headers.Length; c++)
            {
                var col = ws.Column(c);
                col.AdjustToContents(startRow, Math.Min(r, startRow + 300));
                if (col.Width > 60) col.Width = 60;
                if (col.Width < 8) col.Width = 8;
            }
        }

        // ------------------------------------------------------------------ PDF

        private const string Accent = "#00A389";
        private const string ErrorColor = "#E5484D";
        private const string WarningColor = "#F2994A";
        private const string NoticeColor = "#3E8EF7";
        private const string Muted = "#6B7280";

        public static void ExportPdf(AuditResult a, string path, DomainMetrics? domain = null, int maxPageRows = 300) =>
            BuildPdf(a, domain, maxPageRows).GeneratePdf(path);

        public static IDocument BuildPdf(AuditResult a, DomainMetrics? domain = null, int maxPageRows = 300)
        {
            var summaries = a.BuildIssueSummaries();

            return Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(32);
                    page.DefaultTextStyle(t => t.FontSize(9.5f).FontFamily("Segoe UI", "Tahoma", "Arial"));

                    page.Header().PaddingBottom(10).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Site Audit Report").FontSize(20).Bold().FontColor(Accent);
                            c.Item().Text(a.StartUrl).FontColor(Muted);
                        });
                        row.ConstantItem(150).AlignRight().Column(c =>
                        {
                            c.Item().AlignRight().Text(a.StartedAt.ToString("dd MMM yyyy, HH:mm")).FontColor(Muted);
                            c.Item().AlignRight().Text($"{a.Pages.Count} pages crawled").FontColor(Muted);
                        });
                    });

                    page.Content().Column(col =>
                    {
                        col.Spacing(14);

                        // Score + totals
                        col.Item().Row(row =>
                        {
                            row.ConstantItem(150).Border(1).BorderColor("#E5E7EB").Padding(12).Column(c =>
                            {
                                c.Item().Text("Site Health").FontColor(Muted);
                                c.Item().Text($"{a.SiteHealth}%").FontSize(34).Bold().FontColor(ScoreColor(a.SiteHealth));
                                c.Item().Height(6).Row(bar =>
                                {
                                    if (a.SiteHealth > 0) bar.RelativeItem(a.SiteHealth).Background(ScoreColor(a.SiteHealth));
                                    if (a.SiteHealth < 100) bar.RelativeItem(100 - a.SiteHealth).Background("#E5E7EB");
                                });
                            });
                            row.ConstantItem(10);
                            StatBox(row.RelativeItem(), "Errors", a.Errors, ErrorColor);
                            row.ConstantItem(10);
                            StatBox(row.RelativeItem(), "Warnings", a.Warnings, WarningColor);
                            row.ConstantItem(10);
                            StatBox(row.RelativeItem(), "Notices", a.Notices, NoticeColor);
                        });

                        if (a.Cancelled)
                            col.Item().Background("#FEF3C7").Padding(8).Text("The audit was stopped before it finished; figures cover the pages crawled so far.");

                        // Status codes
                        var codes = a.Pages.GroupBy(p => p.StatusCode switch
                        {
                            0 => "Failed", >= 500 => "5xx", >= 400 => "4xx", >= 300 => "3xx", _ => "2xx"
                        }).ToDictionary(g => g.Key, g => g.Count());
                        col.Item().Text("Crawled pages by status").FontSize(13).Bold();
                        col.Item().Text(string.Join("     ", new[] { "2xx", "3xx", "4xx", "5xx", "Failed" }
                            .Select(k => $"{k}: {(codes.TryGetValue(k, out var n) ? n : 0)}")));

                        // Site checks
                        col.Item().Text("Site-wide checks").FontSize(13).Bold();
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(3); c.ConstantColumn(55); c.RelativeColumn(6); });
                            t.Header(h => { HeaderCell(h.Cell(), "Check"); HeaderCell(h.Cell(), "Result"); HeaderCell(h.Cell(), "Detail"); });
                            foreach (var c in a.SiteChecks)
                            {
                                BodyCell(t.Cell()).Text(c.Name);
                                BodyCell(t.Cell()).Text(c.Passed ? "Passed" : "Failed").Bold().FontColor(c.Passed ? Accent : SeverityColor(c.Severity));
                                BodyCell(t.Cell()).Text(c.Detail);
                            }
                        });

                        // Issues summary
                        col.Item().Text("Issues").FontSize(13).Bold();
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.ConstantColumn(60); c.RelativeColumn(4); c.ConstantColumn(45); c.RelativeColumn(5); });
                            t.Header(h => { HeaderCell(h.Cell(), "Severity"); HeaderCell(h.Cell(), "Issue"); HeaderCell(h.Cell(), "Pages"); HeaderCell(h.Cell(), "How to fix"); });
                            foreach (var s in summaries)
                            {
                                BodyCell(t.Cell()).Text(s.Definition.Severity.ToString()).Bold().FontColor(SeverityColor(s.Definition.Severity));
                                BodyCell(t.Cell()).Text(s.Definition.Title);
                                BodyCell(t.Cell()).AlignRight().Text(s.Count.ToString());
                                BodyCell(t.Cell()).Text(s.Definition.HowToFix).FontColor(Muted);
                            }
                        });

                        // Affected URLs for errors and warnings
                        foreach (var s in summaries.Where(x => x.Definition.Severity != Severity.Notice))
                        {
                            col.Item().ShowEntire().Column(c =>
                            {
                                c.Item().Text($"{s.Definition.Title} ({s.Count})").Bold().FontColor(SeverityColor(s.Definition.Severity));
                                c.Item().Text(s.Definition.Description).FontColor(Muted).FontSize(8.5f);
                                foreach (var p in s.Pages.Take(15))
                                    c.Item().PaddingLeft(8).Text(t =>
                                    {
                                        t.Span("• " + p.Url).FontSize(8.5f);
                                        if (p.Detail.Length > 0) t.Span("  — " + Trim(p.Detail, 140)).FontSize(8).FontColor(Muted);
                                    });
                                if (s.Count > 15) c.Item().PaddingLeft(8).Text($"…and {s.Count - 15} more (see the Excel export)").FontColor(Muted).FontSize(8);
                            });
                        }

                        // PageSpeed
                        if (a.PageSpeed.Count > 0)
                        {
                            col.Item().Text("Core Web Vitals (Google PageSpeed Insights)").FontSize(13).Bold();
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(4); c.ConstantColumn(45); c.ConstantColumn(40); c.ConstantColumn(45); c.ConstantColumn(45); c.ConstantColumn(45); c.ConstantColumn(45); });
                                t.Header(h => { foreach (var x in new[] { "URL", "Device", "Perf.", "LCP", "CLS", "TBT", "FCP" }) HeaderCell(h.Cell(), x); });
                                foreach (var p in a.PageSpeed)
                                {
                                    BodyCell(t.Cell()).Text(p.Error == null ? p.Url : $"{p.Url} — {p.Error}");
                                    BodyCell(t.Cell()).Text(p.Strategy);
                                    BodyCell(t.Cell()).Text(p.PerformanceScore?.ToString() ?? "-").Bold().FontColor(p.PerformanceScore is int ps ? ScoreColor(ps) : Muted);
                                    BodyCell(t.Cell()).Text(p.Lcp);
                                    BodyCell(t.Cell()).Text(p.Cls);
                                    BodyCell(t.Cell()).Text(p.Tbt);
                                    BodyCell(t.Cell()).Text(p.Fcp);
                                }
                            });
                        }

                        // Domain metrics
                        if (domain != null)
                        {
                            col.Item().Text($"Domain overview — {domain.Domain} ({domain.Database.ToUpperInvariant()}, Semrush data)").FontSize(13).Bold();
                            col.Item().Row(row =>
                            {
                                StatBox(row.RelativeItem(), "Authority Score", domain.AuthorityScore ?? 0, Accent); row.ConstantItem(8);
                                StatBox(row.RelativeItem(), "Organic traffic", domain.OrganicTraffic ?? 0, NoticeColor); row.ConstantItem(8);
                                StatBox(row.RelativeItem(), "Organic keywords", domain.OrganicKeywords ?? 0, NoticeColor); row.ConstantItem(8);
                                StatBox(row.RelativeItem(), "Ref. domains", domain.ReferringDomains ?? 0, WarningColor);
                            });
                            if (domain.TopKeywords.Count > 0)
                                col.Item().Table(t =>
                                {
                                    t.ColumnsDefinition(c => { c.RelativeColumn(3); c.ConstantColumn(45); c.ConstantColumn(60); c.ConstantColumn(50); c.RelativeColumn(4); });
                                    t.Header(h => { foreach (var x in new[] { "Keyword", "Pos.", "Volume", "Traffic %", "URL" }) HeaderCell(h.Cell(), x); });
                                    foreach (var k in domain.TopKeywords.Take(25))
                                    {
                                        BodyCell(t.Cell()).Text(k.Keyword);
                                        BodyCell(t.Cell()).Text(k.Position.ToString());
                                        BodyCell(t.Cell()).Text(k.SearchVolume.ToString("N0"));
                                        BodyCell(t.Cell()).Text(k.TrafficPercent.ToString("0.##"));
                                        BodyCell(t.Cell()).Text(k.Url).FontSize(8);
                                    }
                                });
                        }

                        // Crawled pages
                        col.Item().PageBreak();
                        col.Item().Text("Crawled pages").FontSize(13).Bold();
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(6); c.ConstantColumn(40); c.ConstantColumn(38); c.ConstantColumn(38); c.ConstantColumn(42); c.ConstantColumn(50); });
                            t.Header(h => { foreach (var x in new[] { "URL", "Status", "Score", "Errors", "Warn.", "Load (ms)" }) HeaderCell(h.Cell(), x); });
                            foreach (var p in a.Pages.OrderBy(p => p.Score).Take(maxPageRows))
                            {
                                BodyCell(t.Cell()).Text(t2 =>
                                {
                                    t2.Line(Trim(p.Url, 110)).FontSize(8.5f);
                                    if (p.Title.Length > 0) t2.Line(Trim(p.Title, 110)).FontSize(7.5f).FontColor(Muted);
                                });
                                BodyCell(t.Cell()).Text(p.StatusText).FontColor(p.StatusCode is >= 200 and < 300 ? Accent : ErrorColor);
                                BodyCell(t.Cell()).Text(p.Score.ToString()).Bold().FontColor(ScoreColor(p.Score));
                                BodyCell(t.Cell()).Text(p.ErrorCount.ToString());
                                BodyCell(t.Cell()).Text(p.WarningCount.ToString());
                                BodyCell(t.Cell()).Text(p.ResponseTimeMs.ToString("N0"));
                            }
                        });
                        if (a.Pages.Count > maxPageRows)
                            col.Item().Text($"Showing the {maxPageRows} lowest-scoring of {a.Pages.Count} pages. The Excel export lists them all.").FontColor(Muted);
                    });

                    page.Footer().Row(row =>
                    {
                        row.RelativeItem().Text("Generated by SEO Audit Studio").FontColor(Muted).FontSize(8);
                        row.RelativeItem().AlignRight().Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                            t.Span("Page ");
                            t.CurrentPageNumber();
                            t.Span(" of ");
                            t.TotalPages();
                        });
                    });
                });
            });
        }

        private static void StatBox(IContainer container, string label, long value, string color)
        {
            container.Border(1).BorderColor("#E5E7EB").Padding(12).Column(c =>
            {
                c.Item().Text(label).FontColor(Muted);
                c.Item().Text(value.ToString("N0")).FontSize(24).Bold().FontColor(color);
            });
        }

        private static void HeaderCell(IContainer c, string text) =>
            c.Background("#F3F4F6").BorderBottom(1).BorderColor("#D1D5DB").Padding(4).Text(text).Bold().FontSize(8.5f);

        private static IContainer BodyCell(IContainer c) =>
            c.BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).PaddingHorizontal(4);

        private static string ScoreColor(int score) => score >= 80 ? Accent : score >= 50 ? WarningColor : ErrorColor;

        private static string SeverityColor(Severity s) => s switch
        {
            Severity.Error => ErrorColor,
            Severity.Warning => WarningColor,
            _ => NoticeColor
        };

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
