using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SPAdminStudio.Services;

namespace SPAdminStudio.ViewModels
{
    public partial class BackupViewModel : ObservableObject
    {
        private readonly ISharePointService _spService;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready to perform backup.";

        [ObservableProperty]
        private bool _includeMasterPages = true;

        [ObservableProperty]
        private bool _includeStyleLibrary = true;

        [ObservableProperty]
        private string _lastBackupPath = string.Empty;

        public event Action<string>? LogRequested;

        public BackupViewModel(ISharePointService spService)
        {
            _spService = spService;
        }

        public void ClearSession()
        {
            LastBackupPath = string.Empty;
            StatusMessage = "Ready to perform backup.";
        }

        [RelayCommand]
        public async Task StartBackupAsync()
        {
            if (!_spService.IsConnected || _spService.CurrentSiteInfo == null)
            {
                MessageBox.Show("Please connect to a SharePoint site first.", "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var defaultName = $"SP_Branding_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var saveDlg = new SaveFileDialog
            {
                Title = "Select Destination for Branding Backup ZIP",
                FileName = defaultName,
                Filter = "ZIP Archive (*.zip)|*.zip"
            };

            if (saveDlg.ShowDialog() != true) return;

            var zipPath = saveDlg.FileName;
            IsBusy = true;
            StatusMessage = "Starting backup process...";
            LogRequested?.Invoke($"Starting 1-Click Branding Backup to '{zipPath}'...");

            var tempDir = Path.Combine(Path.GetTempPath(), "SPAdminStudio_Backup_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            try
            {
                int fileCount = 0;

                if (IncludeMasterPages)
                {
                    StatusMessage = "Exporting Master Page Gallery...";
                    var mpDir = Path.Combine(tempDir, "MasterPageGallery");
                    Directory.CreateDirectory(mpDir);

                    var masterPages = await _spService.GetMasterPageGalleryItemsAsync();
                    foreach (var item in masterPages)
                    {
                        try
                        {
                            StatusMessage = $"Backing up: {item.Name}...";
                            var bytes = await _spService.DownloadFileAsync(item.ServerRelativeUrl);
                            await File.WriteAllBytesAsync(Path.Combine(mpDir, item.Name), bytes);
                            fileCount++;
                        }
                        catch (Exception ex)
                        {
                            LogRequested?.Invoke($"[WARNING] Failed to backup file '{item.Name}': {ex.Message}");
                        }
                    }
                    LogRequested?.Invoke($"Backed up {fileCount} items from Master Page Gallery.");
                }

                if (IncludeStyleLibrary)
                {
                    StatusMessage = "Exporting Style Library tree...";
                    var styleDir = Path.Combine(tempDir, "StyleLibrary");
                    Directory.CreateDirectory(styleDir);

                    var rootUrl = await _spService.GetStyleLibraryServerRelativeUrlAsync();
                    await BackupFolderRecursiveAsync(rootUrl, styleDir, () => fileCount++);
                    LogRequested?.Invoke("Backed up Style Library hierarchy.");
                }

                StatusMessage = "Creating ZIP archive...";
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, false);

                LastBackupPath = zipPath;
                StatusMessage = $"Backup completed successfully! {fileCount} file(s) saved.";
                LogRequested?.Invoke($"Branding backup created: {zipPath} ({new FileInfo(zipPath).Length / 1024} KB)");
                MessageBox.Show($"Backup completed successfully!\n\nArchive: {zipPath}\nTotal Files: {fileCount}", "Backup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Backup failed: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Backup error: {ex.Message}");
                MessageBox.Show($"Backup failed:\n{ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
                IsBusy = false;
            }
        }

        private async Task BackupFolderRecursiveAsync(string spFolderUrl, string localPath, Action onFileExported)
        {
            if (!Directory.Exists(localPath)) Directory.CreateDirectory(localPath);

            var files = await _spService.GetFolderFilesAsync(spFolderUrl);
            foreach (var f in files)
            {
                try
                {
                    StatusMessage = $"Backing up: {f.Name}...";
                    var bytes = await _spService.DownloadFileAsync(f.ServerRelativeUrl);
                    await File.WriteAllBytesAsync(Path.Combine(localPath, f.Name), bytes);
                    onFileExported();
                }
                catch (Exception ex)
                {
                    LogRequested?.Invoke($"[WARNING] Failed to backup file '{f.Name}': {ex.Message}");
                }
            }

            foreach (var sub in await _spService.GetSubFoldersAsync(spFolderUrl))
            {
                var subLocal = Path.Combine(localPath, sub.Name);
                await BackupFolderRecursiveAsync(sub.ServerRelativeUrl, subLocal, onFileExported);
            }
        }
    }
}
