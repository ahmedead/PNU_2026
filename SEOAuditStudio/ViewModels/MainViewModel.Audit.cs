using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SEOAuditStudio.Models;
using SEOAuditStudio.Services;

namespace SEOAuditStudio.ViewModels
{
    public partial class MainViewModel
    {
        private CancellationTokenSource? _auditCts;

        /// <summary>0 = crawl a website, 1 = audit a list of URLs.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCrawlMode), nameof(IsListMode))]
        private int _auditModeIndex;

        public bool IsCrawlMode => AuditModeIndex == 0;
        public bool IsListMode => AuditModeIndex == 1;

        [ObservableProperty]
        private string _startUrl = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UrlListCountText))]
        private string _urlListText = "";

        public string UrlListCountText
        {
            get
            {
                var n = UrlInputService.ParseText(UrlListText).Count;
                return n == 0 ? "No valid URLs yet" : $"{n:N0} valid URL(s)";
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartAuditCommand), nameof(StopAuditCommand), nameof(RerunAuditCommand), nameof(ContinueAuditCommand), nameof(TogglePauseCommand))]
        private bool _isRunning;

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private double _progressMax = 1;

        [ObservableProperty]
        private bool _progressIndeterminate;

        [ObservableProperty]
        private string _progressText = "";

        [ObservableProperty]
        private string _currentUrl = "";

        [ObservableProperty]
        private string _importedFileName = "";

        public ObservableCollection<string> LiveLog { get; } = new();

        [RelayCommand]
        private void ImportFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import URLs",
                Filter = "Excel or CSV|*.xlsx;*.xlsm;*.csv;*.txt|Excel workbook (*.xlsx)|*.xlsx;*.xlsm|CSV file (*.csv)|*.csv|Text file (*.txt)|*.txt"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var urls = Path.GetExtension(dlg.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    ? UrlInputService.ParseText(File.ReadAllText(dlg.FileName))
                    : UrlInputService.ImportFile(dlg.FileName);
                if (urls.Count == 0)
                {
                    MessageBox.Show("No URLs were found in that file. Put the URLs in a column (ideally headed \"URL\").",
                        "Import URLs", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                AuditModeIndex = 1;
                UrlListText = string.Join(Environment.NewLine, urls);
                ImportedFileName = Path.GetFileName(dlg.FileName);
                StatusText = $"Imported {urls.Count:N0} URL(s) from {ImportedFileName}";
                AddLog(StatusText);
            }
            catch (Exception ex)
            {
                ShowError("Could not import the file", ex);
            }
        }

        [RelayCommand]
        private async Task LoadSitemapAsList()
        {
            // Handy for "audit exactly what my sitemap lists"
            var url = UrlInputService.Normalize(StartUrl);
            if (url == null) { MessageBox.Show("Enter the website address first.", "Sitemap"); return; }
            try
            {
                StatusText = "Reading sitemap...";
                var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(Settings.UserAgent);
                var xml = await http.GetStringAsync(origin + "/sitemap.xml");
                var urls = System.Text.RegularExpressions.Regex.Matches(xml, @"<loc>\s*(.*?)\s*</loc>")
                    .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
                if (urls.Count == 0) { StatusText = "The sitemap has no URLs"; return; }
                AuditModeIndex = 1;
                UrlListText = string.Join(Environment.NewLine, urls);
                StatusText = $"Loaded {urls.Count:N0} URL(s) from the sitemap";
            }
            catch (Exception ex)
            {
                ShowError("Could not read /sitemap.xml", ex);
            }
        }

        private bool CanStart() => !IsRunning;

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAudit()
        {
            List<string> urls;
            AuditMode mode;
            if (IsCrawlMode)
            {
                var url = UrlInputService.Normalize(StartUrl);
                if (url == null)
                {
                    MessageBox.Show("Enter a valid website address, for example https://example.com", "New audit",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                StartUrl = url;
                urls = new List<string> { url };
                mode = AuditMode.CrawlSite;
            }
            else
            {
                urls = UrlInputService.ParseText(UrlListText);
                if (urls.Count == 0)
                {
                    MessageBox.Show("Paste URLs (one per line) or import a CSV / Excel file first.", "New audit",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                mode = AuditMode.UrlList;
            }
            await RunAuditAsync(mode, urls);
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task RerunAudit()
        {
            if (CurrentAudit == null) return;
            var urls = CurrentAudit.Mode == AuditMode.CrawlSite
                ? new List<string> { CurrentAudit.StartUrl }
                : (CurrentAudit.InputUrls.Count > 0 ? CurrentAudit.InputUrls : CurrentAudit.Pages.Select(p => p.Url).ToList());
            await RunAuditAsync(CurrentAudit.Mode, urls);
        }

        private bool CanContinue() => !IsRunning && CurrentAudit?.CanContinue == true;

        /// <summary>
        /// Continues the current audit: keeps its pages and crawls the links they found but nobody visited yet
        /// (or the rest of the URL list), then recomputes the report. Works for audits saved before this existed.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanContinue))]
        private async Task ContinueAudit()
        {
            var audit = CurrentAudit;
            if (audit == null) return;
            var what = audit.Mode == AuditMode.CrawlSite
                ? $"It keeps the {audit.Pages.Count:N0} page(s) already crawled and visits up to {Settings.MaxPages:N0} more (Max pages), " +
                  "starting with the links found but not crawled yet."
                : $"It audits the {Math.Max(0, audit.InputUrls.Count - audit.Pages.Count):N0} URL(s) of the list not done yet.";
            if (MessageBox.Show($"Continue the audit of {audit.Name}?\n\n{what}\n\nThe report is then recomputed over all pages and saved in place of this audit.",
                    "Continue audit", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await RunAuditAsync(audit.Mode, new List<string>(), resume: audit);
        }

        [RelayCommand]
        private async Task ContinueHistory(AuditSummary? summary)
        {
            if (summary == null || IsRunning) return;
            OpenHistory(summary);
            if (CurrentAudit?.Id == summary.Id) await ContinueAudit();
        }

        private bool CanStop() => IsRunning;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private void StopAudit()
        {
            _auditCts?.Cancel();
            ProgressText = "Stopping and saving...";
        }

        // ---------------------------------------------------------------- pause / resume

        private PauseGate? _pauseGate;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PauseButtonText), nameof(PauseButtonGlyph))]
        private bool _isPaused;

        public string PauseButtonText => IsPaused ? "Resume" : "Pause";
        public string PauseButtonGlyph => IsPaused ? "" : ""; // Play / Pause

        [RelayCommand(CanExecute = nameof(CanStop))]
        private void TogglePause()
        {
            if (_pauseGate == null) return;
            if (_pauseGate.IsPaused)
            {
                _pauseGate.Resume();
                IsPaused = false;
                StatusText = "Audit resumed";
                AddLog("Audit resumed");
            }
            else
            {
                _pauseGate.Pause();
                IsPaused = true;
                ProgressText = "Pausing — finishing the requests in progress...";
                StatusText = "Audit paused. Resume to carry on, or Stop to save what is done so far.";
                AddLog("Audit paused");
            }
        }

        private Task? _runTask;

        /// <summary>
        /// Called when the window closes. While an audit runs (or is paused), offer to stop it and save the pages
        /// crawled so far so it can be continued later. Returns false to keep the window open.
        /// </summary>
        public async Task<bool> ConfirmCloseAsync()
        {
            if (!IsRunning || _runTask == null) return true;
            var answer = MessageBox.Show(
                "An audit is still running.\n\nYes — stop it, save the pages crawled so far, and close. You can Continue it later from History.\nNo — keep the app open.",
                "Close SEO Audit Studio", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
            _auditCts?.Cancel();
            try { await _runTask; } catch { /* already reported */ }
            return true;
        }

        private async Task RunAuditAsync(AuditMode mode, List<string> urls, AuditResult? resume = null)
        {
            var run = RunAuditCoreAsync(mode, urls, resume);
            _runTask = run;
            await run;
        }

        private async Task RunAuditCoreAsync(AuditMode mode, List<string> urls, AuditResult? resume)
        {
            SaveSettings();
            IsRunning = true;
            IsPaused = false;
            LiveLog.Clear();
            ProgressValue = 0;
            ProgressMax = 1;
            ProgressIndeterminate = true;
            SelectedSection = SectionNewAudit;
            _auditCts = new CancellationTokenSource();
            _pauseGate = new PauseGate();

            var auditor = new SiteAuditor();
            var dispatcher = Application.Current.Dispatcher;
            auditor.Log += msg => dispatcher.BeginInvoke(() =>
            {
                LiveLog.Add(msg);
                AddLog(msg);
            });
            auditor.Progress += p => dispatcher.BeginInvoke(() =>
            {
                ProgressIndeterminate = p.Total == 0;
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Done;
                ProgressText = p.Phase == "Paused"
                    ? $"Paused — {p.Done:N0} / {p.Total:N0}. Press Resume to carry on."
                    : p.Total > 0 ? $"{p.Phase}: {p.Done:N0} / {p.Total:N0}" : p.Phase + "...";
                CurrentUrl = p.CurrentUrl;
            });

            var label = resume != null ? $"continuing {resume.Name}" : mode == AuditMode.CrawlSite ? urls[0] : $"{urls.Count:N0} URLs";
            AddLog($"Audit started: {label}");
            LiveLog.Add($"Audit started: {label}");
            StatusText = resume != null ? "Continuing the audit..." : "Auditing...";
            try
            {
                var result = await auditor.RunAsync(mode, urls, Settings, _auditCts.Token, resume, _pauseGate);
                try { _storage.SaveAudit(result); }
                catch (Exception ex) { AddLog("Could not save the audit to history: " + ex.Message); }
                RefreshHistory();
                LoadAudit(result);
                var state = result.Cancelled ? "stopped (use Continue to crawl the rest)"
                          : result.HitPageLimit ? "reached the page limit (use Continue to crawl more)"
                          : "finished";
                var summary = $"Audit {state}: {result.Pages.Count:N0} pages, Site Health {result.SiteHealth}%, " +
                              $"{result.Errors:N0} errors, {result.Warnings:N0} warnings, {result.Notices:N0} notices ({result.Duration:hh\\:mm\\:ss})";
                AddLog(summary);
                StatusText = summary;
                SelectedSection = SectionDashboard;
            }
            catch (Exception ex)
            {
                ShowError("The audit failed", ex);
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
                _pauseGate = null;
                ProgressIndeterminate = false;
                ProgressText = "";
                CurrentUrl = "";
                _auditCts.Dispose();
                _auditCts = null;
                ContinueAuditCommand.NotifyCanExecuteChanged();
            }
        }
    }
}
