using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace SPAdminStudio.Models
{
    public enum CheckOutStatus
    {
        None = 0,       // Checked in
        Online = 1,     // Checked out to someone
        Offline = 2     // Checked out offline
    }

    public enum ModerationStatus
    {
        Approved = 0,
        Denied = 1,
        Pending = 2,
        Draft = 3,
        Scheduled = 4,
        Unknown = 99
    }

    public class SharePointItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _name = string.Empty;
        private string _serverRelativeUrl = string.Empty;
        private long _length;
        private int _checkOutType;
        private string _checkedOutByUser = string.Empty;
        private string _version = "1.0";
        private int _moderationStatus = (int)ModerationStatus.Approved;
        private string _moderationComments = string.Empty;
        private DateTime _timeLastModified;
        private string _modifiedBy = string.Empty;
        private string _contentType = string.Empty;
        private string _uiVersion = "15";
        private bool _isFolder;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(FileExtension));
                OnPropertyChanged(nameof(IconGlyph));
            }
        }

        public string ServerRelativeUrl
        {
            get => _serverRelativeUrl;
            set { _serverRelativeUrl = value; OnPropertyChanged(nameof(ServerRelativeUrl)); }
        }

        public long Length
        {
            get => _length;
            set
            {
                _length = value;
                OnPropertyChanged(nameof(Length));
                OnPropertyChanged(nameof(FormattedSize));
            }
        }

        public int CheckOutType
        {
            get => _checkOutType;
            set
            {
                _checkOutType = value;
                OnPropertyChanged(nameof(CheckOutType));
                OnPropertyChanged(nameof(IsCheckedOut));
                OnPropertyChanged(nameof(CheckOutStatusDisplay));
            }
        }

        public string CheckedOutByUser
        {
            get => _checkedOutByUser;
            set { _checkedOutByUser = value; OnPropertyChanged(nameof(CheckedOutByUser)); }
        }

        public string Version
        {
            get => _version;
            set { _version = value; OnPropertyChanged(nameof(Version)); }
        }

        public int ModerationStatusCode
        {
            get => _moderationStatus;
            set
            {
                _moderationStatus = value;
                OnPropertyChanged(nameof(ModerationStatusCode));
                OnPropertyChanged(nameof(ApprovalStatusDisplay));
            }
        }

        public string ModerationComments
        {
            get => _moderationComments;
            set { _moderationComments = value; OnPropertyChanged(nameof(ModerationComments)); }
        }

        public DateTime TimeLastModified
        {
            get => _timeLastModified;
            set { _timeLastModified = value; OnPropertyChanged(nameof(TimeLastModified)); }
        }

        public string ModifiedBy
        {
            get => _modifiedBy;
            set { _modifiedBy = value; OnPropertyChanged(nameof(ModifiedBy)); }
        }

        public string ContentType
        {
            get => _contentType;
            set { _contentType = value; OnPropertyChanged(nameof(ContentType)); }
        }

        public string UIVersion
        {
            get => _uiVersion;
            set { _uiVersion = value; OnPropertyChanged(nameof(UIVersion)); }
        }

        public bool IsFolder
        {
            get => _isFolder;
            set
            {
                _isFolder = value;
                OnPropertyChanged(nameof(IsFolder));
                OnPropertyChanged(nameof(IconGlyph));
            }
        }

        public bool IsCheckedOut => CheckOutType != 2 && CheckOutType != 0 ? true : (CheckOutType == 1 || !string.IsNullOrEmpty(CheckedOutByUser));

        public string CheckOutStatusDisplay
        {
            get
            {
                if (CheckOutType == 2 || (CheckOutType == 0 && string.IsNullOrEmpty(CheckedOutByUser)))
                    return "Checked In";
                return string.IsNullOrEmpty(CheckedOutByUser) ? "Checked Out" : $"Checked Out ({CheckedOutByUser})";
            }
        }

        public string ApprovalStatusDisplay
        {
            get
            {
                return ModerationStatusCode switch
                {
                    0 => "Approved",
                    1 => "Denied",
                    2 => "Pending",
                    3 => "Draft",
                    4 => "Scheduled",
                    _ => "N/A"
                };
            }
        }

        public string FileExtension
        {
            get
            {
                if (IsFolder) return "Folder";
                var ext = Path.GetExtension(Name)?.ToLowerInvariant();
                return string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.');
            }
        }

        public string FormattedSize
        {
            get
            {
                if (IsFolder) return "";
                if (Length < 1024) return $"{Length} B";
                if (Length < 1024 * 1024) return $"{Length / 1024.0:F1} KB";
                return $"{Length / (1024.0 * 1024.0):F2} MB";
            }
        }

        public string IconGlyph
        {
            get
            {
                if (IsFolder) return "📁";
                var ext = Path.GetExtension(Name)?.ToLowerInvariant();
                return ext switch
                {
                    ".master" => "⚡",
                    ".html" or ".htm" => "🌐",
                    ".css" => "🎨",
                    ".js" => "📜",
                    ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" => "🖼️",
                    ".aspx" => "📄",
                    ".xml" or ".json" => "⚙️",
                    _ => "📝"
                };
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class SharePointFolder : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _serverRelativeUrl = string.Empty;
        private bool _isExpanded;
        private bool _isSelected;
        private ObservableCollection<SharePointFolder> _subFolders = new();

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public string ServerRelativeUrl
        {
            get => _serverRelativeUrl;
            set { _serverRelativeUrl = value; OnPropertyChanged(nameof(ServerRelativeUrl)); }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public ObservableCollection<SharePointFolder> SubFolders
        {
            get => _subFolders;
            set { _subFolders = value; OnPropertyChanged(nameof(SubFolders)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class TransferProgress
    {
        public string CurrentItemName { get; set; } = string.Empty;
        public int CurrentIndex { get; set; }
        public int TotalItems { get; set; }
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public bool IsIndeterminate { get; set; }
        public double Percentage => TotalItems > 0 ? (double)CurrentIndex / TotalItems * 100.0 : 0.0;
    }

    public class BreadcrumbItem
    {
        public string Name { get; set; } = string.Empty;
        public string ServerRelativeUrl { get; set; } = string.Empty;
        public bool IsLast { get; set; }
    }
}
