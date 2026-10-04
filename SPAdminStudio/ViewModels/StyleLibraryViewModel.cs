using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SPAdminStudio.Models;
using SPAdminStudio.Services;
using SPAdminStudio.Views;

namespace SPAdminStudio.ViewModels
{
    public partial class StyleLibraryViewModel : ObservableObject
    {
        private readonly ISharePointService _spService;
        private List<SharePointItem> _allFolderFiles = new();

        [ObservableProperty]
        private SharePointFolder? _rootFolder;

        [ObservableProperty]
        private ObservableCollection<SharePointFolder> _folderRoots = new();

        [ObservableProperty]
        private SharePointFolder? _selectedFolder;

        [ObservableProperty]
        private ObservableCollection<SharePointItem> _displayedFiles = new();

        [ObservableProperty]
        private SharePointItem? _selectedFile;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready.";

        [ObservableProperty]
        private ObservableCollection<BreadcrumbItem> _breadcrumbs = new();

        [ObservableProperty]
        private string _currentFolderPath = string.Empty;

        [ObservableProperty]
        private bool _canGoBack;

        [ObservableProperty]
        private bool _canGoForward;

        [ObservableProperty]
        private bool _canGoUp;

        private readonly Stack<string> _backStack = new();
        private readonly Stack<string> _forwardStack = new();

        public string ActiveFolderUrl => !string.IsNullOrEmpty(CurrentFolderPath)
            ? CurrentFolderPath
            : (SelectedFolder?.ServerRelativeUrl ?? RootFolder?.ServerRelativeUrl ?? "");

        public event Action<string>? LogRequested;
        public event Action<SharePointItem>? OpenFileInEditorRequested;

        public StyleLibraryViewModel(ISharePointService spService)
        {
            _spService = spService;
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            DisplayedFiles.Clear();
            var filtered = string.IsNullOrWhiteSpace(SearchText)
                ? _allFolderFiles
                : _allFolderFiles.Where(x => x.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            foreach (var item in filtered)
            {
                DisplayedFiles.Add(item);
            }
        }

        public void ClearSession()
        {
            _allFolderFiles.Clear();
            DisplayedFiles.Clear();
            FolderRoots.Clear();
            Breadcrumbs.Clear();
            _backStack.Clear();
            _forwardStack.Clear();
            CurrentFolderPath = string.Empty;
            CanGoBack = false;
            CanGoForward = false;
            CanGoUp = false;
            RootFolder = null;
            SelectedFolder = null;
            SelectedFile = null;
            SearchText = string.Empty;
            StatusMessage = "Ready.";
        }

        [RelayCommand]
        public async Task LoadRootAsync()
        {
            if (!_spService.IsConnected || _spService.CurrentSiteInfo == null)
            {
                StatusMessage = "Please connect to a SharePoint site first.";
                return;
            }

            IsBusy = true;
            StatusMessage = "Loading Style Library...";
            LogRequested?.Invoke("Fetching Style Library folders...");

            try
            {
                var rootUrl = await _spService.GetStyleLibraryServerRelativeUrlAsync();
                LogRequested?.Invoke($"Style Library location detected: '{rootUrl}'");
                RootFolder = await _spService.GetFolderHierarchyAsync(rootUrl);
                FolderRoots.Clear();
                FolderRoots.Add(RootFolder);
                SelectedFolder = RootFolder;

                _backStack.Clear();
                _forwardStack.Clear();
                await LoadFolderContentsAsync(rootUrl, recordHistory: false);
                StatusMessage = "Loaded Style Library.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load Style Library: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Failed to load Style Library: {ex.Message}");
                MessageBox.Show($"Error loading Style Library:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task SelectFolderAsync(SharePointFolder folder)
        {
            SelectedFolder = folder;
            await LoadFolderContentsAsync(folder.ServerRelativeUrl);
        }

        public async Task LoadFolderContentsAsync(string folderUrl, bool recordHistory = true)
        {
            if (string.IsNullOrEmpty(folderUrl)) return;
            folderUrl = folderUrl.TrimEnd('/');

            if (recordHistory && !string.IsNullOrEmpty(CurrentFolderPath) && !CurrentFolderPath.Equals(folderUrl, StringComparison.OrdinalIgnoreCase))
            {
                _backStack.Push(CurrentFolderPath);
                _forwardStack.Clear();
            }

            CurrentFolderPath = folderUrl;
            UpdateNavState();
            UpdateBreadcrumbs(folderUrl);

            IsBusy = true;
            StatusMessage = $"Loading items in '{folderUrl}'...";

            try
            {
                _allFolderFiles = await _spService.GetFolderContentsAsync(folderUrl);
                ApplyFilter();
                var folderCount = _allFolderFiles.Count(x => x.IsFolder);
                var fileCount = _allFolderFiles.Count(x => !x.IsFolder);
                StatusMessage = $"{folderCount} folder(s), {fileCount} file(s)";
                LogRequested?.Invoke($"Loaded {folderCount} folder(s) and {fileCount} file(s) from '{folderUrl}'.");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load items: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Failed to load items from '{folderUrl}': {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UpdateBreadcrumbs(string folderUrl)
        {
            Breadcrumbs.Clear();
            var rootUrl = RootFolder?.ServerRelativeUrl?.TrimEnd('/') ?? "";

            Breadcrumbs.Add(new BreadcrumbItem
            {
                Name = RootFolder?.Name ?? "Style Library",
                ServerRelativeUrl = rootUrl
            });

            if (!string.IsNullOrEmpty(rootUrl) && folderUrl.Length > rootUrl.Length)
            {
                var relativePart = folderUrl.Substring(rootUrl.Length).TrimStart('/');
                var segments = relativePart.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var accumulated = rootUrl;

                foreach (var seg in segments)
                {
                    accumulated += "/" + seg;
                    Breadcrumbs.Add(new BreadcrumbItem
                    {
                        Name = seg,
                        ServerRelativeUrl = accumulated
                    });
                }
            }

            if (Breadcrumbs.Any())
            {
                Breadcrumbs.Last().IsLast = true;
            }
        }

        private void UpdateNavState()
        {
            CanGoBack = _backStack.Count > 0;
            CanGoForward = _forwardStack.Count > 0;
            var rootUrl = RootFolder?.ServerRelativeUrl?.TrimEnd('/') ?? "";
            CanGoUp = !string.IsNullOrEmpty(CurrentFolderPath) &&
                      !CurrentFolderPath.Equals(rootUrl, StringComparison.OrdinalIgnoreCase) &&
                      CurrentFolderPath.Length > rootUrl.Length;
        }

        [RelayCommand]
        public async Task NavigateBackAsync()
        {
            if (_backStack.Count == 0) return;
            var prev = _backStack.Pop();
            _forwardStack.Push(CurrentFolderPath);
            await LoadFolderContentsAsync(prev, recordHistory: false);
        }

        [RelayCommand]
        public async Task NavigateForwardAsync()
        {
            if (_forwardStack.Count == 0) return;
            var next = _forwardStack.Pop();
            _backStack.Push(CurrentFolderPath);
            await LoadFolderContentsAsync(next, recordHistory: false);
        }

        [RelayCommand]
        public async Task NavigateUpAsync()
        {
            if (!CanGoUp) return;
            var parent = Path.GetDirectoryName(CurrentFolderPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) parent = RootFolder?.ServerRelativeUrl;
            if (!string.IsNullOrEmpty(parent))
            {
                await LoadFolderContentsAsync(parent);
            }
        }

        [RelayCommand]
        public async Task NavigateToBreadcrumbAsync(BreadcrumbItem? crumb)
        {
            if (crumb == null || string.IsNullOrEmpty(crumb.ServerRelativeUrl)) return;
            await LoadFolderContentsAsync(crumb.ServerRelativeUrl);
        }

        [RelayCommand]
        public async Task NavigateIntoFolderAsync(SharePointItem? folderItem)
        {
            if (folderItem == null || !folderItem.IsFolder) return;
            await LoadFolderContentsAsync(folderItem.ServerRelativeUrl);
        }

        public List<SharePointItem> GetSelectedOrCurrentFiles()
        {
            var selected = DisplayedFiles.Where(x => x.IsSelected).ToList();
            if (!selected.Any() && SelectedFile != null)
            {
                selected.Add(SelectedFile);
            }
            return selected;
        }

        [RelayCommand]
        public void SelectAll(bool select)
        {
            foreach (var item in DisplayedFiles)
            {
                item.IsSelected = select;
            }
        }

        [RelayCommand]
        public async Task CheckOutAsync()
        {
            var targetItems = GetSelectedOrCurrentFiles().Where(x => !x.IsFolder).ToList();
            if (!targetItems.Any()) return;

            IsBusy = true;
            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Checking out: {item.ServerRelativeUrl}");
                    await _spService.CheckOutAsync(item.ServerRelativeUrl);
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Check-out failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Checked out {success} of {targetItems.Count} file(s).";
            await LoadFolderContentsAsync(ActiveFolderUrl, recordHistory: false);
        }

        [RelayCommand]
        public async Task CheckInAsync()
        {
            var targetItems = GetSelectedOrCurrentFiles().Where(x => !x.IsFolder).ToList();
            if (!targetItems.Any()) return;

            var dlg = new CheckInDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Checking in: {item.ServerRelativeUrl} (Type: {dlg.CheckInType})");
                    await _spService.CheckInAsync(item.ServerRelativeUrl, dlg.CheckInType, dlg.Comment);
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Check-in failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Checked in {success} of {targetItems.Count} file(s).";
            await LoadFolderContentsAsync(ActiveFolderUrl, recordHistory: false);
        }

        [RelayCommand]
        public async Task PublishAsync()
        {
            var targetItems = GetSelectedOrCurrentFiles().Where(x => !x.IsFolder).ToList();
            if (!targetItems.Any()) return;

            var dlg = new CheckInDialog(publishMode: true) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Publishing: {item.ServerRelativeUrl}");
                    // A checked-out file cannot be published directly; check it in as a major version instead
                    if (item.CheckOutType != 2)
                        await _spService.CheckInAsync(item.ServerRelativeUrl, 1, dlg.Comment);
                    else
                        await _spService.PublishAsync(item.ServerRelativeUrl, dlg.Comment);
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Publish failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Published {success} of {targetItems.Count} file(s).";
            await LoadFolderContentsAsync(ActiveFolderUrl, recordHistory: false);
        }

        [RelayCommand]
        public async Task UndoCheckOutAsync()
        {
            var targetItems = GetSelectedOrCurrentFiles().Where(x => !x.IsFolder).ToList();
            if (!targetItems.Any()) return;

            var res = MessageBox.Show($"Discard checkout for {targetItems.Count} file(s)?", "Confirm Discard Check-out", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            IsBusy = true;
            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Discarding checkout: {item.ServerRelativeUrl}");
                    await _spService.UndoCheckOutAsync(item.ServerRelativeUrl);
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Discard checkout failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Discarded checkout for {success} file(s).";
            await LoadFolderContentsAsync(ActiveFolderUrl, recordHistory: false);
        }

        [RelayCommand]
        public async Task CreateNewFolderAsync()
        {
            var parentUrl = ActiveFolderUrl;
            if (string.IsNullOrEmpty(parentUrl)) parentUrl = RootFolder?.ServerRelativeUrl;
            if (string.IsNullOrEmpty(parentUrl))
            {
                MessageBox.Show("No active folder located.", "Folder Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new NewFolderDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = $"Creating folder '{dlg.FolderName}'...";

            try
            {
                await _spService.CreateFolderAsync(parentUrl, dlg.FolderName);
                LogRequested?.Invoke($"Created folder: {parentUrl}/{dlg.FolderName}");
                await LoadFolderContentsAsync(parentUrl, recordHistory: false);
                if (RootFolder != null)
                {
                    RootFolder = await _spService.GetFolderHierarchyAsync(RootFolder.ServerRelativeUrl);
                    FolderRoots.Clear();
                    FolderRoots.Add(RootFolder);
                }
                StatusMessage = $"Created folder '{dlg.FolderName}'.";
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Failed to create folder: {ex.Message}");
                MessageBox.Show($"Failed to create folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task ExportSelectedFilesAsync()
        {
            var targetItems = GetSelectedOrCurrentFiles();
            if (!targetItems.Any())
            {
                MessageBox.Show("Please select at least one file to export.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog { Title = "Select Destination Folder for Export" };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            int count = 0;
            var errors = new List<string>();

            foreach (var item in targetItems)
            {
                if (item.IsFolder)
                {
                    // Recursively export folder
                    try
                    {
                        var targetLocalRoot = Path.Combine(dialog.FolderName, item.Name);
                        await ExportFolderRecursiveCoreAsync(item.ServerRelativeUrl, targetLocalRoot, (f) => count++);
                        LogRequested?.Invoke($"Exported folder: {item.Name} -> {targetLocalRoot}");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{item.Name} (folder): {ex.Message}");
                        LogRequested?.Invoke($"[ERROR] Failed to export folder '{item.Name}': {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        StatusMessage = $"Exporting ({count + 1}/{targetItems.Count}) {item.Name}...";
                        var data = await _spService.DownloadFileAsync(item.ServerRelativeUrl);
                        var filePath = Path.Combine(dialog.FolderName, item.Name);
                        await File.WriteAllBytesAsync(filePath, data);
                        LogRequested?.Invoke($"Exported: {item.Name} ({data.Length / 1024.0:F1} KB) -> {filePath}");
                        count++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{item.Name}: {ex.Message}");
                        LogRequested?.Invoke($"[ERROR] Failed to export '{item.Name}': {ex.Message}");
                    }
                }
            }

            IsBusy = false;

            if (errors.Any())
            {
                if (count == 0)
                {
                    StatusMessage = $"Export failed for all {targetItems.Count} item(s).";
                    var errorSummary = string.Join("\n• ", errors.Take(5));
                    if (errors.Count > 5) errorSummary += $"\n... and {errors.Count - 5} more";
                    MessageBox.Show($"Export failed!\n0 of {targetItems.Count} item(s) could be downloaded.\n\nErrors:\n• {errorSummary}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    StatusMessage = $"Exported {count} of {targetItems.Count} items with some errors.";
                    var errorSummary = string.Join("\n• ", errors.Take(5));
                    if (errors.Count > 5) errorSummary += $"\n... and {errors.Count - 5} more";
                    MessageBox.Show($"Partial export completed.\n{count} of {targetItems.Count} item(s) saved to:\n{dialog.FolderName}\n\nFailed items:\n• {errorSummary}", "Partial Export Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                StatusMessage = $"Exported {count} file(s).";
                MessageBox.Show($"Export complete!\n{count} file(s) saved to:\n{dialog.FolderName}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        public async Task ExportFolderRecursiveAsync()
        {
            var targetUrl = SelectedFile?.IsFolder == true
                ? SelectedFile.ServerRelativeUrl
                : ActiveFolderUrl;

            if (string.IsNullOrEmpty(targetUrl)) return;
            var folderName = Path.GetFileName(targetUrl.TrimEnd('/'));
            if (string.IsNullOrEmpty(folderName)) folderName = "Style Library";

            var dialog = new OpenFolderDialog { Title = $"Select Local Folder to Export '{folderName}' Tree Into" };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = $"Recursively exporting folder '{folderName}'...";

            try
            {
                int fileCount = 0;
                var targetLocalRoot = Path.Combine(dialog.FolderName, folderName);
                await ExportFolderRecursiveCoreAsync(targetUrl, targetLocalRoot, (item) => fileCount++);
                StatusMessage = $"Export complete. Downloaded {fileCount} file(s) to '{targetLocalRoot}'.";
                LogRequested?.Invoke($"Bulk folder export finished: {fileCount} files -> {targetLocalRoot}");
                MessageBox.Show($"Export complete!\nDownloaded {fileCount} file(s) and folders to:\n{targetLocalRoot}", "Folder Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Recursive folder export error: {ex.Message}");
                MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExportFolderRecursiveCoreAsync(string spFolderUrl, string localFolderPath, Action<SharePointItem> onFileExported)
        {
            if (!Directory.Exists(localFolderPath))
            {
                Directory.CreateDirectory(localFolderPath);
            }

            var files = await _spService.GetFolderFilesAsync(spFolderUrl);
            foreach (var f in files)
            {
                try
                {
                    StatusMessage = $"Downloading {f.Name}...";
                    var bytes = await _spService.DownloadFileAsync(f.ServerRelativeUrl);
                    var destPath = Path.Combine(localFolderPath, f.Name);
                    await File.WriteAllBytesAsync(destPath, bytes);
                    onFileExported(f);
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Failed to export '{f.Name}': {ex.Message}");
                }
            }

            foreach (var sub in await _spService.GetSubFoldersAsync(spFolderUrl))
            {
                var subLocal = Path.Combine(localFolderPath, sub.Name);
                await ExportFolderRecursiveCoreAsync(sub.ServerRelativeUrl, subLocal, onFileExported);
            }
        }

        [RelayCommand]
        public async Task ImportFilesAsync()
        {
            var targetUrl = ActiveFolderUrl;
            if (string.IsNullOrEmpty(targetUrl)) return;
            var folderName = Path.GetFileName(targetUrl.TrimEnd('/'));
            if (string.IsNullOrEmpty(folderName)) folderName = "Style Library";

            var openDlg = new OpenFileDialog
            {
                Title = $"Select Files to Import to '{folderName}'",
                Multiselect = true,
                Filter = "All Assets (*.css;*.js;*.png;*.jpg;*.gif;*.svg;*.woff;*.woff2)|*.css;*.js;*.png;*.jpg;*.gif;*.svg;*.woff;*.woff2|CSS (*.css)|*.css|JavaScript (*.js)|*.js|Images (*.png;*.jpg;*.gif;*.svg)|*.png;*.jpg;*.gif;*.svg|All Files (*.*)|*.*"
            };

            if (openDlg.ShowDialog() != true) return;

            IsBusy = true;
            int count = 0;
            var failed = new List<string>();
            foreach (var filePath in openDlg.FileNames)
            {
                var fileName = Path.GetFileName(filePath);
                try
                {
                    StatusMessage = $"Importing ({count + 1}/{openDlg.FileNames.Length}) {fileName}...";
                    var bytes = await File.ReadAllBytesAsync(filePath);
                    await _spService.UploadFileAsync(targetUrl, fileName, bytes, overwrite: true);
                    LogRequested?.Invoke($"Imported to {targetUrl}: {fileName} ({bytes.Length / 1024} KB)");
                    count++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{fileName}: {ex.Message}");
                    LogRequested?.Invoke($"[ERROR] Failed to import '{fileName}': {ex.Message}");
                }
            }

            StatusMessage = $"Imported {count} of {openDlg.FileNames.Length} file(s).";
            IsBusy = false;
            await LoadFolderContentsAsync(targetUrl, recordHistory: false);
            ShowImportResult(count, failed, folderName);
        }

        private static void ShowImportResult(int count, List<string> failed, string folderName)
        {
            if (failed.Any())
            {
                var summary = string.Join("\n• ", failed.Take(5));
                if (failed.Count > 5) summary += $"\n... and {failed.Count - 5} more";
                MessageBox.Show($"Imported {count} file(s) into '{folderName}', {failed.Count} failed:\n• {summary}", "Import Completed With Errors",
                    MessageBoxButton.OK, count == 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"Import complete! {count} file(s) imported into '{folderName}'.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        public async Task ImportFolderRecursiveAsync()
        {
            var targetUrl = ActiveFolderUrl;
            if (string.IsNullOrEmpty(targetUrl)) return;
            var folderName = Path.GetFileName(targetUrl.TrimEnd('/'));
            if (string.IsNullOrEmpty(folderName)) folderName = "Style Library";

            var folderDlg = new OpenFolderDialog { Title = $"Select Local Folder to Import Recursively into '{folderName}'" };
            if (folderDlg.ShowDialog() != true) return;

            var localFolder = folderDlg.FolderName;
            IsBusy = true;
            StatusMessage = $"Recursively importing folder '{Path.GetFileName(localFolder)}'...";

            try
            {
                int count = 0;
                var failed = new List<string>();
                await ImportFolderRecursiveCoreAsync(localFolder, targetUrl, () => count++, failed);
                StatusMessage = $"Import finished. Uploaded {count} file(s), {failed.Count} failed.";
                LogRequested?.Invoke($"Recursive folder upload finished: {count} files uploaded, {failed.Count} failed.");
                await LoadFolderContentsAsync(targetUrl, recordHistory: false);
                ShowImportResult(count, failed, folderName);
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Recursive folder import error: {ex.Message}");
                MessageBox.Show($"Import error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ImportFolderRecursiveCoreAsync(string localDirectory, string spParentUrl, Action onFileUploaded, List<string> failed)
        {
            var dirName = Path.GetFileName(localDirectory);
            var targetSpFolder = spParentUrl.TrimEnd('/') + "/" + dirName;

            try
            {
                await _spService.CreateFolderAsync(spParentUrl, dirName);
            }
            catch { /* Folder may already exist */ }

            // One bad file must not abort the rest of the tree
            foreach (var filePath in Directory.GetFiles(localDirectory))
            {
                var fileName = Path.GetFileName(filePath);
                try
                {
                    StatusMessage = $"Uploading {fileName}...";
                    var bytes = await File.ReadAllBytesAsync(filePath);
                    await _spService.UploadFileAsync(targetSpFolder, fileName, bytes, overwrite: true);
                    onFileUploaded();
                }
                catch (Exception ex)
                {
                    failed.Add($"{targetSpFolder}/{fileName}: {ex.Message}");
                    LogRequested?.Invoke($"[ERROR] Failed to upload '{filePath}': {ex.Message}");
                }
            }

            foreach (var subDir in Directory.GetDirectories(localDirectory))
            {
                await ImportFolderRecursiveCoreAsync(subDir, targetSpFolder, onFileUploaded, failed);
            }
        }

        [RelayCommand]
        public void OpenInEditor()
        {
            var target = SelectedFile ?? DisplayedFiles.FirstOrDefault(x => x.IsSelected);
            if (target != null)
            {
                if (target.IsFolder)
                {
                    _ = NavigateIntoFolderAsync(target);
                }
                else
                {
                    OpenFileInEditorRequested?.Invoke(target);
                }
            }
        }
    }
}
