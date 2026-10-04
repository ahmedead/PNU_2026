using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SPAdminStudio.Models;
using SPAdminStudio.Services;

namespace SPAdminStudio.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        // Tab order in MainWindow.xaml
        public const int TabConnection = 0;
        public const int TabSiteExplorer = 1;
        public const int TabMasterPages = 2;
        public const int TabStyleLibrary = 3;
        public const int TabEditor = 4;
        public const int TabBackup = 5;
        public const int TabDeploy = 6;

        private readonly ISharePointService _spService;
        private readonly ProfileStorageService _profileStorage;

        public ConnectionViewModel ConnectionVM { get; }
        public SiteExplorerViewModel SiteExplorerVM { get; }
        public DeploymentViewModel DeploymentVM { get; }
        public MasterPageGalleryViewModel MasterPageVM { get; }
        public StyleLibraryViewModel StyleLibraryVM { get; }
        public CodeViewerViewModel CodeViewerVM { get; }
        public BackupViewModel BackupVM { get; }

        [ObservableProperty]
        private int _selectedTabIndex;

        [ObservableProperty]
        private string _connectionStatusText = "Not Connected";

        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private string _siteTitle = "SharePoint Admin Studio";

        [ObservableProperty]
        private string _userInfo = string.Empty;

        [ObservableProperty]
        private ObservableCollection<string> _logEntries = new();

        public MainViewModel()
        {
            var restService = new SharePointRestService();
            restService.Notice += message => AddLog(message);
            _spService = restService;
            _profileStorage = new ProfileStorageService();

            ConnectionVM = new ConnectionViewModel(_spService, _profileStorage);
            MasterPageVM = new MasterPageGalleryViewModel(_spService);
            StyleLibraryVM = new StyleLibraryViewModel(_spService);
            CodeViewerVM = new CodeViewerViewModel(_spService);
            BackupVM = new BackupViewModel(_spService);
            SiteExplorerVM = new SiteExplorerViewModel(_spService);
            DeploymentVM = new DeploymentViewModel(_spService, ConnectionVM.Profiles);

            // Wire up logging
            ConnectionVM.LogRequested += AddLog;
            SiteExplorerVM.LogRequested += AddLog;
            DeploymentVM.LogRequested += AddLog;
            MasterPageVM.LogRequested += AddLog;
            StyleLibraryVM.LogRequested += AddLog;
            CodeViewerVM.LogRequested += AddLog;
            BackupVM.LogRequested += AddLog;

            // Wire up connection callbacks
            ConnectionVM.ConnectedSuccessfully += OnConnectedSuccessfully;
            ConnectionVM.Disconnected += OnDisconnected;
            ConnectionVM.CanChangeConnection = () => ClearSessionAndTabsAsync(promptIfDirty: true);

            // Wire up editor requests
            MasterPageVM.OpenFileInEditorRequested += OpenItemInEditor;
            StyleLibraryVM.OpenFileInEditorRequested += OpenItemInEditor;
            SiteExplorerVM.OpenFileInEditorRequested += OpenItemInEditor;

            // Site navigation (subsites, parent site, address bar)
            SiteExplorerVM.NavigateToWebRequested += NavigateToWebAsync;
            SiteExplorerVM.SiteInfoChanged += UpdateConnectionHeader;
            MasterPageVM.SiteSettingsChanged += () => _ = SiteExplorerVM.RefreshSummaryAsync();

            AddLog("SPAdminStudio initialized. Ready for SharePoint 2013/2016/2019 On-Premises.");
        }

        public async Task<bool> ClearSessionAndTabsAsync(bool promptIfDirty = true)
        {
            // Close all editor tabs (and prompt to save unsaved files if promptIfDirty is true)
            if (!await CodeViewerVM.CloseAllDocumentsAsync(promptIfDirty))
            {
                return false; // User cancelled
            }

            // Clear Master Page Gallery state
            MasterPageVM.ClearSession();

            // Clear Style Library state
            StyleLibraryVM.ClearSession();

            // Clear Backup state
            BackupVM.ClearSession();

            SiteExplorerVM.ClearSession();
            DeploymentVM.ClearSession();

            // Reset header connection info
            IsConnected = false;
            SiteTitle = "SharePoint Admin Studio";
            UserInfo = string.Empty;
            ConnectionStatusText = "Not Connected";

            AddLog("Cleared previous session and editor tabs.");
            return true;
        }

        private async void OnDisconnected()
        {
            await ClearSessionAndTabsAsync(promptIfDirty: false);
            SelectedTabIndex = TabConnection;
            AddLog("Disconnected from SharePoint. Returned to connection panel.");
        }

        private async void OnConnectedSuccessfully()
        {
            // Ensure any previous session artifacts are cleared
            await ClearSessionAndTabsAsync(promptIfDirty: false);

            IsConnected = _spService.IsConnected;
            var site = _spService.CurrentSiteInfo;
            if (site != null)
            {
                UpdateConnectionHeader();
                AddLog($"Active session established for '{site.Title}' ({site.Url})");

                // Auto load initial data
                DeploymentVM.OnSourceChanged(siteCollectionChanged: true);
                _ = SiteExplorerVM.LoadAsync();
                _ = MasterPageVM.LoadItemsAsync();
                _ = StyleLibraryVM.LoadRootAsync();

                // Land on the site contents, like SharePoint Designer
                SelectedTabIndex = TabSiteExplorer;
            }
        }

        private void UpdateConnectionHeader()
        {
            var site = _spService.CurrentSiteInfo;
            if (site == null) return;

            SiteTitle = site.Title;
            UserInfo = $"{site.CurrentUserTitle} ({site.CurrentUserLogin})";
            ConnectionStatusText = $"Connected: {site.Url}";
        }

        /// <summary>
        /// Moves the session to another web with the same credentials.
        /// Subsites and parent sites share the site collection, so open editor tabs and the
        /// Master Page / Style Library views stay valid; any other site requires closing them.
        /// </summary>
        public async Task NavigateToWebAsync(string webUrl, bool sameSiteCollection)
        {
            if (!_spService.IsConnected) return;

            if (!sameSiteCollection && !await CodeViewerVM.CloseAllDocumentsAsync(promptIfDirty: true))
            {
                AddLog("Site navigation cancelled by user.");
                return;
            }

            var previousSiteCollection = _spService.CurrentSiteInfo?.SiteCollectionServerRelativeUrl;
            AddLog($"Opening site '{webUrl}'...");
            SiteExplorerVM.StatusMessage = $"Opening {webUrl}...";

            try
            {
                await _spService.SwitchWebAsync(webUrl);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Could not open site '{webUrl}': {ex.Message}");
                SiteExplorerVM.StatusMessage = $"Could not open site: {ex.Message}";
                MessageBox.Show($"Could not open site:\n{webUrl}\n\n{ex.Message}", "Open Site", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var site = _spService.CurrentSiteInfo!;
            UpdateConnectionHeader();
            AddLog($"Now working in '{site.Title}' ({site.Url})");

            await SiteExplorerVM.LoadAsync();

            var siteCollectionChanged = !string.Equals(previousSiteCollection, site.SiteCollectionServerRelativeUrl, StringComparison.OrdinalIgnoreCase);
            DeploymentVM.OnSourceChanged(siteCollectionChanged);

            if (siteCollectionChanged)
            {
                // Different site collection: its Master Page Gallery and Style Library are different
                MasterPageVM.ClearSession();
                StyleLibraryVM.ClearSession();
                BackupVM.ClearSession();
                _ = MasterPageVM.LoadItemsAsync();
                _ = StyleLibraryVM.LoadRootAsync();
                AddLog($"Switched site collection to '{site.SiteCollectionServerRelativeUrl}'. Reloaded branding libraries.");
            }
        }

        /// <summary>Sends the selection of another tab to the UAT → Production deployment queue.</summary>
        [RelayCommand]
        public void AddToDeployment(string? from)
        {
            var items = from switch
            {
                "masterpages" => MasterPageVM.GetSelectedOrCurrentItems(),
                "style" => StyleLibraryVM.GetSelectedOrCurrentFiles(),
                "explorer" => SiteExplorerVM.GetSelectedFiles(),
                _ => new System.Collections.Generic.List<SharePointItem>()
            };

            if (items.Count == 0)
            {
                MessageBox.Show("Select (tick) the files or folders to deploy first.", "Add to Deployment", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DeploymentVM.AddToQueue(items);
            SelectedTabIndex = TabDeploy;
        }

        [RelayCommand]
        public void GoToTab(string? index)
        {
            if (int.TryParse(index, out var tab)) SelectedTabIndex = tab;
        }

        public void OpenItemInEditor(SharePointItem item)
        {
            if (EditorDocument.IsTextFile(item.Name))
            {
                SelectedTabIndex = TabEditor;
            }
            _ = CodeViewerVM.LoadFileAsync(item);
        }

        public void AddLog(string message)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogEntries.Insert(0, $"[{timestamp}] {message}");
                if (LogEntries.Count > 1000) LogEntries.RemoveAt(LogEntries.Count - 1);
            });
        }

        [RelayCommand]
        public void ClearLog()
        {
            LogEntries.Clear();
            AddLog("Log cleared.");
        }
    }
}
