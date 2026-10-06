using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    public interface ISharePointService
    {
        bool IsConnected { get; }
        SharePointSiteInfo? CurrentSiteInfo { get; }
        string BaseUrl { get; }

        Task<bool> ConnectAsync(ConnectionProfile profile, string plainPassword, CancellationToken cancellationToken = default);
        void Disconnect();
        Task SwitchWebAsync(string webUrl, CancellationToken cancellationToken = default);
        string ToAbsoluteUrl(string serverRelativeUrl);

        // Site Explorer (SharePoint Designer "Site Objects")
        Task<SiteDetails> GetSiteDetailsAsync(CancellationToken cancellationToken = default);
        Task UpdateSiteInfoAsync(string title, string description, CancellationToken cancellationToken = default);
        Task SetMasterPageAsync(string masterPageServerRelativeUrl, bool siteMaster, bool systemMaster, CancellationToken cancellationToken = default);
        Task<SPWebInfo?> GetParentWebAsync(CancellationToken cancellationToken = default);
        Task<List<SPWebInfo>> GetSubsitesAsync(CancellationToken cancellationToken = default);
        Task<List<SPListInfo>> GetListsAsync(CancellationToken cancellationToken = default);
        Task<List<SPListItemInfo>> GetListItemsAsync(string listId, int maxItems = 2000, CancellationToken cancellationToken = default);
        Task<List<SPContentTypeInfo>> GetContentTypesAsync(CancellationToken cancellationToken = default);
        Task<List<SPFieldInfo>> GetSiteColumnsAsync(CancellationToken cancellationToken = default);
        Task<List<SPGroupInfo>> GetGroupsAsync(CancellationToken cancellationToken = default);
        Task<List<SPUserInfo>> GetGroupMembersAsync(int groupId, CancellationToken cancellationToken = default);
        Task<List<SPRoleAssignmentInfo>> GetRoleAssignmentsAsync(CancellationToken cancellationToken = default);
        // Deployment (UAT → Production)
        Task<SharePointItem?> TryGetFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);
        Task<bool> FolderExistsAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);
        Task<bool> FolderIsInListAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);
        Task<ListItemSnapshot> GetFileListItemAsync(string fileServerRelativeUrl, CancellationToken cancellationToken = default);
        Task<SPListSettings> GetFileParentListAsync(string fileServerRelativeUrl, CancellationToken cancellationToken = default);
        Task<int> GetFileListItemIdAsync(string fileServerRelativeUrl, CancellationToken cancellationToken = default);
        Task<List<SPFieldInfo>> GetListFieldsAsync(string listId, CancellationToken cancellationToken = default, string? webUrl = null);
        Task<List<SPContentTypeInfo>> GetListContentTypesAsync(string listId, CancellationToken cancellationToken = default, string? webUrl = null);
        Task UpdateListItemAsync(string listId, int itemId, string entityTypeName, IDictionary<string, object?> values, CancellationToken cancellationToken = default, string? fileServerRelativeUrl = null);

        Task<WriteProbeResult> ProbeAsync(string name, System.Net.Http.HttpMethod method, string relativeUrl, CancellationToken cancellationToken = default,
            string? requestBody = null, string? requestContentType = null);
        Task<PermissionInfo?> GetEffectivePermissionsAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);

        Task RecycleAsync(string serverRelativeUrl, bool isFolder, CancellationToken cancellationToken = default);

        Task<string> GetStyleLibraryServerRelativeUrlAsync(CancellationToken cancellationToken = default);
        Task<string> GetMasterPageGalleryServerRelativeUrlAsync(CancellationToken cancellationToken = default);

        Task<List<SharePointItem>> GetMasterPageGalleryItemsAsync(CancellationToken cancellationToken = default);
        Task<SharePointFolder> GetFolderHierarchyAsync(string rootRelativeUrl, CancellationToken cancellationToken = default);
        Task<List<SharePointFolder>> GetSubFoldersAsync(string folderServerRelativeUrl, CancellationToken cancellationToken = default);
        Task<List<SharePointItem>> GetFolderFilesAsync(string folderServerRelativeUrl, CancellationToken cancellationToken = default);
        Task<List<SharePointItem>> GetFolderContentsAsync(string folderServerRelativeUrl, CancellationToken cancellationToken = default);
        
        Task<SharePointItem> GetFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);
        Task<byte[]> DownloadFileAsync(string serverRelativeUrl, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
        Task UploadFileAsync(string folderServerRelativeUrl, string fileName, byte[] content, bool overwrite, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
        Task CreateFolderAsync(string parentServerRelativeUrl, string newFolderName, CancellationToken cancellationToken = default);
        
        Task CheckOutAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);
        Task CheckInAsync(string serverRelativeUrl, int checkInType, string comment, CancellationToken cancellationToken = default);
        Task PublishAsync(string serverRelativeUrl, string comment, CancellationToken cancellationToken = default);
        Task UndoCheckOutAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);
        Task ApproveAsync(string serverRelativeUrl, string comment, CancellationToken cancellationToken = default);
        Task DenyAsync(string serverRelativeUrl, string comment, CancellationToken cancellationToken = default);
    }
}
