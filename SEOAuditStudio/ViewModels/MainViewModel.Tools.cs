using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEOAuditStudio.Models;
using SEOAuditStudio.Services;

namespace SEOAuditStudio.ViewModels
{
    public partial class MainViewModel
    {
        private readonly PageSpeedService _pageSpeed = new();
        private readonly SemrushApiService _semrush = new();
        private CancellationTokenSource? _toolCts;

        // ---------------------------------------------------------------- PageSpeed / Core Web Vitals

        [ObservableProperty] private string _pageSpeedUrl = "";
        /// <summary>0 mobile, 1 desktop, 2 both.</summary>
        [ObservableProperty] private int _pageSpeedStrategy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunPageSpeedCommand), nameof(RunPageSpeedTopPagesCommand), nameof(LoadDomainCommand), nameof(CancelToolCommand))]
        private bool _isToolBusy;

        [ObservableProperty] private string _toolStatus = "";
        [ObservableProperty] private PageSpeedResult? _selectedPageSpeed;

        public ObservableCollection<PageSpeedResult> PageSpeedResults { get; } = new();

        private bool CanRunTool() => !IsToolBusy;

        [RelayCommand(CanExecute = nameof(CanRunTool))]
        private async Task RunPageSpeed()
        {
            var url = UrlInputService.Normalize(PageSpeedUrl);
            if (url == null) { ToolStatus = "Enter a valid URL."; return; }
            await RunPageSpeedForAsync(new[] { url });
        }

        private bool CanRunTopPages() => !IsToolBusy && HasAudit;

        /// <summary>Tests the start page and the next most linked-to pages of the current audit.</summary>
        [RelayCommand(CanExecute = nameof(CanRunTopPages))]
        private async Task RunPageSpeedTopPages()
        {
            if (CurrentAudit == null) return;
            var inbound = CurrentAudit.Pages.SelectMany(p => p.Links).Where(l => l.IsInternal)
                .GroupBy(l => l.Url).ToDictionary(g => g.Key, g => g.Count());
            var urls = CurrentAudit.Pages
                .Where(p => p.StatusCode == 200 && p.IsHtml)
                .OrderBy(p => p.Depth)
                .ThenByDescending(p => inbound.TryGetValue(p.Url, out var n) ? n : 0)
                .Select(p => p.FinalUrl).Distinct().Take(5).ToList();
            await RunPageSpeedForAsync(urls);
        }

        private async Task RunPageSpeedForAsync(IReadOnlyList<string> urls)
        {
            var strategies = PageSpeedStrategy switch { 1 => new[] { "desktop" }, 2 => new[] { "mobile", "desktop" }, _ => new[] { "mobile" } };
            IsToolBusy = true;
            _toolCts = new CancellationTokenSource();
            try
            {
                int i = 0, total = urls.Count * strategies.Length;
                foreach (var url in urls)
                {
                    foreach (var strategy in strategies)
                    {
                        ToolStatus = $"Testing {url} ({strategy}) — {++i} of {total}. Each test takes 15-60 seconds...";
                        var r = await _pageSpeed.RunAsync(url, strategy, PageSpeedApiKey, _toolCts.Token);
                        // Replace an older result for the same URL and device
                        var old = PageSpeedResults.FirstOrDefault(x => x.Url == r.Url && x.Strategy == r.Strategy);
                        if (old != null) PageSpeedResults.Remove(old);
                        PageSpeedResults.Insert(0, r);
                        SelectedPageSpeed = r;
                        if (CurrentAudit != null)
                        {
                            CurrentAudit.PageSpeed.RemoveAll(x => x.Url == r.Url && x.Strategy == r.Strategy);
                            CurrentAudit.PageSpeed.Add(r);
                        }
                        AddLog(r.Error == null ? $"PageSpeed {strategy} {url}: performance {r.PerformanceScore}, LCP {r.Lcp}, CLS {r.Cls}"
                                               : $"PageSpeed {strategy} {url} failed: {r.Error}");
                    }
                }
                ToolStatus = "PageSpeed tests finished.";
                if (CurrentAudit != null)
                {
                    try { _storage.SaveAudit(CurrentAudit); } catch { /* history is best effort */ }
                }
            }
            catch (OperationCanceledException) { ToolStatus = "Cancelled."; }
            finally
            {
                IsToolBusy = false;
                _toolCts.Dispose();
                _toolCts = null;
            }
        }

        private bool CanCancelTool() => IsToolBusy;

        [RelayCommand(CanExecute = nameof(CanCancelTool))]
        private void CancelTool() => _toolCts?.Cancel();

        // ---------------------------------------------------------------- Domain overview (Semrush API)

        [ObservableProperty] private string _domainInput = "";
        [ObservableProperty] private string _domainDatabase = "us";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDomainMetrics))]
        private DomainMetrics? _domainMetrics;

        [ObservableProperty] private string _domainStatus = "";

        public bool HasDomainMetrics => DomainMetrics != null;

        public string[] SemrushDatabases { get; } =
            { "us", "uk", "ca", "au", "de", "fr", "es", "it", "nl", "in", "ae", "sa", "eg", "kw", "qa", "bh", "om", "jo", "tr", "br", "mx", "jp" };

        [RelayCommand(CanExecute = nameof(CanRunTool))]
        private async Task LoadDomain()
        {
            if (string.IsNullOrWhiteSpace(DomainInput)) { DomainStatus = "Enter a domain, for example example.com"; return; }
            if (string.IsNullOrWhiteSpace(SemrushApiKey))
            {
                DomainStatus = "A Semrush API key is required (Settings → Semrush API key). Traffic, keyword and backlink figures come from Semrush's database and cannot be measured by crawling.";
                return;
            }
            IsToolBusy = true;
            _toolCts = new CancellationTokenSource();
            DomainStatus = "Loading from Semrush...";
            try
            {
                DomainMetrics = await _semrush.GetDomainAsync(DomainInput, DomainDatabase, SemrushApiKey, AddLog, _toolCts.Token);
                DomainStatus = $"Semrush data for {DomainMetrics.Domain} ({DomainMetrics.Database.ToUpperInvariant()} database), loaded {DateTime.Now:HH:mm}.";
            }
            catch (OperationCanceledException) { DomainStatus = "Cancelled."; }
            catch (Exception ex)
            {
                DomainStatus = ex.Message;
                AddLog("Semrush: " + ex.Message);
            }
            finally
            {
                IsToolBusy = false;
                _toolCts.Dispose();
                _toolCts = null;
            }
        }
    }
}
