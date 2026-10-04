using System;
using System.Collections.Generic;

namespace SPAdminStudio.Models
{
    /// <summary>
    /// Navigation sections of the Site Explorer (mirrors SharePoint Designer's "Site Objects" pane).
    /// The int values are used by the view to switch panels.
    /// </summary>
    public enum SiteSection
    {
        Summary = 0,
        ListsAndLibraries = 1,
        Subsites = 2,
        AllFiles = 3,
        ContentTypes = 4,
        SiteColumns = 5,
        Groups = 6,
        Permissions = 7,
        ListItems = 8 // Drill-down view opened from Lists and Libraries
    }

    public class SiteSectionItem
    {
        public SiteSection Section { get; init; }
        public string Icon { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
    }

    public class SiteDetails
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ServerRelativeUrl { get; set; } = string.Empty;
        public string WebTemplate { get; set; } = string.Empty;
        public int Configuration { get; set; }
        public int Language { get; set; }
        public int UIVersion { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastItemModifiedDate { get; set; }
        public string MasterUrl { get; set; } = string.Empty;
        public string CustomMasterUrl { get; set; } = string.Empty;
        public string AlternateCssUrl { get; set; } = string.Empty;
        public string SiteLogoUrl { get; set; } = string.Empty;
        public bool? HasUniqueRoleAssignments { get; set; }

        public string TemplateDisplay => string.IsNullOrEmpty(WebTemplate) ? "" : $"{WebTemplate}#{Configuration}";

        public string LanguageDisplay
        {
            get
            {
                if (Language == 0) return "";
                try { return $"{new System.Globalization.CultureInfo(Language).DisplayName} ({Language})"; }
                catch { return Language.ToString(); }
            }
        }

        public string PermissionsDisplay => HasUniqueRoleAssignments switch
        {
            true => "Unique permissions",
            false => "Inherits from parent",
            _ => "Unknown"
        };
    }

    public class SPWebInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ServerRelativeUrl { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string WebTemplate { get; set; } = string.Empty;
        public int Configuration { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastItemModifiedDate { get; set; }

        public string TemplateDisplay => string.IsNullOrEmpty(WebTemplate) ? "" : $"{WebTemplate}#{Configuration}";
    }

    public class SPListInfo
    {
        private static readonly Dictionary<int, string> TemplateNames = new()
        {
            [100] = "Custom List", [101] = "Document Library", [102] = "Survey", [103] = "Links",
            [104] = "Announcements", [105] = "Contacts", [106] = "Calendar", [107] = "Tasks",
            [108] = "Discussion Board", [109] = "Picture Library", [110] = "Data Sources",
            [113] = "Web Part Gallery", [114] = "List Template Gallery", [115] = "Form Library",
            [116] = "Master Page Gallery", [118] = "Workflow Process", [119] = "Wiki Page Library",
            [120] = "Custom Grid", [121] = "Solution Gallery", [122] = "No-Code Workflows",
            [123] = "Theme Gallery", [130] = "Data Connection Library", [140] = "Workflow History",
            [150] = "Gantt Tasks", [171] = "Tasks", [175] = "Maintenance Logs", [432] = "Status List",
            [433] = "Report Library", [544] = "Persistent Storage", [850] = "Pages Library",
            [851] = "Asset Library", [1100] = "Issue Tracking", [3100] = "Access App"
        };

        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int BaseTemplate { get; set; }
        public int BaseType { get; set; }
        public int ItemCount { get; set; }
        public bool Hidden { get; set; }
        public DateTime LastItemModifiedDate { get; set; }
        public string RootFolderUrl { get; set; } = string.Empty;
        public string DefaultViewUrl { get; set; } = string.Empty;

        public bool IsLibrary => BaseType == 1;
        public string Kind => IsLibrary ? "Document Libraries" : "Lists";
        public string Icon => IsLibrary ? "📚" : "📋";
        public string TemplateName => TemplateNames.TryGetValue(BaseTemplate, out var name) ? name : $"Template {BaseTemplate}";
    }

    public class SPListItemInfo
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string FileRef { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string Icon => IsFolder ? "📁" : "📄";
    }

    public class SPContentTypeInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool Hidden { get; set; }
        public bool ReadOnly { get; set; }
        public bool Sealed { get; set; }
    }

    public class SPFieldInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string TypeAsString { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool Hidden { get; set; }
        public bool Required { get; set; }
        public bool ReadOnlyField { get; set; }
    }

    public class SPGroupInfo
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string OwnerTitle { get; set; } = string.Empty;
    }

    public class SPUserInfo
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string LoginName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsSiteAdmin { get; set; }
    }

    /// <summary>Outcome of one harmless probe request (see SharePointRestService.ProbeAsync).</summary>
    public class WriteProbeResult
    {
        public string Name { get; init; } = string.Empty;
        public string Method { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public int StatusCode { get; set; }
        public bool ReachedApi { get; set; }
        public bool ServedBySharePoint { get; set; }
        public string Server { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public string Describe()
        {
            var verdict = ReachedApi
                ? "✔ reaches the SharePoint API"
                : StatusCode == 0
                    ? "✘ no response"
                    : ServedBySharePoint
                        ? "✘ answered by SharePoint as a normal web page — the API address was not recognised (URL rewritten on the way in?)"
                        : $"✘ blocked before SharePoint (answered by {(string.IsNullOrEmpty(Server) ? "another server" : Server)})";
            return $"{Name}\n    {Method} → HTTP {StatusCode}: {verdict}\n    {Detail}";
        }
    }

    /// <summary>SP.BasePermissions (EffectiveBasePermissions) decoded for the actions this app performs.</summary>
    public class PermissionInfo
    {
        public ulong Low { get; init; }
        public ulong High { get; init; }
        public string Scope { get; init; } = string.Empty;
        public string Account { get; init; } = string.Empty;

        // SP.PermissionKind values are 1-based bit positions
        private bool Has(int kind) => kind <= 32 ? (Low & (1UL << (kind - 1))) != 0 : (High & (1UL << (kind - 33))) != 0;

        public bool CanView => Has(1);
        public bool CanAdd => Has(2);
        public bool CanEdit => Has(3);
        public bool CanDelete => Has(4);
        public bool CanApprove => Has(5);
        public bool CanCancelCheckout => Has(9);
        public bool CanManageLists => Has(12);

        public string Describe()
        {
            static string Mark(bool allowed, string name) => (allowed ? "✔ " : "✘ ") + name;
            return string.Join("  ", Mark(CanView, "View"), Mark(CanAdd, "Add"), Mark(CanEdit, "Edit"),
                Mark(CanDelete, "Delete"), Mark(CanApprove, "Approve"), Mark(CanCancelCheckout, "Override checkout"),
                Mark(CanManageLists, "Manage library"));
        }
    }

    public class SPRoleAssignmentInfo
    {
        public string PrincipalName { get; set; } = string.Empty;
        public string LoginName { get; set; } = string.Empty;
        public int PrincipalType { get; set; }
        public string PermissionLevels { get; set; } = string.Empty;

        public string PrincipalTypeDisplay => PrincipalType switch
        {
            1 => "User",
            2 => "Distribution List",
            4 => "Security Group",
            8 => "SharePoint Group",
            _ => PrincipalType.ToString()
        };

        public string Icon => PrincipalType == 1 ? "👤" : "👥";
    }
}
