using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SPAdminStudio.Models;
using SPAdminStudio.Services;
using SPAdminStudio.Views;

namespace SPAdminStudio.ViewModels
{
    /// <summary>
    /// SharePoint Designer-style "Site Objects" explorer for the currently connected web.
    /// Each section is loaded lazily the first time it is opened and cached until Refresh or a web switch.
    /// </summary>
    public partial class SiteExplorerViewModel : ObservableObject
    {
        private readonly ISharePointService _spService;
        private readonly HashSet<SiteSection> _loadedSections = new();
        private int _generation; // Incremented per web so late results from a previous web are dropped
        private int _busyCount;
        private string _webRootUrl = string.Empty;

        public IReadOnlyList<SiteSectionItem> Sections { get; } = new List<SiteSectionItem>
        {
            new() { Section = SiteSection.Summary, Icon = "🏠", Title = "Site Summary" },
            new() { Section = SiteSection.ListsAndLibraries, Icon = "📚", Title = "Lists and Libraries" },
            new() { Section = SiteSection.Subsites, Icon = "🌐", Title = "Subsites" },
            new() { Section = SiteSection.AllFiles, Icon = "🗂️", Title = "All Files" },
            new() { Section = SiteSection.ContentTypes, Icon = "🧩", Title = "Content Types" },
            new() { Section = SiteSection.SiteColumns, Icon = "🏷️", Title = "Site Columns" },
            new() { Section = SiteSection.Groups, Icon = "👥", Title = "Site Groups" },
            new() { Section = SiteSection.Permissions, Icon = "🔐", Title = "Site Permissions" }
        };

        [ObservableProperty] private SiteSectionItem? _selectedSection;
        [ObservableProperty] private int _activeView;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _statusMessage = "Connect to a site to explore its contents.";
        [ObservableProperty] private string _addressUrl = string.Empty;
        [ObservableProperty] private string _filterText = string.Empty;
        [ObservableProperty] private bool _showHidden;

        // Summary
        [ObservableProperty] private SiteDetails? _details;
        [ObservableProperty] private string _editTitle = string.Empty;
        [ObservableProperty] private string _editDescription = string.Empty;
        [ObservableProperty] private SPWebInfo? _parentWeb;

        // Collections
        public BulkObservableCollection<SPListInfo> Lists { get; } = new();
        public BulkObservableCollection<SPWebInfo> Subsites { get; } = new();
        public BulkObservableCollection<SPContentTypeInfo> ContentTypes { get; } = new();
        public BulkObservableCollection<SPFieldInfo> Fields { get; } = new();
        public BulkObservableCollection<SPGroupInfo> Groups { get; } = new();
        public BulkObservableCollection<SPUserInfo> GroupMembers { get; } = new();
        public BulkObservableCollection<SPRoleAssignmentInfo> RoleAssignments { get; } = new();
        public BulkObservableCollection<SPListItemInfo> ListItems { get; } = new();
        public BulkObservableCollection<SharePointItem> Files { get; } = new();

        public ICollectionView ListsView { get; }
        public ICollectionView SubsitesView { get; }
        public ICollectionView ContentTypesView { get; }
        public ICollectionView FieldsView { get; }
        public ICollectionView FilesView { get; }

        [ObservableProperty] private SPListInfo? _selectedList;
        [ObservableProperty] private SPWebInfo? _selectedSubsite;
        [ObservableProperty] private SPGroupInfo? _selectedGroup;
        [ObservableProperty] private SharePointItem? _selectedFile;
        [ObservableProperty] private SPListInfo? _currentList;
        [ObservableProperty] private string _filesFolderUrl = string.Empty;

        public bool CanGoToParent => ParentWeb != null;
        public bool CanGoUpFolder => !string.IsNullOrEmpty(FilesFolderUrl) &&
                                     !FilesFolderUrl.TrimEnd('/').Equals(_webRootUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                                     FilesFolderUrl.TrimEnd('/').Length > _webRootUrl.TrimEnd('/').Length;

        public event Action<string>? LogRequested;
        public event Action<SharePointItem>? OpenFileInEditorRequested;

        /// <summary>Raised to move the session to another web. bool = target is in the same site collection.</summary>
        public event Func<string, bool, Task>? NavigateToWebRequested;

        public SiteExplorerViewModel(ISharePointService spService)
        {
            _spService = spService;

            ListsView = CreateView(Lists, nameof(SPListInfo.Kind), o => o is SPListInfo l &&
                (ShowHidden || !l.Hidden) && Matches(l.Title, l.TemplateName, l.Description));
            SubsitesView = CreateView(Subsites, null, o => o is SPWebInfo w && Matches(w.Title, w.ServerRelativeUrl, w.Description));
            ContentTypesView = CreateView(ContentTypes, nameof(SPContentTypeInfo.Group), o => o is SPContentTypeInfo c &&
                (ShowHidden || !c.Hidden) && Matches(c.Name, c.Group, c.Description));
            FieldsView = CreateView(Fields, nameof(SPFieldInfo.Group), o => o is SPFieldInfo f &&
                (ShowHidden || !f.Hidden) && Matches(f.Title, f.InternalName, f.Group, f.TypeAsString));
            FilesView = CreateView(Files, null, o => o is SharePointItem i && Matches(i.Name));

            SelectedSection = Sections[0];
        }

        private static ICollectionView CreateView<T>(ObservableCollection<T> source, string? groupBy, Predicate<object> filter)
        {
            var view = new ListCollectionView(source) { Filter = filter };
            if (groupBy != null) view.GroupDescriptions.Add(new PropertyGroupDescription(groupBy));
            return view;
        }

        private bool Matches(params string[] values) =>
            string.IsNullOrWhiteSpace(FilterText) ||
            values.Any(v => !string.IsNullOrEmpty(v) && v.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase));

        private IDisposable BeginBusy(string message)
        {
            _busyCount++;
            IsBusy = true;
            StatusMessage = message;
            return new BusyScope(this);
        }

        private sealed class BusyScope : IDisposable
        {
            private SiteExplorerViewModel? _owner;
            public BusyScope(SiteExplorerViewModel owner) => _owner = owner;
            public void Dispose()
            {
                if (_owner == null) return;
                _owner._busyCount = Math.Max(0, _owner._busyCount - 1);
                _owner.IsBusy = _owner._busyCount > 0;
                _owner = null;
            }
        }

        partial void OnFilterTextChanged(string value) => RefreshViews();
        partial void OnShowHiddenChanged(bool value) => RefreshViews();

        private void RefreshViews()
        {
            ListsView.Refresh();
            SubsitesView.Refresh();
            ContentTypesView.Refresh();
            FieldsView.Refresh();
            FilesView.Refresh();
        }

        partial void OnParentWebChanged(SPWebInfo? value) => OnPropertyChanged(nameof(CanGoToParent));
        partial void OnFilesFolderUrlChanged(string value) => OnPropertyChanged(nameof(CanGoUpFolder));

        partial void OnSelectedSectionChanged(SiteSectionItem? value)
        {
            if (value == null) return;
            ActiveView = (int)value.Section;
            FilterText = string.Empty;
            _ = EnsureSectionLoadedAsync(value.Section, force: false);
        }

        partial void OnSelectedGroupChanged(SPGroupInfo? value)
        {
            GroupMembers.Clear();
            if (value != null) _ = LoadGroupMembersAsync(value);
        }

        public void ClearSession()
        {
            _generation++;
            _loadedSections.Clear();
            Details = null;
            ParentWeb = null;
            EditTitle = string.Empty;
            EditDescription = string.Empty;
            AddressUrl = string.Empty;
            FilesFolderUrl = string.Empty;
            _webRootUrl = string.Empty;
            CurrentList = null;
            Lists.Clear();
            Subsites.Clear();
            ContentTypes.Clear();
            Fields.Clear();
            Groups.Clear();
            GroupMembers.Clear();
            RoleAssignments.Clear();
            ListItems.Clear();
            Files.Clear();
            StatusMessage = "Connect to a site to explore its contents.";
            if (ActiveView == (int)SiteSection.ListItems) ActiveView = (int)SiteSection.ListsAndLibraries;
        }

        /// <summary>Called after connecting or switching webs: resets caches and loads the visible section.</summary>
        public async Task LoadAsync()
        {
            ClearSession();
            if (!_spService.IsConnected || _spService.CurrentSiteInfo == null) return;

            AddressUrl = _spService.CurrentSiteInfo.Url;
            _webRootUrl = _spService.CurrentSiteInfo.ServerRelativeUrl;

            var summaryTask = EnsureSectionLoadedAsync(SiteSection.Summary, force: true);
            var current = SelectedSection?.Section ?? SiteSection.Summary;
            if (current != SiteSection.Summary)
            {
                await EnsureSectionLoadedAsync(current, force: true);
            }
            await summaryTask;
        }

        private async Task EnsureSectionLoadedAsync(SiteSection section, bool force)
        {
            if (!_spService.IsConnected) return;
            if (!force && _loadedSections.Contains(section)) return;

            var generation = _generation;
            var title = Sections.FirstOrDefault(s => s.Section == section)?.Title ?? section.ToString();

            using var busy = BeginBusy($"Loading {title}...");
            try
            {
                switch (section)
                {
                    case SiteSection.Summary:
                        var detailsTask = _spService.GetSiteDetailsAsync();
                        var parentTask = _spService.GetParentWebAsync();
                        var details = await detailsTask;
                        var parent = await parentTask;
                        if (generation != _generation) return;
                        Details = details;
                        EditTitle = details.Title;
                        EditDescription = details.Description;
                        ParentWeb = parent;
                        break;

                    case SiteSection.ListsAndLibraries:
                        var lists = await _spService.GetListsAsync();
                        if (generation != _generation) return;
                        Lists.ReplaceAll(lists);
                        break;

                    case SiteSection.Subsites:
                        var webs = await _spService.GetSubsitesAsync();
                        if (generation != _generation) return;
                        Subsites.ReplaceAll(webs);
                        break;

                    case SiteSection.AllFiles:
                        await LoadFolderCoreAsync(string.IsNullOrEmpty(FilesFolderUrl) ? _webRootUrl : FilesFolderUrl, generation);
                        break;

                    case SiteSection.ContentTypes:
                        var cts = await _spService.GetContentTypesAsync();
                        if (generation != _generation) return;
                        ContentTypes.ReplaceAll(cts);
                        break;

                    case SiteSection.SiteColumns:
                        var fields = await _spService.GetSiteColumnsAsync();
                        if (generation != _generation) return;
                        Fields.ReplaceAll(fields);
                        break;

                    case SiteSection.Groups:
                        var groups = await _spService.GetGroupsAsync();
                        if (generation != _generation) return;
                        Groups.ReplaceAll(groups);
                        break;

                    case SiteSection.Permissions:
                        var roles = await _spService.GetRoleAssignmentsAsync();
                        if (generation != _generation) return;
                        RoleAssignments.ReplaceAll(roles);
                        break;

                    case SiteSection.ListItems:
                        if (CurrentList != null) await LoadListItemsCoreAsync(CurrentList, generation);
                        return;
                }

                _loadedSections.Add(section);
                StatusMessage = BuildSectionStatus(section);
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                StatusMessage = $"Failed to load {title}: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Site Explorer - {title}: {ex.Message}");
            }
        }

        private string BuildSectionStatus(SiteSection section) => section switch
        {
            SiteSection.Summary => $"Site: {Details?.Title}",
            SiteSection.ListsAndLibraries => $"{Lists.Count(l => l.IsLibrary)} libraries, {Lists.Count(l => !l.IsLibrary)} lists ({Lists.Count(l => l.Hidden)} hidden)",
            SiteSection.Subsites => Subsites.Count == 0 ? "This site has no subsites." : $"{Subsites.Count} subsite(s)",
            SiteSection.AllFiles => $"{Files.Count(f => f.IsFolder)} folder(s), {Files.Count(f => !f.IsFolder)} file(s)",
            SiteSection.ContentTypes => $"{ContentTypes.Count} content types available",
            SiteSection.SiteColumns => $"{Fields.Count} site columns available",
            SiteSection.Groups => $"{Groups.Count} site group(s)",
            SiteSection.Permissions => $"{RoleAssignments.Count} permission assignment(s) — {Details?.PermissionsDisplay}",
            _ => string.Empty
        };

        public void ReturnToSelectedSection()
        {
            if (SelectedSection == null || ActiveView == (int)SelectedSection.Section) return;
            if (ActiveView == (int)SiteSection.ListItems)
            {
                CloseListItems();
                return;
            }
            ActiveView = (int)SelectedSection.Section;
        }

        private async Task SelectSectionAsync(SiteSection section)
        {
            if (SelectedSection?.Section == section)
            {
                ActiveView = (int)section;
                await EnsureSectionLoadedAsync(section, force: false);
            }
            else
            {
                SelectedSection = Sections.First(s => s.Section == section);
            }
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            var view = (SiteSection)ActiveView;
            await EnsureSectionLoadedAsync(view, force: true);
            if (view != SiteSection.Summary) await EnsureSectionLoadedAsync(SiteSection.Summary, force: true);
        }

        public Task RefreshSummaryAsync() => EnsureSectionLoadedAsync(SiteSection.Summary, force: true);

        #region Site navigation

        [RelayCommand]
        public async Task GoToParentAsync()
        {
            if (ParentWeb == null || NavigateToWebRequested == null) return;
            await NavigateToWebRequested(ParentWeb.Url, true);
        }

        [RelayCommand]
        public async Task OpenSubsiteAsync(SPWebInfo? web)
        {
            web ??= SelectedSubsite;
            if (web == null || NavigateToWebRequested == null) return;
            await NavigateToWebRequested(web.Url, true);
        }

        [RelayCommand]
        public async Task GoToAddressAsync()
        {
            var url = AddressUrl?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("Enter a full site URL, e.g. https://portal/sites/hr", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (NavigateToWebRequested == null) return;

            var sameSite = string.Equals(uri.AbsoluteUri.TrimEnd('/'), _spService.CurrentSiteInfo?.Url?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            if (sameSite)
            {
                await RefreshAsync();
                return;
            }
            await NavigateToWebRequested(uri.AbsoluteUri, false);
        }

        [RelayCommand]
        public void OpenInBrowser(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) url = _spService.CurrentSiteInfo?.Url;
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                Process.Start(new ProcessStartInfo(_spService.ToAbsoluteUrl(url)) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Could not open browser: {ex.Message}");
            }
        }

        [RelayCommand]
        public async Task SaveSiteInfoAsync()
        {
            if (Details == null) return;
            if (string.IsNullOrWhiteSpace(EditTitle))
            {
                MessageBox.Show("The site title cannot be empty.", "Site Title", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            using var busy = BeginBusy("Saving site information...");
            try
            {
                await _spService.UpdateSiteInfoAsync(EditTitle.Trim(), EditDescription?.Trim() ?? string.Empty);
                LogRequested?.Invoke($"Updated site title/description for {Details.Url}");
                SiteInfoChanged?.Invoke();
                await EnsureSectionLoadedAsync(SiteSection.Summary, force: true);
                StatusMessage = "Site information saved.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to save site information: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Update site info failed: {ex.Message}");
                MessageBox.Show($"Failed to save site information:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public event Action? SiteInfoChanged;

        /// <summary>
        /// Finds out which requests reach the SharePoint API from this machine through this URL.
        /// Probe POSTs carry no security digest, so SharePoint rejects them before doing anything:
        /// nothing is checked out, uploaded or changed.
        /// </summary>
        [RelayCommand]
        public async Task DiagnoseWriteAccessAsync()
        {
            if (!_spService.IsConnected) return;

            using var busy = BeginBusy("Diagnosing write access...");
            var results = new List<WriteProbeResult>();
            var spdProbes = new List<WriteProbeResult>();
            var post = System.Net.Http.HttpMethod.Post;
            const string probeName = "spadminstudio-probe.txt"; // never created: the request has no digest

            try
            {
                results.Add(await _spService.ProbeAsync("Read the site (API)", System.Net.Http.HttpMethod.Get, "_api/web?$select=Title"));
                results.Add(await _spService.ProbeAsync("Get security token (_api/contextinfo)", post, "_api/contextinfo"));

                var gallery = await _spService.GetMasterPageGalleryServerRelativeUrlAsync();
                var sample = (await _spService.GetFolderFilesAsync(gallery))
                    .FirstOrDefault(f => f.Name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".master", StringComparison.OrdinalIgnoreCase));
                if (sample != null)
                {
                    results.Add(await _spService.ProbeAsync($"Check out '{sample.Name}' (Master Page Gallery)", post,
                        $"_api/web/GetFileByServerRelativeUrl('{SharePointRestService.SafeUrl(sample.ServerRelativeUrl)}')/CheckOut()"));
                }
                results.Add(await _spService.ProbeAsync("Upload to Master Page Gallery", post,
                    $"_api/web/GetFolderByServerRelativeUrl('{SharePointRestService.SafeUrl(gallery)}')/Files/Add(url='{probeName}',overwrite=false)"));

                var style = await _spService.GetStyleLibraryServerRelativeUrlAsync();
                results.Add(await _spService.ProbeAsync("Upload to Style Library", post,
                    $"_api/web/GetFolderByServerRelativeUrl('{SharePointRestService.SafeUrl(style)}')/Files/Add(url='{probeName}',overwrite=false)"));

                var pages = (await _spService.GetListsAsync()).FirstOrDefault(l => l.BaseTemplate == 850)?.RootFolderUrl;
                if (!string.IsNullOrEmpty(pages))
                {
                    var page = (await _spService.GetFolderFilesAsync(pages)).FirstOrDefault();
                    if (page != null)
                    {
                        results.Add(await _spService.ProbeAsync($"Check out '{page.Name}' (Pages)", post,
                            $"_api/web/GetFileByServerRelativeUrl('{SharePointRestService.SafeUrl(page.ServerRelativeUrl)}')/CheckOut()"));
                    }
                    results.Add(await _spService.ProbeAsync("Upload to Pages", post,
                        $"_api/web/GetFolderByServerRelativeUrl('{SharePointRestService.SafeUrl(pages)}')/Files/Add(url='{probeName}',overwrite=false)"));
                }

                results.Add(await _spService.ProbeAsync("Client object model endpoint (client.svc)", post, "_vti_bin/client.svc/ProcessQuery"));

                // The protocols SharePoint Designer uses — both probes are read-only calls:
                // "server version" (FrontPage RPC) and GetListCollection (Lists web service).
                spdProbes.Add(await _spService.ProbeAsync("SharePoint Designer: FrontPage RPC (author.dll) — check-out, check-in, save", post, "_vti_bin/_vti_aut/author.dll",
                    requestBody: "method=server+version%3a15.0.0.0000&service_name=%2f", requestContentType: "application/x-www-form-urlencoded"));
                spdProbes.Add(await _spService.ProbeAsync("SharePoint Designer: Lists web service (lists.asmx) — major check-in, approve, properties", post, "_vti_bin/lists.asmx",
                    requestBody: "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                                 "<soap:Body><GetListCollection xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\" /></soap:Body></soap:Envelope>",
                    requestContentType: "text/xml; charset=utf-8"));
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Write diagnosis stopped: {ex.Message}");
            }

            var readOk = results.FirstOrDefault()?.ReachedApi == true;
            var writes = results.Skip(2).Where(r => r.Name != "Client object model endpoint (client.svc)").ToList();
            var spdOpen = spdProbes.Where(p => p.ReachedApi).Select(p => p.Name).ToList();
            string conclusion;
            if (!readOk)
                conclusion = "Even reading through the API fails from this address.";
            else if (writes.Count > 0 && writes.All(w => !w.ReachedApi) && spdOpen.Count > 0)
                conclusion = "The REST API writes this app uses are blocked on the way in, but these SharePoint Designer / Office " +
                             $"channels get through:\n  • {string.Join("\n  • ", spdOpen)}\n" +
                             "That is why SharePoint Designer can check out and save here. This app now switches to the same channels " +
                             "automatically when the REST API is blocked (the switch is noted in the Activity Log).";
            else if (writes.Count > 0 && writes.All(w => !w.ReachedApi))
                conclusion = "Reading works, but every write request is stopped before it reaches SharePoint's API.\n" +
                             "This is a network / reverse-proxy rule on this address, not a permission or app problem.\n" +
                             "Connect through the internal authoring URL of this farm (ask your SharePoint admin which " +
                             "Alternate Access Mapping zone authors use), or ask the network team to allow these requests.";
            else if (writes.Any(w => !w.ReachedApi))
                conclusion = "Some write requests are blocked (✘ above) while others reach SharePoint — a path-based rule " +
                             "on the reverse proxy / firewall. Share this report with the network team.";
            else
                conclusion = "All write requests reach SharePoint. If an action still fails, the error message shows SharePoint's reason " +
                             "(use 🔑 Check My Permissions).";

            var report = $"Address: {_spService.CurrentSiteInfo?.Url}\nAccount: {_spService.CurrentSiteInfo?.CurrentUserLogin}\n\n" +
                         string.Join("\n\n", results.Select(r => r.Describe())) +
                         (spdProbes.Count > 0 ? "\n\n— SharePoint Designer / Office channels —\n\n" + string.Join("\n\n", spdProbes.Select(r => r.Describe())) : string.Empty) +
                         $"\n\nCONCLUSION\n{conclusion}\n\n(No data was changed: probe requests carry no security token, so SharePoint refuses them.)";

            LogRequested?.Invoke("Write access diagnosis:\n" + report);
            StatusMessage = "Write access diagnosis finished — full report in the Activity Log.";
            MessageBox.Show(report, "Write Access Diagnosis", MessageBoxButton.OK,
                writes.Any(w => !w.ReachedApi) ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>
        /// Read-only check of what the connected account may do in the branding / publishing libraries.
        /// Separates "no permission" from other causes when check-out, save or deploy fails.
        /// </summary>
        [RelayCommand]
        public async Task CheckPermissionsAsync()
        {
            if (!_spService.IsConnected) return;

            using var busy = BeginBusy("Checking permissions...");
            var lines = new List<string>();
            try
            {
                var lists = await _spService.GetListsAsync();
                var targets = new List<(string Name, string? Url)>
                {
                    ("Master Page Gallery", await _spService.GetMasterPageGalleryServerRelativeUrlAsync()),
                    ("Style Library", await _spService.GetStyleLibraryServerRelativeUrlAsync()),
                    ("Pages", lists.FirstOrDefault(l => l.BaseTemplate == 850)?.RootFolderUrl),
                    ("Site Pages", lists.FirstOrDefault(l => l.BaseTemplate == 119)?.RootFolderUrl)
                };

                foreach (var (name, url) in targets)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    var permissions = await _spService.GetEffectivePermissionsAsync(url);
                    lines.Add(permissions == null
                        ? $"{name} ({url}):\n    no access reported"
                        : $"{name} ({url}):\n    {permissions.Describe()}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Permission check failed: {ex.Message}");
            }

            var account = _spService.CurrentSiteInfo?.CurrentUserLogin;
            var report = $"Account: {account}\nSite: {_spService.CurrentSiteInfo?.Url}\n\n{string.Join("\n\n", lines)}";
            LogRequested?.Invoke("Permission check — " + report.Replace("\n", " "));
            StatusMessage = "Permission check finished.";
            MessageBox.Show(report + "\n\nCheck-out, saving and deploying need ✔ Edit (and ✔ Add for new files); approving needs ✔ Approve.",
                "My Permissions", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Lists and libraries

        [RelayCommand]
        public async Task OpenListAsync(SPListInfo? list)
        {
            list ??= SelectedList;
            if (list == null) return;

            if (list.IsLibrary && !string.IsNullOrEmpty(list.RootFolderUrl))
            {
                // Libraries open in the All Files browser, like SharePoint Designer
                FilesFolderUrl = list.RootFolderUrl;
                _loadedSections.Remove(SiteSection.AllFiles);
                await SelectSectionAsync(SiteSection.AllFiles);
                return;
            }

            CurrentList = list;
            ActiveView = (int)SiteSection.ListItems;
            await LoadListItemsCoreAsync(list, _generation);
        }

        [RelayCommand]
        public void CloseListItems()
        {
            CurrentList = null;
            ListItems.Clear();
            ActiveView = (int)SiteSection.ListsAndLibraries;
            StatusMessage = BuildSectionStatus(SiteSection.ListsAndLibraries);
        }

        private async Task LoadListItemsCoreAsync(SPListInfo list, int generation)
        {
            using var busy = BeginBusy($"Loading items of '{list.Title}'...");
            try
            {
                var items = await _spService.GetListItemsAsync(list.Id);
                if (generation != _generation) return;
                ListItems.ReplaceAll(items);
                StatusMessage = list.ItemCount > items.Count
                    ? $"Showing first {items.Count} of {list.ItemCount} item(s) in '{list.Title}'"
                    : $"{items.Count} item(s) in '{list.Title}'";
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                StatusMessage = $"Failed to load items: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Load items of '{list.Title}' failed: {ex.Message}");
            }
        }

        #endregion

        #region Groups

        private async Task LoadGroupMembersAsync(SPGroupInfo group)
        {
            var generation = _generation;
            using var busy = BeginBusy($"Loading members of '{group.Title}'...");
            try
            {
                var members = await _spService.GetGroupMembersAsync(group.Id);
                if (generation != _generation || SelectedGroup != group) return;
                GroupMembers.ReplaceAll(members);
                StatusMessage = $"'{group.Title}': {members.Count} member(s)";
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                StatusMessage = $"Cannot read members of '{group.Title}': {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Group members '{group.Title}': {ex.Message}");
            }
        }

        #endregion

        #region All Files

        private async Task LoadFolderCoreAsync(string folderUrl, int generation)
        {
            var items = await _spService.GetFolderContentsAsync(folderUrl);
            if (generation != _generation) return;
            FilesFolderUrl = folderUrl.TrimEnd('/');
            Files.ReplaceAll(items);
        }

        public async Task BrowseFolderAsync(string folderUrl)
        {
            var generation = _generation;
            using var busy = BeginBusy($"Loading '{folderUrl}'...");
            try
            {
                await LoadFolderCoreAsync(folderUrl, generation);
                FilterText = string.Empty;
                StatusMessage = BuildSectionStatus(SiteSection.AllFiles);
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                StatusMessage = $"Failed to open folder: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Open folder '{folderUrl}' failed: {ex.Message}");
            }
        }

        [RelayCommand]
        public async Task OpenFileItemAsync(SharePointItem? item)
        {
            item ??= SelectedFile;
            if (item == null) return;

            if (item.IsFolder)
            {
                await BrowseFolderAsync(item.ServerRelativeUrl);
            }
            else
            {
                OpenFileInEditorRequested?.Invoke(item);
            }
        }

        [RelayCommand]
        public async Task FolderUpAsync()
        {
            if (!CanGoUpFolder) return;
            var index = FilesFolderUrl.TrimEnd('/').LastIndexOf('/');
            var parent = index > 0 ? FilesFolderUrl.Substring(0, index) : _webRootUrl;
            await BrowseFolderAsync(parent);
        }

        [RelayCommand]
        public async Task FolderHomeAsync() => await BrowseFolderAsync(_webRootUrl);

        public List<SharePointItem> GetSelectedFiles()
        {
            var selected = Files.Where(f => f.IsSelected).ToList();
            if (!selected.Any() && SelectedFile != null) selected.Add(SelectedFile);
            return selected;
        }

        [RelayCommand]
        public async Task DownloadFilesAsync()
        {
            var targets = GetSelectedFiles().Where(f => !f.IsFolder).ToList();
            if (!targets.Any())
            {
                MessageBox.Show("Select one or more files to download.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog { Title = "Select Destination Folder" };
            if (dialog.ShowDialog() != true) return;

            using var busy = BeginBusy("Downloading...");
            int count = 0;
            var failed = new List<string>();
            foreach (var file in targets)
            {
                try
                {
                    StatusMessage = $"Downloading ({count + 1}/{targets.Count}) {file.Name}...";
                    var bytes = await _spService.DownloadFileAsync(file.ServerRelativeUrl);
                    await File.WriteAllBytesAsync(Path.Combine(dialog.FolderName, file.Name), bytes);
                    count++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{file.Name}: {ex.Message}");
                    LogRequested?.Invoke($"[ERROR] Download '{file.ServerRelativeUrl}' failed: {ex.Message}");
                }
            }

            StatusMessage = $"Downloaded {count} of {targets.Count} file(s) to {dialog.FolderName}";
            LogRequested?.Invoke(StatusMessage);
            if (failed.Any())
            {
                MessageBox.Show($"Downloaded {count} of {targets.Count} file(s).\n\nFailed:\n• {string.Join("\n• ", failed.Take(5))}",
                    "Download Completed With Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand]
        public async Task UploadFilesAsync()
        {
            if (string.IsNullOrEmpty(FilesFolderUrl)) return;

            var dialog = new OpenFileDialog { Title = $"Upload to '{FilesFolderUrl}'", Multiselect = true, Filter = "All Files (*.*)|*.*" };
            if (dialog.ShowDialog() != true) return;

            var existing = dialog.FileNames.Select(Path.GetFileName)
                .Where(n => Files.Any(f => !f.IsFolder && f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (existing.Any())
            {
                var res = MessageBox.Show($"These file(s) already exist and will be overwritten:\n• {string.Join("\n• ", existing.Take(10))}\n\nContinue?",
                    "Overwrite Files", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            var folder = FilesFolderUrl;
            int count = 0;
            var failed = new List<string>();
            using (BeginBusy("Uploading..."))
            {
                foreach (var path in dialog.FileNames)
                {
                    var name = Path.GetFileName(path);
                    try
                    {
                        StatusMessage = $"Uploading ({count + 1}/{dialog.FileNames.Length}) {name}...";
                        var bytes = await File.ReadAllBytesAsync(path);
                        await _spService.UploadFileAsync(folder, name, bytes, overwrite: true);
                        LogRequested?.Invoke($"Uploaded {name} to {folder}");
                        count++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{name}: {ex.Message}");
                        LogRequested?.Invoke($"[ERROR] Upload '{name}' failed: {ex.Message}");
                    }
                }
            }

            await BrowseFolderAsync(folder);
            if (failed.Any())
            {
                MessageBox.Show($"Uploaded {count} of {dialog.FileNames.Length} file(s).\n\nFailed:\n• {string.Join("\n• ", failed.Take(5))}",
                    "Upload Completed With Errors", MessageBoxButton.OK, count == 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
            }
        }

        [RelayCommand]
        public async Task NewFolderAsync()
        {
            if (string.IsNullOrEmpty(FilesFolderUrl)) return;

            var dlg = new NewFolderDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            var folder = FilesFolderUrl;
            using (BeginBusy($"Creating folder '{dlg.FolderName}'..."))
            {
                try
                {
                    await _spService.CreateFolderAsync(folder, dlg.FolderName);
                    LogRequested?.Invoke($"Created folder {folder}/{dlg.FolderName}");
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Create folder failed: {ex.Message}");
                    MessageBox.Show($"Failed to create folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            await BrowseFolderAsync(folder);
        }

        [RelayCommand]
        public async Task RecycleFilesAsync()
        {
            var targets = GetSelectedFiles();
            if (!targets.Any()) return;

            var names = string.Join("\n• ", targets.Take(10).Select(t => (t.IsFolder ? "📁 " : "") + t.Name));
            if (targets.Count > 10) names += $"\n... and {targets.Count - 10} more";
            var res = MessageBox.Show($"Move {targets.Count} item(s) to the site Recycle Bin?\n• {names}\n\nFolders are recycled with all their contents. Items can be restored from the Recycle Bin.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            var folder = FilesFolderUrl;
            int count = 0;
            using (BeginBusy("Recycling..."))
            {
                foreach (var item in targets)
                {
                    try
                    {
                        await _spService.RecycleAsync(item.ServerRelativeUrl, item.IsFolder);
                        LogRequested?.Invoke($"Recycled {item.ServerRelativeUrl}");
                        count++;
                    }
                    catch (Exception ex)
                    {
                        LogRequested?.Invoke($"[ERROR] Recycle '{item.ServerRelativeUrl}' failed: {ex.Message}");
                    }
                }
            }

            await BrowseFolderAsync(folder);
            StatusMessage = $"Moved {count} of {targets.Count} item(s) to the Recycle Bin.";
        }

        #endregion
    }
}
