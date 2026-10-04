using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SPAdminStudio.Models;
using SPAdminStudio.Services;

namespace SPAdminStudio.ViewModels
{
    /// <summary>
    /// Promotes files and folders from the connected site (source, e.g. UAT) to a second
    /// connection (target, e.g. Production): queue → analyze (dry run) → deploy.
    /// </summary>
    public partial class DeploymentViewModel : ObservableObject
    {
        private readonly ISharePointService _source;
        private readonly SharePointRestService _target = new();
        private CancellationTokenSource? _cts;

        public ObservableCollection<ConnectionProfile> Profiles { get; }

        public IReadOnlyList<string> CheckInModes { get; } = new[]
        {
            "Leave checked out",
            "Check in as draft (minor version)",
            "Publish major version"
        };

        // Target connection
        [ObservableProperty] private ConnectionProfile? _targetProfile;
        [ObservableProperty] private string _targetUrl = string.Empty;
        [ObservableProperty] private string _targetPassword = string.Empty;
        [ObservableProperty] private bool _isTargetConnected;
        [ObservableProperty] private string _targetStatus = "Not connected";

        // Source browser
        [ObservableProperty] private string _sourceFolderUrl = string.Empty;
        [ObservableProperty] private SharePointItem? _selectedSourceItem;
        public BulkObservableCollection<SharePointItem> SourceItems { get; } = new();

        // Queue & plan
        public BulkObservableCollection<DeploymentQueueEntry> Queue { get; } = new();
        [ObservableProperty] private DeploymentQueueEntry? _selectedQueueEntry;
        public BulkObservableCollection<DeploymentPlanItem> Plan { get; } = new();
        [ObservableProperty] private bool _isPlanStale = true;

        // Options
        [ObservableProperty] private bool _overwrite = true;
        [ObservableProperty] private bool _backupTarget = true;
        [ObservableProperty] private bool _copyMetadata = true;
        [ObservableProperty] private bool _rewriteUrls = true;
        [ObservableProperty] private bool _approve = true;
        [ObservableProperty] private int _checkInModeIndex = (int)CheckInMode.PublishMajor;
        [ObservableProperty] private string _comment = "Deployed via SPAdminStudio";
        [ObservableProperty] private string _backupFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SPAdminStudio", "Deployments");

        // Progress
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _isDeploying;
        [ObservableProperty] private string _statusMessage = "Connect to the source site, then choose a target profile.";
        [ObservableProperty] private int _progressValue;
        [ObservableProperty] private int _progressMax = 1;
        [ObservableProperty] private string _lastRunFolder = string.Empty;

        public string SourceSiteUrl => _source.CurrentSiteInfo?.Url ?? "(not connected)";
        public string SourceUser => _source.CurrentSiteInfo?.CurrentUserTitle ?? string.Empty;

        public event Action<string>? LogRequested;

        public DeploymentViewModel(ISharePointService source, ObservableCollection<ConnectionProfile> profiles)
        {
            _source = source;
            Profiles = profiles;
            _target.Notice += message => LogRequested?.Invoke("[Target] " + message);
        }

        partial void OnTargetProfileChanged(ConnectionProfile? value)
        {
            TargetUrl = value?.SiteUrl ?? string.Empty;
            TargetPassword = value != null && !string.IsNullOrEmpty(value.EncryptedPassword)
                ? ProfileStorageService.DecryptPassword(value.EncryptedPassword)
                : string.Empty;
        }

        partial void OnOverwriteChanged(bool value) => MarkStale();

        private void MarkStale()
        {
            IsPlanStale = true;
            foreach (var item in Plan.Where(p => p.Status != DeployStatus.Done && p.Status != DeployStatus.Warning && p.Status != DeployStatus.Failed))
            {
                item.Status = DeployStatus.Pending;
                item.Message = "Re-analyze";
            }
        }

        /// <summary>Called when the source connection or web changes.</summary>
        public void OnSourceChanged(bool siteCollectionChanged)
        {
            OnPropertyChanged(nameof(SourceSiteUrl));
            OnPropertyChanged(nameof(SourceUser));

            if (siteCollectionChanged)
            {
                SourceItems.Clear();
                SourceFolderUrl = string.Empty;
                Queue.Clear();
                Plan.Clear();
                IsPlanStale = true;
            }
        }

        public void ClearSession() => OnSourceChanged(siteCollectionChanged: true);

        #region Target connection

        [RelayCommand]
        public async Task ConnectTargetAsync()
        {
            if (TargetProfile == null)
            {
                MessageBox.Show("Select the target (Production) profile first.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var url = (TargetUrl ?? string.Empty).Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                MessageBox.Show("Enter the full target site URL.", "Target URL", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_source.IsConnected && string.Equals(url, _source.CurrentSiteInfo?.Url?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The target is the same site as the source. Choose the Production site.", "Same Site", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Use the profile's credentials against the (possibly edited) URL, without modifying the saved profile
            var profile = new ConnectionProfile
            {
                ProfileName = TargetProfile.ProfileName,
                SiteUrl = url,
                AuthMode = TargetProfile.AuthMode,
                Domain = TargetProfile.Domain,
                Username = TargetProfile.Username,
                TrustAllCertificates = TargetProfile.TrustAllCertificates
            };

            IsBusy = true;
            TargetStatus = $"Connecting to {url}...";
            try
            {
                await _target.ConnectAsync(profile, TargetPassword);
                var info = _target.CurrentSiteInfo!;
                IsTargetConnected = true;
                TargetStatus = $"Connected: {info.Title} as {info.CurrentUserTitle}";
                LogRequested?.Invoke($"Deployment target connected: {info.Url} (site collection '{info.SiteCollectionServerRelativeUrl}') as {info.CurrentUserLogin}");
                MarkStale();
            }
            catch (Exception ex)
            {
                IsTargetConnected = false;
                TargetStatus = $"Connection failed: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Deployment target connection failed: {ex.Message}");
                MessageBox.Show($"Could not connect to the target site:\n{ex.Message}", "Target Connection", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public void DisconnectTarget()
        {
            _target.Disconnect();
            IsTargetConnected = false;
            TargetStatus = "Not connected";
            MarkStale();
        }

        #endregion

        #region Source browser

        public async Task BrowseSourceAsync(string folderUrl)
        {
            if (!_source.IsConnected || string.IsNullOrEmpty(folderUrl)) return;

            IsBusy = true;
            try
            {
                var items = await _source.GetFolderContentsAsync(folderUrl);
                SourceItems.ReplaceAll(items);
                SourceFolderUrl = folderUrl.TrimEnd('/');
                StatusMessage = $"Source: {SourceFolderUrl} — {items.Count(i => i.IsFolder)} folder(s), {items.Count(i => !i.IsFolder)} file(s)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open '{folderUrl}': {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task BrowseLocationAsync(string? location)
        {
            if (!_source.IsConnected)
            {
                StatusMessage = "Connect to the source site first (Connection tab).";
                return;
            }

            try
            {
                string? url = location switch
                {
                    "masterpages" => await _source.GetMasterPageGalleryServerRelativeUrlAsync(),
                    "style" => await _source.GetStyleLibraryServerRelativeUrlAsync(),
                    "pages" => (await _source.GetListsAsync()).FirstOrDefault(l => l.BaseTemplate == 850)?.RootFolderUrl,
                    "sitepages" => (await _source.GetListsAsync()).FirstOrDefault(l => l.BaseTemplate == 119)?.RootFolderUrl,
                    _ => _source.CurrentSiteInfo?.ServerRelativeUrl
                };

                if (string.IsNullOrEmpty(url))
                {
                    StatusMessage = "That library does not exist in the current source site.";
                    return;
                }
                await BrowseSourceAsync(url);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open location: {ex.Message}";
            }
        }

        [RelayCommand]
        public async Task SourceUpAsync()
        {
            if (string.IsNullOrEmpty(SourceFolderUrl)) return;
            var index = SourceFolderUrl.LastIndexOf('/');
            await BrowseSourceAsync(index > 0 ? SourceFolderUrl.Substring(0, index) : "/");
        }

        public async Task OpenSourceItemAsync(SharePointItem item)
        {
            if (item.IsFolder) await BrowseSourceAsync(item.ServerRelativeUrl);
            else AddToQueue(new[] { item });
        }

        [RelayCommand]
        public void AddSelectedToQueue()
        {
            var selected = SourceItems.Where(i => i.IsSelected).ToList();
            if (!selected.Any() && SelectedSourceItem != null) selected.Add(SelectedSourceItem);
            if (!selected.Any())
            {
                StatusMessage = "Tick the files or folders to deploy first.";
                return;
            }

            AddToQueue(selected);
            foreach (var item in selected) item.IsSelected = false;
        }

        #endregion

        #region Queue

        /// <summary>Adds files/folders to the deployment queue (also used by the other tabs). Returns how many were new.</summary>
        public int AddToQueue(IEnumerable<SharePointItem> items)
        {
            int added = 0;
            var list = Queue.ToList();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.ServerRelativeUrl) ||
                    list.Any(q => q.SourceUrl.Equals(item.ServerRelativeUrl, StringComparison.OrdinalIgnoreCase)))
                    continue;

                list.Add(new DeploymentQueueEntry { Name = item.Name, SourceUrl = item.ServerRelativeUrl, IsFolder = item.IsFolder });
                added++;
            }

            if (added > 0)
            {
                Queue.ReplaceAll(list);
                MarkStale();
            }

            StatusMessage = added > 0 ? $"Added {added} item(s) to the deployment queue ({Queue.Count} total)." : "Already in the queue.";
            LogRequested?.Invoke(StatusMessage);
            return added;
        }

        [RelayCommand]
        public void RemoveFromQueue()
        {
            if (SelectedQueueEntry == null) return;
            Queue.Remove(SelectedQueueEntry);
            MarkStale();
        }

        [RelayCommand]
        public void ClearQueue()
        {
            Queue.Clear();
            Plan.Clear();
            IsPlanStale = true;
            StatusMessage = "Queue cleared.";
        }

        #endregion

        #region Analyze / Deploy

        private DeploymentOptions BuildOptions() => new()
        {
            Overwrite = Overwrite,
            BackupTarget = BackupTarget,
            BackupFolder = BackupFolder,
            CopyMetadata = CopyMetadata,
            RewriteUrls = RewriteUrls,
            CheckIn = (CheckInMode)CheckInModeIndex,
            Approve = Approve,
            Comment = string.IsNullOrWhiteSpace(Comment) ? "Deployed via SPAdminStudio" : Comment.Trim()
        };

        private bool EnsureReady()
        {
            if (!_source.IsConnected)
            {
                MessageBox.Show("Connect to the source site first (Connection tab).", "Source Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (!IsTargetConnected)
            {
                MessageBox.Show("Connect to the target (Production) site first.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (Queue.Count == 0)
            {
                MessageBox.Show("Add files or folders to the deployment queue first.", "Queue Empty", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        private DeploymentService CreateEngine()
        {
            var engine = new DeploymentService(_source, _target);
            engine.Log += m => LogRequested?.Invoke(m);
            return engine;
        }

        [RelayCommand]
        public async Task AnalyzeAsync()
        {
            if (!EnsureReady()) return;
            await RunAnalyzeAsync();
        }

        private async Task<bool> RunAnalyzeAsync()
        {
            _cts = new CancellationTokenSource();
            IsBusy = true;
            StatusMessage = "Analyzing...";
            try
            {
                var progress = new Progress<string>(m => StatusMessage = m);
                var plan = await CreateEngine().AnalyzeAsync(Queue.ToList(), BuildOptions(), progress, _cts.Token);
                Plan.ReplaceAll(plan);
                IsPlanStale = false;
                StatusMessage = "Plan: " + Summarize(plan);
                LogRequested?.Invoke($"Deployment plan to {_target.CurrentSiteInfo?.Url}: {Summarize(plan)}");
                return true;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Analysis cancelled.";
                return false;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Analysis failed: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Deployment analysis failed: {ex.Message}");
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string Summarize(IEnumerable<DeploymentPlanItem> plan)
        {
            var groups = plan.GroupBy(p => p.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
            return string.Join(", ", groups);
        }

        [RelayCommand]
        public async Task DeployAsync()
        {
            if (!EnsureReady()) return;
            if ((IsPlanStale || Plan.Count == 0) && !await RunAnalyzeAsync()) return;

            var actionable = Plan.Where(p => p.IsActionable).ToList();
            if (!actionable.Any())
            {
                MessageBox.Show("Nothing to deploy. Check the plan statuses.", "Deploy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = BuildOptions();
            var files = actionable.Count(p => p.Kind == DeployItemKind.File);
            var overwrites = actionable.Count(p => p.Status == DeployStatus.Overwrite);
            var blocked = Plan.Count(p => p.Status == DeployStatus.Blocked);
            var confirm = MessageBox.Show(
                $"Deploy to:\n{_target.CurrentSiteInfo?.Url}\n\n" +
                $"• {files} file(s) — {actionable.Count(p => p.Status == DeployStatus.New && p.Kind == DeployItemKind.File)} new, {overwrites} overwrite\n" +
                $"• {actionable.Count(p => p.Kind == DeployItemKind.Folder)} folder(s)\n" +
                (blocked > 0 ? $"• {blocked} blocked item(s) will be skipped\n" : string.Empty) +
                $"\nBackup of overwritten files: {(options.BackupTarget ? "ON" : "OFF")}\n" +
                $"After upload: {CheckInModes[CheckInModeIndex]}{(options.Approve ? " + approve" : string.Empty)}\n\nContinue?",
                "Confirm Deployment", MessageBoxButton.YesNo, overwrites > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            // Every run gets its own folder: backups of overwritten files + the report
            var host = Uri.TryCreate(_target.BaseUrl, UriKind.Absolute, out var targetUri) ? targetUri.Host : "target";
            var runFolder = Path.Combine(BackupFolder, $"{host}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(runFolder);
            LastRunFolder = runFolder;

            var context = new DeploymentService.RunContext { BackupFolder = options.BackupTarget ? Path.Combine(runFolder, "backup") : string.Empty };
            var engine = CreateEngine();

            _cts = new CancellationTokenSource();
            IsBusy = true;
            IsDeploying = true;
            ProgressMax = actionable.Count;
            ProgressValue = 0;
            LogRequested?.Invoke($"=== Deployment started: {SourceSiteUrl} → {_target.CurrentSiteInfo?.Url} ({actionable.Count} item(s)) ===");

            try
            {
                foreach (var item in actionable)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    StatusMessage = $"Deploying ({ProgressValue + 1}/{actionable.Count}) {item.Name}...";
                    await engine.DeployItemAsync(item, options, context, _cts.Token);
                    ProgressValue++;
                }
                StatusMessage = "Deployment finished: " + Summarize(Plan);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Deployment cancelled: " + Summarize(Plan);
            }
            finally
            {
                IsBusy = false;
                IsDeploying = false;
                IsPlanStale = true;

                var reportPath = Path.Combine(runFolder, "deployment-report.csv");
                try
                {
                    WriteReport(reportPath);
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Could not write deployment report: {ex.Message}");
                }
                LogRequested?.Invoke($"=== Deployment ended: {Summarize(Plan)}. Report: {reportPath} ===");
            }

            var failed = Plan.Count(p => p.Status == DeployStatus.Failed);
            var warnings = Plan.Count(p => p.Status == DeployStatus.Warning);
            MessageBox.Show($"{StatusMessage}\n\nReport and backups:\n{runFolder}",
                "Deployment", MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Error : warnings > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        [RelayCommand]
        public void Cancel() => _cts?.Cancel();

        [RelayCommand]
        public void ExportReport()
        {
            if (Plan.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "Export Deployment Report",
                FileName = $"deployment-report_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Filter = "CSV (*.csv)|*.csv"
            };
            if (dialog.ShowDialog() != true) return;

            WriteReport(dialog.FileName);
            StatusMessage = $"Report saved: {dialog.FileName}";
        }

        private void WriteReport(string path)
        {
            static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

            var sb = new StringBuilder();
            sb.AppendLine("Kind,Status,Source,Target,Size,Message");
            foreach (var p in Plan)
            {
                sb.AppendLine(string.Join(",", Csv(p.Kind.ToString()), Csv(p.Status.ToString()),
                    Csv(_source.ToAbsoluteUrl(p.SourceUrl)), Csv(IsTargetConnected ? _target.ToAbsoluteUrl(p.TargetUrl) : p.TargetUrl),
                    p.Size.ToString(), Csv(p.Message)));
            }
            // UTF-8 with BOM so Excel shows Arabic names correctly
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        [RelayCommand]
        public void OpenBackupFolder()
        {
            var folder = !string.IsNullOrEmpty(LastRunFolder) && Directory.Exists(LastRunFolder) ? LastRunFolder : BackupFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }

        [RelayCommand]
        public void BrowseBackupFolder()
        {
            var dialog = new OpenFolderDialog { Title = "Folder for deployment backups and reports" };
            if (dialog.ShowDialog() == true) BackupFolder = dialog.FolderName;
        }

        #endregion
    }
}
