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
    public partial class MasterPageGalleryViewModel : ObservableObject
    {
        private readonly ISharePointService _spService;
        private List<SharePointItem> _allItems = new();

        [ObservableProperty]
        private ObservableCollection<SharePointItem> _displayedItems = new();

        [ObservableProperty]
        private SharePointItem? _selectedItem;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready.";

        public event Action<string>? LogRequested;
        public event Action<SharePointItem>? OpenFileInEditorRequested;

        public MasterPageGalleryViewModel(ISharePointService spService)
        {
            _spService = spService;
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            DisplayedItems.Clear();
            var filtered = string.IsNullOrWhiteSpace(SearchText)
                ? _allItems
                : _allItems.Where(x => x.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                       x.ContentType.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            foreach (var item in filtered)
            {
                DisplayedItems.Add(item);
            }
        }

        public void ClearSession()
        {
            _allItems.Clear();
            DisplayedItems.Clear();
            SelectedItem = null;
            SearchText = string.Empty;
            StatusMessage = "Ready.";
        }

        [RelayCommand]
        public async Task LoadItemsAsync()
        {
            if (!_spService.IsConnected)
            {
                StatusMessage = "Please connect to a SharePoint site first.";
                return;
            }

            IsBusy = true;
            StatusMessage = "Reading Master Page Gallery (_catalogs/masterpage)...";
            LogRequested?.Invoke("Fetching Master Page Gallery items...");

            try
            {
                _allItems = await _spService.GetMasterPageGalleryItemsAsync();
                ApplyFilter();
                StatusMessage = $"Loaded {_allItems.Count} items from Master Page Gallery.";
                LogRequested?.Invoke($"Loaded {_allItems.Count} files in Master Page Gallery.");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load master pages: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Failed to load Master Page Gallery: {ex.Message}");
                MessageBox.Show($"Error loading Master Page Gallery:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public List<SharePointItem> GetSelectedOrCurrentItems()
        {
            var selected = DisplayedItems.Where(x => x.IsSelected).ToList();
            if (!selected.Any() && SelectedItem != null)
            {
                selected.Add(SelectedItem);
            }
            return selected;
        }

        [RelayCommand]
        public void SelectAll(bool select)
        {
            foreach (var item in DisplayedItems)
            {
                item.IsSelected = select;
            }
        }

        [RelayCommand]
        public async Task CheckOutAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any())
            {
                MessageBox.Show("Please select at least one file to check out.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            StatusMessage = $"Checking out {targetItems.Count} item(s)...";

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

            StatusMessage = $"Checked out {success} of {targetItems.Count} item(s).";
            await LoadItemsAsync();
        }

        [RelayCommand]
        public async Task CheckInAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any())
            {
                MessageBox.Show("Please select at least one file to check in.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new CheckInDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = $"Checking in {targetItems.Count} item(s)...";

            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Checking in: {item.ServerRelativeUrl} (Type: {dlg.CheckInType}, Comment: '{dlg.Comment}')");
                    await _spService.CheckInAsync(item.ServerRelativeUrl, dlg.CheckInType, dlg.Comment);
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Check-in failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Checked in {success} of {targetItems.Count} item(s).";
            await LoadItemsAsync();
        }

        [RelayCommand]
        public async Task PublishAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any())
            {
                MessageBox.Show("Please select at least one file to publish.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new CheckInDialog(publishMode: true) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = $"Publishing {targetItems.Count} item(s)...";

            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Publishing major version: {item.ServerRelativeUrl} ('{dlg.Comment}')");
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

            StatusMessage = $"Published {success} of {targetItems.Count} item(s).";
            await LoadItemsAsync();
        }

        [RelayCommand]
        public async Task UndoCheckOutAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any()) return;

            var res = MessageBox.Show($"Discard checkout for {targetItems.Count} item(s)? Any unsaved server changes will be reverted.", "Confirm Discard Check-out", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
                    LogRequested?.Invoke($"[ERROR] Undo checkout failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Discarded checkout for {success} of {targetItems.Count} item(s).";
            await LoadItemsAsync();
        }

        [RelayCommand]
        public async Task ApproveAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any()) return;

            IsBusy = true;
            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Approving: {item.ServerRelativeUrl}");
                    await _spService.ApproveAsync(item.ServerRelativeUrl, "Approved via SPAdminStudio");
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Approve failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Approved {success} of {targetItems.Count} item(s).";
            await LoadItemsAsync();
        }

        [RelayCommand]
        public async Task DenyAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any()) return;

            var res = MessageBox.Show($"Reject {targetItems.Count} item(s)? They will be marked as Denied and not visible to readers.", "Confirm Reject", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            IsBusy = true;
            int success = 0;
            foreach (var item in targetItems)
            {
                try
                {
                    LogRequested?.Invoke($"Rejecting: {item.ServerRelativeUrl}");
                    await _spService.DenyAsync(item.ServerRelativeUrl, "Rejected via SPAdminStudio");
                    success++;
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[ERROR] Reject failed for '{item.Name}': {ex.Message}");
                }
            }

            StatusMessage = $"Rejected {success} of {targetItems.Count} item(s).";
            await LoadItemsAsync();
        }

        [RelayCommand]
        public async Task ExportSelectedAsync()
        {
            var targetItems = GetSelectedOrCurrentItems();
            if (!targetItems.Any())
            {
                MessageBox.Show("Please select at least one file to export.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Select Destination Folder for Export"
            };

            if (dialog.ShowDialog() != true) return;
            var destFolder = dialog.FolderName;

            IsBusy = true;
            int count = 0;
            var errors = new List<string>();

            foreach (var item in targetItems)
            {
                try
                {
                    StatusMessage = $"Exporting ({count + 1}/{targetItems.Count}) {item.Name}...";
                    var data = await _spService.DownloadFileAsync(item.ServerRelativeUrl);
                    var filePath = Path.Combine(destFolder, item.Name);
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

            IsBusy = false;

            if (errors.Any())
            {
                if (count == 0)
                {
                    StatusMessage = $"Export failed for all {targetItems.Count} file(s).";
                    var errorSummary = string.Join("\n• ", errors.Take(5));
                    if (errors.Count > 5) errorSummary += $"\n... and {errors.Count - 5} more";
                    MessageBox.Show($"Export failed!\n0 of {targetItems.Count} file(s) could be downloaded.\n\nErrors:\n• {errorSummary}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    StatusMessage = $"Exported {count} of {targetItems.Count} files with some errors.";
                    var errorSummary = string.Join("\n• ", errors.Take(5));
                    if (errors.Count > 5) errorSummary += $"\n... and {errors.Count - 5} more";
                    MessageBox.Show($"Partial export completed.\n{count} of {targetItems.Count} file(s) exported successfully to:\n{destFolder}\n\nFailed files:\n• {errorSummary}", "Partial Export Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                StatusMessage = $"Successfully exported {count} files to {destFolder}.";
                MessageBox.Show($"Export complete!\nExported {count} file(s) to:\n{destFolder}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        public async Task ImportFilesAsync()
        {
            if (!_spService.IsConnected || _spService.CurrentSiteInfo == null) return;

            var openDlg = new OpenFileDialog
            {
                Title = "Select File(s) to Import to Master Page Gallery",
                Multiselect = true,
                Filter = "All Supported (*.master;*.html;*.aspx;*.css;*.js)|*.master;*.html;*.aspx;*.css;*.js|Master Pages (*.master)|*.master|HTML Files (*.html;*.htm)|*.html;*.htm|Page Layouts (*.aspx)|*.aspx|All Files (*.*)|*.*"
            };

            if (openDlg.ShowDialog() != true) return;

            IsBusy = true;
            var targetFolder = await _spService.GetMasterPageGalleryServerRelativeUrlAsync();
            int count = 0;
            var failed = new List<string>();

            foreach (var filePath in openDlg.FileNames)
            {
                var fileName = Path.GetFileName(filePath);
                try
                {
                    StatusMessage = $"Importing ({count + 1}/{openDlg.FileNames.Length}) {fileName}...";
                    var bytes = await File.ReadAllBytesAsync(filePath);
                    await _spService.UploadFileAsync(targetFolder, fileName, bytes, overwrite: true);
                    LogRequested?.Invoke($"Imported to Master Page Gallery: {fileName} ({bytes.Length / 1024} KB)");
                    count++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{fileName}: {ex.Message}");
                    LogRequested?.Invoke($"[ERROR] Failed to import '{fileName}': {ex.Message}");
                }
            }

            StatusMessage = $"Imported {count} of {openDlg.FileNames.Length} file(s) to Master Page Gallery.";
            IsBusy = false;
            await LoadItemsAsync();

            if (failed.Any())
            {
                var summary = string.Join("\n• ", failed.Take(5));
                if (failed.Count > 5) summary += $"\n... and {failed.Count - 5} more";
                MessageBox.Show($"Imported {count} of {openDlg.FileNames.Length} file(s).\n\nFailed:\n• {summary}", "Import Completed With Errors",
                    MessageBoxButton.OK, count == 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"Import complete!\nImported {count} file(s) into Master Page Gallery.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>Raised after web-level settings (e.g. master page assignment) change.</summary>
        public event Action? SiteSettingsChanged;

        [RelayCommand]
        public Task SetAsSiteMasterAsync() => AssignMasterPageAsync(siteMaster: true, systemMaster: false);

        [RelayCommand]
        public Task SetAsSystemMasterAsync() => AssignMasterPageAsync(siteMaster: false, systemMaster: true);

        [RelayCommand]
        public Task SetAsBothMastersAsync() => AssignMasterPageAsync(siteMaster: true, systemMaster: true);

        private async Task AssignMasterPageAsync(bool siteMaster, bool systemMaster)
        {
            var target = SelectedItem ?? DisplayedItems.FirstOrDefault(x => x.IsSelected);
            if (target == null || !target.Name.EndsWith(".master", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select a .master file first.", "Master Page Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var role = siteMaster && systemMaster ? "site AND system master page"
                     : siteMaster ? "site master page (publishing / content pages)"
                     : "system master page (settings, lists and forms)";
            var webUrl = _spService.CurrentSiteInfo?.Url ?? "the current site";

            var warning = target.IsCheckedOut || target.ApprovalStatusDisplay != "Approved"
                ? "\n\n⚠ This file is checked out or not approved. Visitors will not see it until a major version is published and approved."
                : string.Empty;

            var res = MessageBox.Show($"Set '{target.Name}' as the {role} for:\n{webUrl}?{warning}\n\nSubsites that inherit their master page are not changed by this action.",
                "Assign Master Page", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            IsBusy = true;
            try
            {
                await _spService.SetMasterPageAsync(target.ServerRelativeUrl, siteMaster, systemMaster);
                StatusMessage = $"'{target.Name}' is now the {role}.";
                LogRequested?.Invoke($"Assigned {target.ServerRelativeUrl} as {role} on {webUrl}");
                SiteSettingsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to assign master page: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Assign master page failed: {ex.Message}");
                MessageBox.Show($"Failed to assign master page:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public void OpenInEditor()
        {
            var target = SelectedItem ?? DisplayedItems.FirstOrDefault(x => x.IsSelected);
            if (target != null)
            {
                OpenFileInEditorRequested?.Invoke(target);
            }
        }
    }
}
