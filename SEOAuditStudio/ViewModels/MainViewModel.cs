using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEOAuditStudio.Models;
using SEOAuditStudio.Services;

namespace SEOAuditStudio.ViewModels
{
    /// <summary>
    /// The whole app's state. Split by feature: .Audit (input and crawl), .Results (dashboard, issues, pages),
    /// .Tools (PageSpeed, domain overview), .Export, .History.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        // Sidebar order in MainWindow.xaml
        public const int SectionNewAudit = 0;
        public const int SectionDashboard = 1;
        public const int SectionIssues = 2;
        public const int SectionPages = 3;
        public const int SectionPerformance = 4;
        public const int SectionDomain = 5;
        public const int SectionHistory = 6;
        public const int SectionSettings = 7;

        private readonly StorageService _storage = new();

        public AuditSettings Settings { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsResultsSection))]
        private int _selectedSection;

        /// <summary>Dashboard, Issues and Pages show the audit header with the export buttons.</summary>
        public bool IsResultsSection => SelectedSection is SectionDashboard or SectionIssues or SectionPages;

        [RelayCommand]
        private void GoToSection(string? section)
        {
            if (int.TryParse(section, out var s)) SelectedSection = s;
        }

        [ObservableProperty]
        private string _statusText = "Ready";

        public ObservableCollection<string> LogEntries { get; } = new();

        public MainViewModel()
        {
            Settings = _storage.LoadSettings();
            PageSpeedApiKey = Settings.PageSpeedApiKey;
            SemrushApiKey = Settings.SemrushApiKey;
            DomainDatabase = Settings.SemrushDatabase;
            InitViews();
            RefreshHistory();
            ReopenLatestAudit();
        }

        /// <summary>Shows the most recent audit at startup so the results views are not empty.</summary>
        private void ReopenLatestAudit()
        {
            var latest = History.FirstOrDefault();
            if (latest == null) return;
            try
            {
                var audit = _storage.LoadAudit(latest.Id);
                if (audit != null) LoadAudit(audit);
            }
            catch (Exception ex)
            {
                AddLog("Could not reopen the last audit: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- settings

        // API keys are bound through these so the PasswordBox-like fields can be cleared without touching Settings until saved
        [ObservableProperty]
        private string _pageSpeedApiKey = "";

        [ObservableProperty]
        private string _semrushApiKey = "";

        [RelayCommand]
        private void SaveSettings()
        {
            Settings.PageSpeedApiKey = PageSpeedApiKey.Trim();
            Settings.SemrushApiKey = SemrushApiKey.Trim();
            Settings.SemrushDatabase = string.IsNullOrWhiteSpace(DomainDatabase) ? "us" : DomainDatabase.Trim();
            Settings.Concurrency = Math.Clamp(Settings.Concurrency, 1, 32);
            Settings.MaxPages = Math.Clamp(Settings.MaxPages, 1, 100_000);
            Settings.TimeoutSeconds = Math.Clamp(Settings.TimeoutSeconds, 5, 300);
            try
            {
                _storage.SaveSettings(Settings);
                StatusText = "Settings saved";
            }
            catch (Exception ex)
            {
                ShowError("Could not save settings", ex);
            }
        }

        [RelayCommand]
        private void OpenDataFolder() => OpenPath(_storage.Folder);

        // ---------------------------------------------------------------- shared helpers

        [RelayCommand]
        private void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            OpenPath(url);
        }

        [RelayCommand]
        private void CopyText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); StatusText = "Copied to clipboard"; } catch { /* clipboard busy */ }
        }

        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* no handler */ }
        }

        internal void AddLog(string message)
        {
            void Add()
            {
                LogEntries.Add($"{DateTime.Now:HH:mm:ss}  {message}");
                while (LogEntries.Count > 1000) LogEntries.RemoveAt(0);
            }
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Add(); else d.BeginInvoke(Add);
        }

        private void ShowError(string title, Exception ex)
        {
            AddLog($"{title}: {ex.Message}");
            StatusText = title;
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
