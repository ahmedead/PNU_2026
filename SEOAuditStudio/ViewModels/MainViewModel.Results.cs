using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using SEOAuditStudio.Models;
using SEOAuditStudio.Services;

namespace SEOAuditStudio.ViewModels
{
    public partial class MainViewModel
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAudit), nameof(AuditTitle), nameof(AuditSubtitle), nameof(ContinueBannerText))]
        [NotifyCanExecuteChangedFor(nameof(RerunAuditCommand), nameof(ExportPdfCommand), nameof(ExportExcelCommand),
            nameof(ExportPagesCsvCommand), nameof(ExportIssuesCsvCommand), nameof(RunPageSpeedTopPagesCommand), nameof(ContinueAuditCommand))]
        private AuditResult? _currentAudit;

        public bool HasAudit => CurrentAudit != null;
        public string AuditTitle => CurrentAudit?.Name ?? "No audit loaded";
        public string AuditSubtitle => CurrentAudit == null ? "Run a new audit or open one from History."
            : $"{CurrentAudit.StartUrl}  ·  {CurrentAudit.StartedAt:dd MMM yyyy HH:mm}  ·  {CurrentAudit.Pages.Count:N0} pages  ·  {CurrentAudit.Duration:hh\\:mm\\:ss}"
              + (CurrentAudit.Cancelled ? "  ·  stopped early" : CurrentAudit.HitPageLimit ? "  ·  page limit reached" : "");

        /// <summary>Dashboard banner for an audit that can usefully be continued; empty otherwise.</summary>
        public string ContinueBannerText => CurrentAudit == null ? ""
            : CurrentAudit.Cancelled ? $"This audit was stopped after {CurrentAudit.Pages.Count:N0} pages. Continue keeps them and crawls the rest."
            : CurrentAudit.HitPageLimit ? $"The crawl reached the page limit ({CurrentAudit.Pages.Count:N0} pages) with more URLs still to visit. Continue crawls up to {Settings.MaxPages:N0} more."
            : "";

        // ---------------------------------------------------------------- dashboard

        [ObservableProperty] private int _siteHealth;
        [ObservableProperty] private int _errorCount;
        [ObservableProperty] private int _warningCount;
        [ObservableProperty] private int _noticeCount;
        [ObservableProperty] private int _pagesCrawled;
        [ObservableProperty] private string _healthDeltaText = "";
        [ObservableProperty] private string _errorDeltaText = "";
        [ObservableProperty] private string _warningDeltaText = "";
        [ObservableProperty] private string _noticeDeltaText = "";
        [ObservableProperty] private string _comparedWithText = "";
        [ObservableProperty] private string _averageResponseText = "";
        [ObservableProperty] private string _indexablePagesText = "";

        public ObservableCollection<StatusBucket> StatusBuckets { get; } = new();
        public ObservableCollection<ThematicReport> ThematicReports { get; } = new();
        public ObservableCollection<SiteCheck> SiteChecks { get; } = new();
        public ObservableCollection<IssueSummary> TopIssues { get; } = new();

        // ---------------------------------------------------------------- issues

        public ObservableCollection<IssueSummary> IssueSummaries { get; } = new();
        public ICollectionView IssuesView { get; private set; } = null!;

        /// <summary>0 all, 1 errors, 2 warnings, 3 notices.</summary>
        [ObservableProperty] private int _issueSeverityFilter;
        [ObservableProperty] private string _issueSearch = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AffectedRows), nameof(SelectedIssueHasTargets), nameof(SelectedIssueHasSources), nameof(SelectedIssueIsPlain), nameof(AffectedCountText))]
        private IssueSummary? _selectedIssue;

        /// <summary>Broken links/images/hreflang get one row per target URL; other issues one row per page.</summary>
        public List<AffectedRow> AffectedRows => SelectedIssue?.Pages.SelectMany(p =>
                p.Targets.Count > 0
                    ? p.Targets.Select(t => new AffectedRow { PageUrl = p.Url, TargetUrl = t.Url, Detail = t.Reason })
                : SelectedIssueHasSources
                    // 4XX / 5XX / redirect: one row per page that links to the URL, so the link can be fixed at its source
                    ? (p.LinkedFrom.Count > 0
                        ? p.LinkedFrom.Select(s => new AffectedRow { PageUrl = p.Url, Detail = p.Detail, SourceUrl = s.Url, SourceNote = s.Reason })
                        : new[] { new AffectedRow { PageUrl = p.Url, Detail = p.Detail, SourceNote = p.FoundVia } })
                : new[] { new AffectedRow { PageUrl = p.Url, Detail = p.Detail } })
            .ToList() ?? new List<AffectedRow>();

        public bool SelectedIssueHasTargets => SelectedIssue?.Pages.Any(p => p.Targets.Count > 0) == true;

        /// <summary>The selected issue is about the page's own URL (4XX, 5XX, failed, redirect): show the pages linking to it.</summary>
        public bool SelectedIssueHasSources => SelectedIssue != null && !SelectedIssueHasTargets &&
                                               AuditResult.InlinkIssueIds.Contains(SelectedIssue.Definition.Id);

        public bool SelectedIssueIsPlain => SelectedIssue != null && !SelectedIssueHasTargets && !SelectedIssueHasSources;

        public string AffectedCountText => SelectedIssue == null ? "" : SelectedIssueHasTargets
            ? $"Affected pages: {SelectedIssue.Count:N0}  ·  URLs: {SelectedIssue.Pages.SelectMany(p => p.Targets).Select(t => t.Url).Distinct().Count():N0}"
            : SelectedIssueHasSources
            ? $"Affected pages: {SelectedIssue.Count:N0}  ·  linked from {SelectedIssue.Pages.SelectMany(p => p.LinkedFrom).Select(s => s.Url).Distinct().Count():N0} page(s)"
            : $"Affected pages: {SelectedIssue.Count:N0}";

        /// <summary>Copies every distinct broken URL of the selected issue (or its pages), one per line.</summary>
        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void CopyAffectedUrls()
        {
            var urls = SelectedIssueHasTargets
                ? AffectedRows.Select(r => r.TargetUrl).Distinct()
                : AffectedRows.Select(r => r.PageUrl).Distinct();
            CopyText(string.Join(Environment.NewLine, urls));
        }

        partial void OnIssueSeverityFilterChanged(int value) => IssuesView?.Refresh();
        partial void OnIssueSearchChanged(string value) => IssuesView?.Refresh();

        // ---------------------------------------------------------------- pages

        public ObservableCollection<PageResult> Pages { get; } = new();
        public ICollectionView PagesView { get; private set; } = null!;

        /// <summary>0 all, 1 with errors, 2 with warnings, 3 2xx, 4 3xx redirects, 5 4xx/5xx/failed, 6 not in navigation, 7 orphan or sitemap only.</summary>
        [ObservableProperty] private int _pageFilter;
        [ObservableProperty] private string _pageSearch = "";
        [ObservableProperty] private string _pagesCountText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedPageIssues))]
        private PageResult? _selectedPage;

        /// <summary>Internal link target → pages linking to it, for the current audit.</summary>
        private Dictionary<string, List<IssueTarget>> _inlinks = new();

        public List<PageIssueRow> SelectedPageIssues
        {
            get
            {
                var page = SelectedPage;
                if (page == null || CurrentAudit == null) return new List<PageIssueRow>();
                return page.Issues
                    .Select(i => (i, d: IssueCatalog.Get(i.IssueId)))
                    .OrderBy(x => x.d.Severity)
                    .Select(x =>
                    {
                        bool sources = x.i.Targets.Count == 0 && AuditResult.InlinkIssueIds.Contains(x.i.IssueId);
                        var linkedFrom = sources ? AuditResult.InlinksFor(page, _inlinks) : new List<IssueTarget>();
                        return new PageIssueRow
                        {
                            Severity = x.d.Severity,
                            Title = x.d.Title,
                            Detail = x.i.Targets.Count > 0 ? $"{x.i.Targets.Count} URL(s) — click to open, or copy:"
                                   : sources ? $"{x.i.Detail} — linked from {linkedFrom.Count} page(s):" : x.i.Detail,
                            HowToFix = x.d.HowToFix,
                            Targets = x.i.Targets,
                            LinkedFrom = linkedFrom,
                            FoundVia = sources && linkedFrom.Count == 0 ? CurrentAudit.FoundVia(page) : ""
                        };
                    })
                    .ToList();
            }
        }

        partial void OnPageFilterChanged(int value) => RefreshPages();
        partial void OnPageSearchChanged(string value) => RefreshPages();

        private void InitViews()
        {
            IssuesView = CollectionViewSource.GetDefaultView(IssueSummaries);
            IssuesView.Filter = o =>
            {
                if (o is not IssueSummary s) return false;
                if (IssueSeverityFilter > 0 && (int)s.Definition.Severity != IssueSeverityFilter - 1) return false;
                return string.IsNullOrWhiteSpace(IssueSearch) ||
                       s.Definition.Title.Contains(IssueSearch, StringComparison.OrdinalIgnoreCase) ||
                       s.Definition.Category.Contains(IssueSearch, StringComparison.OrdinalIgnoreCase);
            };

            PagesView = CollectionViewSource.GetDefaultView(Pages);
            PagesView.Filter = o =>
            {
                if (o is not PageResult p) return false;
                bool pass = PageFilter switch
                {
                    1 => p.ErrorCount > 0,
                    2 => p.WarningCount > 0,
                    3 => p.StatusCode is >= 200 and < 300,
                    4 => p.RedirectChain.Count > 0,
                    5 => p.StatusCode >= 400 || p.StatusCode == 0,
                    6 => p.Origin?.Kind is OriginKind.ContentOnly or OriginKind.SitemapOnly or OriginKind.Orphan,
                    7 => p.Origin?.IsCandidateForRemoval == true,
                    _ => true
                };
                if (!pass) return false;
                return string.IsNullOrWhiteSpace(PageSearch) ||
                       p.Url.Contains(PageSearch, StringComparison.OrdinalIgnoreCase) ||
                       p.Title.Contains(PageSearch, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void RefreshPages()
        {
            PagesView?.Refresh();
            if (PagesView != null)
                PagesCountText = $"{PagesView.Cast<object>().Count():N0} of {Pages.Count:N0} pages";
        }

        /// <summary>Shows an audit (just finished or opened from History) in every results view.</summary>
        internal void LoadAudit(AuditResult audit)
        {
            if (IssuesView == null) InitViews();
            _inlinks = audit.BuildInlinks();
            PageOrigins.Compute(audit);
            bool sameAudit = ReferenceEquals(CurrentAudit, audit);
            CurrentAudit = audit;
            if (sameAudit)
            {
                // A continued audit is the same object, updated: refresh everything bound to it
                OnPropertyChanged(nameof(CurrentAudit));
                OnPropertyChanged(nameof(AuditTitle));
                OnPropertyChanged(nameof(AuditSubtitle));
                OnPropertyChanged(nameof(ContinueBannerText));
            }
            var previous = _storage.FindPrevious(audit);

            SiteHealth = audit.SiteHealth;
            ErrorCount = audit.Errors;
            WarningCount = audit.Warnings;
            NoticeCount = audit.Notices;
            PagesCrawled = audit.Pages.Count;

            if (previous != null)
            {
                HealthDeltaText = Delta(audit.SiteHealth - previous.SiteHealth, "%");
                ErrorDeltaText = Delta(audit.Errors - previous.Errors);
                WarningDeltaText = Delta(audit.Warnings - previous.Warnings);
                NoticeDeltaText = Delta(audit.Notices - previous.Notices);
                ComparedWithText = $"Compared with the audit of {previous.StartedAt:dd MMM yyyy HH:mm}";
            }
            else
            {
                HealthDeltaText = ErrorDeltaText = WarningDeltaText = NoticeDeltaText = "";
                ComparedWithText = "First audit of this site — run it again later to see changes.";
            }

            var ok = audit.Pages.Where(p => p.StatusCode == 200).ToList();
            AverageResponseText = ok.Count == 0 ? "-" : $"{ok.Average(p => p.ResponseTimeMs):N0} ms";
            int indexable = audit.Pages.Count(p => p.StatusCode == 200 && p.IsHtml && p.RedirectChain.Count == 0 &&
                                                   !p.MetaRobots.Contains("noindex", StringComparison.OrdinalIgnoreCase));
            IndexablePagesText = $"{indexable:N0} of {audit.Pages.Count:N0}";

            // Status-code chart
            StatusBuckets.Clear();
            var buckets = new (string Label, Func<PageResult, bool> Match, string Color)[]
            {
                ("2xx OK", p => p.StatusCode is >= 200 and < 300 && p.RedirectChain.Count == 0, "#2BB673"),
                ("3xx Redirect", p => p.RedirectChain.Count > 0 && p.StatusCode is >= 200 and < 300, "#3E8EF7"),
                ("4xx Client error", p => p.StatusCode is >= 300 and < 500, "#F2994A"),
                ("5xx Server error", p => p.StatusCode >= 500, "#E5484D"),
                ("Failed", p => p.StatusCode == 0, "#9B59B6"),
            };
            var counts = buckets.Select(b => audit.Pages.Count(b.Match)).ToArray();
            int max = Math.Max(1, counts.Max());
            for (int i = 0; i < buckets.Length; i++)
                StatusBuckets.Add(new StatusBucket { Label = buckets[i].Label, Count = counts[i], Ratio = (double)counts[i] / max, Color = buckets[i].Color });

            // Issues (with change since the previous audit)
            var summaries = audit.BuildIssueSummaries();
            if (previous != null)
            {
                var prevCounts = previous.BuildIssueSummaries().ToDictionary(s => s.Definition.Id, s => s.Count);
                foreach (var s in summaries) s.Delta = s.Count - (prevCounts.TryGetValue(s.Definition.Id, out var c) ? c : 0);
            }
            IssueSummaries.Clear();
            foreach (var s in summaries) IssueSummaries.Add(s);
            TopIssues.Clear();
            foreach (var s in summaries.Where(s => s.Definition.Severity != Severity.Notice).Take(8)) TopIssues.Add(s);
            SelectedIssue = IssueSummaries.FirstOrDefault();

            // Thematic reports: share of HTML pages without issues in each category
            ThematicReports.Clear();
            var htmlPages = Math.Max(1, audit.Pages.Count);
            foreach (var cat in new[] { "Crawlability", "On-page", "Content", "Links", "Performance", "Security", "Indexability", "Mobile", "Markup", "Accessibility", "International" })
            {
                var inCat = summaries.Where(s => s.Definition.Category == cat && s.Definition.Severity != Severity.Notice).ToList();
                int affected = inCat.SelectMany(s => s.Pages.Select(p => p.Url)).Distinct().Count();
                ThematicReports.Add(new ThematicReport
                {
                    Category = cat,
                    IssueCount = summaries.Where(s => s.Definition.Category == cat).Sum(s => s.Count),
                    PagesAffected = affected,
                    Score = (int)Math.Round(100.0 * (htmlPages - affected) / htmlPages)
                });
            }

            SiteChecks.Clear();
            foreach (var c in audit.SiteChecks.OrderBy(c => c.Passed).ThenBy(c => c.Severity)) SiteChecks.Add(c);

            Pages.Clear();
            foreach (var p in audit.Pages.OrderBy(p => p.Depth).ThenBy(p => p.Url)) Pages.Add(p);
            SelectedPage = Pages.FirstOrDefault();
            RefreshPages();

            PageSpeedResults.Clear();
            foreach (var r in audit.PageSpeed) PageSpeedResults.Add(r);
            if (string.IsNullOrWhiteSpace(PageSpeedUrl) || !PageSpeedUrl.Contains(new Uri(audit.StartUrl).Host))
                PageSpeedUrl = audit.Pages.FirstOrDefault()?.FinalUrl ?? audit.StartUrl;
            if (string.IsNullOrWhiteSpace(DomainInput)) DomainInput = SemrushApiService.CleanDomain(audit.StartUrl);
        }

        private static string Delta(int d, string suffix = "") => d == 0 ? "no change" : d > 0 ? $"+{d}{suffix}" : $"{d}{suffix}";

        /// <summary>Opens the Pages view filtered to one URL (from the issue list).</summary>
        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ShowPage(string? url)
        {
            if (url == null) return;
            PageFilter = 0;
            PageSearch = "";
            SelectedPage = Pages.FirstOrDefault(p => p.Url == url);
            SelectedSection = SectionPages;
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ShowIssue(IssueSummary? issue)
        {
            if (issue == null) return;
            IssueSeverityFilter = 0;
            IssueSearch = "";
            SelectedIssue = issue;
            SelectedSection = SectionIssues;
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ShowIssuesBySeverity(string? severity)
        {
            IssueSearch = "";
            IssueSeverityFilter = int.TryParse(severity, out var s) ? s : 0;
            SelectedSection = SectionIssues;
        }
    }
}
