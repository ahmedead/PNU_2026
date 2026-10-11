namespace SEOAuditStudio.Models
{
    /// <summary>An issue on the selected page, with its catalog text, for the page detail panel.</summary>
    public sealed class PageIssueRow
    {
        public Severity Severity { get; init; }
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string HowToFix { get; init; } = "";
        public List<IssueTarget> Targets { get; init; } = new();
        public List<IssueTarget> LinkedFrom { get; init; } = new();
        public string FoundVia { get; init; } = "";
    }

    /// <summary>
    /// One row of the "affected pages" grid: the page, and (for broken links/images/hreflang) the URL it points to.
    /// </summary>
    public sealed class AffectedRow
    {
        public string PageUrl { get; init; } = "";
        public string TargetUrl { get; init; } = "";
        public string Detail { get; init; } = "";
        /// <summary>A crawled page that links to PageUrl (for 4XX/5XX/redirect issues).</summary>
        public string SourceUrl { get; init; } = "";
        /// <summary>Anchor text of that link, or how the page was found when nothing links to it.</summary>
        public string SourceNote { get; init; } = "";
    }

    /// <summary>One bar of the status-code chart on the dashboard.</summary>
    public sealed class StatusBucket
    {
        public string Label { get; init; } = "";
        public int Count { get; init; }
        /// <summary>Bar width as a share of the largest bucket (0-1).</summary>
        public double Ratio { get; init; }
        public string Color { get; init; } = "#888";
    }

    /// <summary>Semrush-style thematic report: share of pages free of issues in one category.</summary>
    public sealed class ThematicReport
    {
        public string Category { get; init; } = "";
        public int Score { get; init; }
        public int IssueCount { get; init; }
        public int PagesAffected { get; init; }
    }
}
