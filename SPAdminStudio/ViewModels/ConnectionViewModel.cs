using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SPAdminStudio.Models;
using SPAdminStudio.Services;

namespace SPAdminStudio.ViewModels
{
    public partial class ConnectionViewModel : ObservableObject
    {
        private readonly ISharePointService _spService;
        private readonly ProfileStorageService _profileStorage;

        [ObservableProperty]
        private ObservableCollection<ConnectionProfile> _profiles = new();

        [ObservableProperty]
        private ConnectionProfile? _selectedProfile;

        [ObservableProperty]
        private string _plainPassword = string.Empty;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready to connect.";

        [ObservableProperty]
        private bool _rememberPassword = true;

        public event Action<string>? LogRequested;
        public event Action? ConnectedSuccessfully;
        public event Action? Disconnected;
        public Func<Task<bool>>? CanChangeConnection;

        public ConnectionViewModel(ISharePointService spService, ProfileStorageService profileStorage)
        {
            _spService = spService;
            _profileStorage = profileStorage;
            LoadProfiles();
        }

        public void LoadProfiles()
        {
            Profiles.Clear();
            var list = _profileStorage.LoadProfiles();
            foreach (var p in list)
            {
                Profiles.Add(p);
            }

            SelectedProfile = Profiles.FirstOrDefault();
            if (SelectedProfile != null && !string.IsNullOrEmpty(SelectedProfile.EncryptedPassword))
            {
                PlainPassword = ProfileStorageService.DecryptPassword(SelectedProfile.EncryptedPassword);
            }
        }

        partial void OnSelectedProfileChanged(ConnectionProfile? value)
        {
            if (value != null && !string.IsNullOrEmpty(value.EncryptedPassword))
            {
                PlainPassword = ProfileStorageService.DecryptPassword(value.EncryptedPassword);
            }
            else
            {
                PlainPassword = string.Empty;
            }
        }

        [RelayCommand]
        public void AddNewProfile()
        {
            var p = new ConnectionProfile
            {
                ProfileName = $"Portal {Profiles.Count + 1}",
                SiteUrl = "http://sharepoint/sites/portal",
                AuthMode = AuthenticationMode.CurrentWindowsUser
            };
            Profiles.Add(p);
            SelectedProfile = p;
            PlainPassword = string.Empty;
        }

        [RelayCommand]
        public void DeleteProfile()
        {
            if (SelectedProfile == null) return;
            var res = MessageBox.Show($"Delete profile '{SelectedProfile.ProfileName}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                Profiles.Remove(SelectedProfile);
                _profileStorage.SaveProfiles(Profiles.ToList());
                SelectedProfile = Profiles.FirstOrDefault();
            }
        }

        [RelayCommand]
        public void SaveProfile()
        {
            if (SelectedProfile == null) return;

            if (RememberPassword && !string.IsNullOrEmpty(PlainPassword))
            {
                SelectedProfile.EncryptedPassword = ProfileStorageService.EncryptPassword(PlainPassword);
            }
            else if (!RememberPassword)
            {
                SelectedProfile.EncryptedPassword = string.Empty;
            }

            _profileStorage.SaveProfiles(Profiles.ToList());
            LogRequested?.Invoke($"Saved profile '{SelectedProfile.ProfileName}'.");
            StatusMessage = "Profile saved successfully.";
        }

        [RelayCommand]
        public async Task ConnectAsync()
        {
            if (SelectedProfile == null) return;

            if (CanChangeConnection != null && !await CanChangeConnection())
            {
                LogRequested?.Invoke("Connection change cancelled by user.");
                return;
            }

            IsBusy = true;
            StatusMessage = $"Connecting to {SelectedProfile.SiteUrl}...";
            LogRequested?.Invoke($"Initiating connection to '{SelectedProfile.SiteUrl}' using mode: {SelectedProfile.AuthMode}...");

            try
            {
                if (RememberPassword && !string.IsNullOrEmpty(PlainPassword))
                {
                    SelectedProfile.EncryptedPassword = ProfileStorageService.EncryptPassword(PlainPassword);
                    _profileStorage.SaveProfiles(Profiles.ToList());
                }

                await _spService.ConnectAsync(SelectedProfile, PlainPassword);
                
                var site = _spService.CurrentSiteInfo;
                StatusMessage = $"Connected to '{site?.Title}' as '{site?.CurrentUserTitle}'.";
                LogRequested?.Invoke($"Successfully connected to '{site?.Title}' (Server Relative: {site?.ServerRelativeUrl}). User: {site?.CurrentUserLogin} (IsAdmin: {site?.IsSiteAdmin})");
                
                ConnectedSuccessfully?.Invoke();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Connection failed: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Connection failed: {ex}");
                MessageBox.Show($"Failed to connect to SharePoint:\n{ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task DisconnectAsync()
        {
            if (CanChangeConnection != null && !await CanChangeConnection())
            {
                return;
            }

            _spService.Disconnect();
            StatusMessage = "Disconnected.";
            LogRequested?.Invoke("Disconnected from SharePoint.");
            Disconnected?.Invoke();
        }
    }
}
