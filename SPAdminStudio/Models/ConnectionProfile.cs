using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SPAdminStudio.Models
{
    public enum AuthenticationMode
    {
        CurrentWindowsUser = 0,
        SpecificWindowsDomain = 1,
        FormsBasedAuth = 2
    }

    public class ConnectionProfile : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _profileName = "My SharePoint Portal";
        private string _siteUrl = "http://sharepoint/sites/portal";
        private AuthenticationMode _authMode = AuthenticationMode.CurrentWindowsUser;
        private string _domain = string.Empty;
        private string _username = string.Empty;
        private string _encryptedPassword = string.Empty;
        private bool _trustAllCertificates;
        private DateTime _lastUsed = DateTime.Now;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(nameof(Id)); }
        }

        public string ProfileName
        {
            get => _profileName;
            set { _profileName = value; OnPropertyChanged(nameof(ProfileName)); }
        }

        public string SiteUrl
        {
            get => _siteUrl;
            set { _siteUrl = value; OnPropertyChanged(nameof(SiteUrl)); }
        }

        public AuthenticationMode AuthMode
        {
            get => _authMode;
            set { _authMode = value; OnPropertyChanged(nameof(AuthMode)); }
        }

        public string Domain
        {
            get => _domain;
            set { _domain = value; OnPropertyChanged(nameof(Domain)); }
        }

        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(nameof(Username)); }
        }

        public string EncryptedPassword
        {
            get => _encryptedPassword;
            set { _encryptedPassword = value; OnPropertyChanged(nameof(EncryptedPassword)); }
        }

        /// <summary>
        /// Skip TLS certificate validation (self-signed internal farms only). Off by default.
        /// </summary>
        public bool TrustAllCertificates
        {
            get => _trustAllCertificates;
            set { _trustAllCertificates = value; OnPropertyChanged(nameof(TrustAllCertificates)); }
        }

        public DateTime LastUsed
        {
            get => _lastUsed;
            set { _lastUsed = value; OnPropertyChanged(nameof(LastUsed)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class SharePointSiteInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ServerRelativeUrl { get; set; } = string.Empty;
        public string SiteCollectionServerRelativeUrl { get; set; } = string.Empty;
        public string SharePointVersion { get; set; } = string.Empty;
        public string CurrentUserLogin { get; set; } = string.Empty;
        public string CurrentUserTitle { get; set; } = string.Empty;
        public bool IsSiteAdmin { get; set; }
    }
}
