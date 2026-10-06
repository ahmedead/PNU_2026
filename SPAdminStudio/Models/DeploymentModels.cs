using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SPAdminStudio.Models
{
    public enum DeployItemKind
    {
        File,
        Folder
    }

    public enum DeployStatus
    {
        Pending,
        New,        // Does not exist on target yet
        Overwrite,  // Exists on target and will be replaced (after backup)
        Skip,       // Exists on target and overwrite is off
        Blocked,    // Checked out by someone else on target
        Deploying,
        Done,
        Warning,    // Deployed, but a post-step (metadata, check-in, approve) had an issue
        Failed
    }

    /// <summary>What the user asked to deploy (a file or a whole folder from the source).</summary>
    public class DeploymentQueueEntry
    {
        public string Name { get; init; } = string.Empty;
        public string SourceUrl { get; init; } = string.Empty;
        public bool IsFolder { get; init; }
        public string Icon => IsFolder ? "📁" : "📄";
    }

    /// <summary>One concrete step of the deployment plan (produced by Analyze).</summary>
    public partial class DeploymentPlanItem : ObservableObject
    {
        public DeployItemKind Kind { get; init; }
        public string SourceUrl { get; init; } = string.Empty;
        public string TargetUrl { get; init; } = string.Empty;
        public long Size { get; init; }
        public DateTime? SourceModified { get; init; }
        public DateTime? TargetModified { get; set; }

        [ObservableProperty] private DeployStatus _status = DeployStatus.Pending;
        [ObservableProperty] private string _message = string.Empty;

        public string Name => Path.GetFileName(SourceUrl.TrimEnd('/'));
        public string Icon => Kind == DeployItemKind.Folder ? "📁" : "📄";
        public string SizeDisplay => Kind == DeployItemKind.Folder ? "" : Size < 1024 ? $"{Size} B" : $"{Size / 1024.0:F1} KB";

        public bool IsActionable => Status is DeployStatus.New or DeployStatus.Overwrite or DeployStatus.Pending;
    }

    public enum CheckInMode
    {
        LeaveCheckedOut = 0,
        Draft = 1,          // Minor version (major if the library has no minor versions)
        PublishMajor = 2
    }

    public class DeploymentOptions
    {
        public bool Overwrite { get; set; } = true;
        public bool BackupTarget { get; set; } = true;
        public string BackupFolder { get; set; } = string.Empty;
        public bool CopyMetadata { get; set; } = true;
        public bool RewriteUrls { get; set; } = true;
        public CheckInMode CheckIn { get; set; } = CheckInMode.PublishMajor;
        public bool Approve { get; set; } = true;
        public string Comment { get; set; } = "Deployed via SPAdminStudio";
    }

    /// <summary>Raw field values of a file's list item (REST property name → JSON value).</summary>
    public class ListItemSnapshot
    {
        public int Id { get; init; }
        public string ContentTypeName { get; init; } = string.Empty;
        public Dictionary<string, JsonElement> Values { get; init; } = new();
    }

    public class SPListSettings
    {
        public string Id { get; init; } = string.Empty;
        /// <summary>Server-relative URL of the web that owns the list (list IDs only resolve there).</summary>
        public string WebUrl { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string EntityTypeName { get; init; } = string.Empty;
        public int BaseTemplate { get; init; }
        public bool EnableModeration { get; init; }
        public bool EnableMinorVersions { get; init; }
        public bool ForceCheckout { get; init; }
    }
}
