using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    /// <summary>
    /// Low-level operations used by the UAT → Production deployment engine.
    /// </summary>
    public partial class SharePointRestService
    {
        /// <summary>Returns the file's current state, or null when it does not exist.</summary>
        public async Task<SharePointItem?> TryGetFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var safePath = SafeUrl(serverRelativeUrl);
            var (api, _) = await WebApiForPathAsync(serverRelativeUrl, cancellationToken);
            var url = $"{api}/GetFileByServerRelativeUrl('{safePath}')?$expand=ModifiedBy,CheckedOutByUser&$select=Exists,Name,ServerRelativeUrl,Length,CheckOutType,CheckedOutByUser/Title,ModifiedBy/Title,TimeLastModified,UIVersionLabel";

            using var response = await GetWithLoginRetryAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // SharePoint 2013 reports a missing file as 500 with "File Not Found" (-2130575338)
                if (IsNotFoundBody(body)) return null;
                throw new InvalidOperationException($"Could not check '{serverRelativeUrl}': {DescribeReadFailure(response, body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement.TryGetProperty("d", out var d) ? d : doc.RootElement;
            if (el.TryGetProperty("Exists", out var exists) && exists.ValueKind == JsonValueKind.False) return null;
            return ParseFileItem(el);
        }

        public async Task<bool> FolderExistsAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var safePath = SafeUrl(serverRelativeUrl.TrimEnd('/'));
            var (api, _) = await WebApiForPathAsync(serverRelativeUrl.TrimEnd('/'), cancellationToken);
            using var response = await GetWithLoginRetryAsync($"{api}/GetFolderByServerRelativeUrl('{safePath}')?$select=Exists,Name", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Only "not found" means "does not exist": a refused login or server error must not look like
                // a missing folder (the deployment would then try to create it)
                if (response.StatusCode == HttpStatusCode.NotFound || IsNotFoundBody(body)) return false;
                throw new InvalidOperationException($"Could not check folder '{serverRelativeUrl}': {DescribeReadFailure(response, body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement.TryGetProperty("d", out var d) ? d : doc.RootElement;
            return !(el.TryGetProperty("Exists", out var exists) && exists.ValueKind == JsonValueKind.False);
        }

        /// <summary>
        /// True when the folder is a list/library root or a folder inside one, i.e. it is safe to create
        /// sub-folders there. (Creating a folder directly in a web would NOT create a library.)
        /// </summary>
        public async Task<bool> FolderIsInListAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");
            var safePath = SafeUrl(serverRelativeUrl.TrimEnd('/'));

            var (api, _) = await WebApiForPathAsync(serverRelativeUrl.TrimEnd('/'), cancellationToken);
            using (var listResponse = await _httpClient.GetAsync($"{api}/GetList('{safePath}')?$select=Id", cancellationToken))
            {
                if (listResponse.IsSuccessStatusCode) return true;
            }

            using var itemResponse = await _httpClient.GetAsync($"{api}/GetFolderByServerRelativeUrl('{safePath}')/ListItemAllFields?$select=Id", cancellationToken);
            if (!itemResponse.IsSuccessStatusCode) return false;

            var body = await itemResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Object && JInt(d, "Id") > 0;
        }

        /// <summary>Reads every field value of the file's list item (plus its content type name).</summary>
        public async Task<ListItemSnapshot> GetFileListItemAsync(string fileServerRelativeUrl, CancellationToken cancellationToken = default)
        {
            var safePath = SafeUrl(fileServerRelativeUrl);
            var (api, _) = await WebApiForPathAsync(fileServerRelativeUrl, cancellationToken);
            var d = await GetEntityAsync($"{api}/GetFileByServerRelativeUrl('{safePath}')/ListItemAllFields?$expand=ContentType", cancellationToken);

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in d.EnumerateObject())
            {
                if (prop.Name == "__metadata" || prop.Name == "ContentType") continue;
                if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("__deferred", out _)) continue;
                values[prop.Name] = prop.Value.Clone();
            }

            return new ListItemSnapshot
            {
                Id = JInt(d, "Id"),
                ContentTypeName = JStr(JObj(d, "ContentType"), "Name"),
                Values = values
            };
        }

        public async Task<SPListSettings> GetFileParentListAsync(string fileServerRelativeUrl, CancellationToken cancellationToken = default)
        {
            var safePath = SafeUrl(fileServerRelativeUrl);
            var (api, web) = await WebApiForPathAsync(fileServerRelativeUrl, cancellationToken);
            var d = await GetEntityAsync(
                $"{api}/GetFileByServerRelativeUrl('{safePath}')/ListItemAllFields/ParentList?$select=Id,Title,ListItemEntityTypeFullName,BaseTemplate,EnableModeration,EnableMinorVersions,ForceCheckout,ParentWebUrl",
                cancellationToken);

            var parentWeb = JStr(d, "ParentWebUrl");
            return new SPListSettings
            {
                // A list ID only resolves on its own web
                WebUrl = string.IsNullOrEmpty(parentWeb) ? web : parentWeb,
                Id = JStr(d, "Id"),
                Title = JStr(d, "Title"),
                EntityTypeName = JStr(d, "ListItemEntityTypeFullName"),
                BaseTemplate = JInt(d, "BaseTemplate"),
                EnableModeration = JBool(d, "EnableModeration"),
                EnableMinorVersions = JBool(d, "EnableMinorVersions"),
                ForceCheckout = JBool(d, "ForceCheckout")
            };
        }

        public async Task<int> GetFileListItemIdAsync(string fileServerRelativeUrl, CancellationToken cancellationToken = default)
        {
            var safePath = SafeUrl(fileServerRelativeUrl);
            var (api, _) = await WebApiForPathAsync(fileServerRelativeUrl, cancellationToken);
            var d = await GetEntityAsync($"{api}/GetFileByServerRelativeUrl('{safePath}')/ListItemAllFields?$select=Id", cancellationToken);
            return JInt(d, "Id");
        }

        public async Task<List<SPFieldInfo>> GetListFieldsAsync(string listId, CancellationToken cancellationToken = default, string? webUrl = null)
        {
            var items = await GetCollectionAsync(
                $"{ApiBaseFor(webUrl)}/lists(guid'{listId}')/fields?$select=Id,Title,InternalName,TypeAsString,Group,Hidden,Required,ReadOnlyField",
                cancellationToken);

            return items.Select(e => new SPFieldInfo
            {
                Id = JStr(e, "Id"),
                Title = JStr(e, "Title"),
                InternalName = JStr(e, "InternalName"),
                TypeAsString = JStr(e, "TypeAsString"),
                Group = JStr(e, "Group"),
                Hidden = JBool(e, "Hidden"),
                Required = JBool(e, "Required"),
                ReadOnlyField = JBool(e, "ReadOnlyField")
            }).ToList();
        }

        public async Task<List<SPContentTypeInfo>> GetListContentTypesAsync(string listId, CancellationToken cancellationToken = default, string? webUrl = null)
        {
            var items = await GetCollectionAsync($"{ApiBaseFor(webUrl)}/lists(guid'{listId}')/contenttypes", cancellationToken);
            return items.Select(e =>
            {
                var id = JStr(e, "StringId");
                if (string.IsNullOrEmpty(id)) id = JStr(JObj(e, "Id"), "StringValue");
                return new SPContentTypeInfo { Id = id, Name = JStr(e, "Name"), Group = JStr(e, "Group") };
            }).ToList();
        }

        /// <summary>MERGE-updates list item fields. Values must already be in REST (verbose OData) shape.</summary>
        public async Task UpdateListItemAsync(string listId, int itemId, string entityTypeName, IDictionary<string, object?> values,
            CancellationToken cancellationToken = default, string? fileServerRelativeUrl = null)
        {
            var payload = new Dictionary<string, object?> { ["__metadata"] = new { type = entityTypeName } };
            foreach (var kv in values) payload[kv.Key] = kv.Value;

            var action = $"Updating properties ({string.Join(", ", values.Keys)})";
            var web = fileServerRelativeUrl != null ? await GetContainingWebUrlAsync(fileServerRelativeUrl, cancellationToken) : null;
            var url = $"{ApiBaseFor(web)}/lists(guid'{listId}')/items({itemId})";

            async Task<(bool, string, Exception?)> Rest()
            {
                var (ok, body, status, reason) = await SendWriteAsync(url, () => VerboseJson(payload), cancellationToken, "MERGE", web);
                return ok
                    ? (true, body, null)
                    : (false, body, await CreateWriteFailureAsync(action, fileServerRelativeUrl ?? ServerRelativeWebUrl, status, reason, body, cancellationToken));
            }

            if (fileServerRelativeUrl == null)
            {
                var (ok, _, failure) = await Rest();
                if (!ok) throw failure!;
                return;
            }

            // Lists web service equivalent of the MERGE (values converted to its string format)
            await WithDesignerFallbackAsync(action, Rest, () => ListsUpdateItemAsync(fileServerRelativeUrl, listId, "Update",
                new[] { ("ID", itemId.ToString()), ("FileRef", HostOrigin + EscapePath(fileServerRelativeUrl)) }.Concat(ToListsFieldValues(values)),
                action, cancellationToken));
        }
    }
}
