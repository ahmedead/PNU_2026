using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    /// <summary>
    /// Site Explorer operations (SharePoint Designer "Site Objects"): site info, lists, subsites,
    /// content types, columns, groups and permissions.
    /// </summary>
    public partial class SharePointRestService
    {
        #region JSON helpers

        private static string JStr(JsonElement el, string name) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        private static int JInt(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            return 0;
        }

        private static bool JBool(JsonElement el, string name) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
            (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

        private static DateTime JDate(JsonElement el, string name) =>
            DateTime.TryParse(JStr(el, name), out var dt) ? dt : DateTime.MinValue;

        private static JsonElement JObj(JsonElement el, string name) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

        #endregion

        #region Request helpers

        private async Task<string> GetStringOrThrowAsync(string url, CancellationToken cancellationToken)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {ExtractErrorMessage(body)}");
            }
            return body;
        }

        /// <summary>Returns the "d" object of a single-entity response.</summary>
        private async Task<JsonElement> GetEntityAsync(string url, CancellationToken cancellationToken)
        {
            var body = await GetStringOrThrowAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("d", out var d) ? d.Clone() : doc.RootElement.Clone();
        }

        /// <summary>
        /// Reads a collection, following OData "__next" paging links up to <paramref name="maxItems"/>.
        /// </summary>
        private async Task<List<JsonElement>> GetCollectionAsync(string url, CancellationToken cancellationToken, int maxItems = 5000)
        {
            var results = new List<JsonElement>();
            string? next = url;

            while (!string.IsNullOrEmpty(next) && results.Count < maxItems)
            {
                var body = await GetStringOrThrowAsync(next, cancellationToken);
                using var doc = JsonDocument.Parse(body);
                next = null;

                if (doc.RootElement.TryGetProperty("d", out var d))
                {
                    if (d.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        results.AddRange(arr.EnumerateArray().Select(e => e.Clone()));
                    }
                    next = JStr(d, "__next");
                }
            }

            return results;
        }

        /// <summary>
        /// POSTs with a form digest, refreshing it once if SharePoint reports it expired.
        /// </summary>
        private async Task PostWithDigestAsync(string url, Func<HttpContent> contentFactory, string? xHttpMethod, CancellationToken cancellationToken,
            string action = "Update", string? resourcePath = null)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");
            await RefreshFormDigestAsync(cancellationToken);

            string lastError = string.Empty;
            HttpStatusCode lastStatus = 0;
            string? lastReason = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = contentFactory() };
                request.Headers.Add("X-RequestDigest", _formDigestValue);
                request.Headers.Accept.Clear();
                request.Headers.TryAddWithoutValidation("Accept", "application/json;odata=verbose");
                if (!string.IsNullOrEmpty(xHttpMethod))
                {
                    request.Headers.Add("X-HTTP-Method", xHttpMethod);
                    request.Headers.TryAddWithoutValidation("IF-MATCH", "*");
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) return;

                lastError = await response.Content.ReadAsStringAsync(cancellationToken);
                lastStatus = response.StatusCode;
                lastReason = response.ReasonPhrase;
                if (IsExpiredDigest(response.StatusCode, lastError))
                {
                    await RefreshFormDigestAsync(cancellationToken, force: true);
                    continue;
                }
                break;
            }

            throw await CreateWriteFailureAsync(action, resourcePath ?? ServerRelativeWebUrl, lastStatus, lastReason, lastError, cancellationToken);
        }

        private string ServerRelativeWebUrl => _currentSiteInfo?.ServerRelativeUrl ?? "/";

        private static HttpContent EmptyContent() => new ByteArrayContent(Array.Empty<byte>());

        private static HttpContent VerboseJson(object payload)
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json;odata=verbose");
            return content;
        }

        #endregion

        /// <summary>
        /// Builds an absolute URL on the host the user connected with (SharePoint may report
        /// a different Alternate Access Mapping zone URL in "Url" properties).
        /// </summary>
        public string ToAbsoluteUrl(string serverRelativeUrl)
        {
            if (Uri.TryCreate(serverRelativeUrl, UriKind.Absolute, out var abs)) return abs.AbsoluteUri;
            return new Uri(new Uri(_baseUrl), serverRelativeUrl.StartsWith("/") ? serverRelativeUrl : "/" + serverRelativeUrl).AbsoluteUri.TrimEnd('/');
        }

        public async Task<SiteDetails> GetSiteDetailsAsync(CancellationToken cancellationToken = default)
        {
            // No $select: every scalar property is returned, which avoids 400s on versions missing one of them
            var d = await GetEntityAsync("_api/web", cancellationToken);
            var details = new SiteDetails
            {
                Id = JStr(d, "Id"),
                Title = JStr(d, "Title"),
                Description = JStr(d, "Description"),
                Url = JStr(d, "Url"),
                ServerRelativeUrl = JStr(d, "ServerRelativeUrl"),
                WebTemplate = JStr(d, "WebTemplate"),
                Configuration = JInt(d, "Configuration"),
                Language = JInt(d, "Language"),
                UIVersion = JInt(d, "UIVersion"),
                Created = JDate(d, "Created"),
                LastItemModifiedDate = JDate(d, "LastItemModifiedDate"),
                MasterUrl = JStr(d, "MasterUrl"),
                CustomMasterUrl = JStr(d, "CustomMasterUrl"),
                AlternateCssUrl = JStr(d, "AlternateCssUrl"),
                SiteLogoUrl = JStr(d, "SiteLogoUrl")
            };

            try
            {
                var perms = await GetEntityAsync("_api/web?$select=HasUniqueRoleAssignments", cancellationToken);
                details.HasUniqueRoleAssignments = JBool(perms, "HasUniqueRoleAssignments");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"HasUniqueRoleAssignments unavailable: {ex.Message}");
            }

            return details;
        }

        public async Task UpdateSiteInfoAsync(string title, string description, CancellationToken cancellationToken = default)
        {
            await PostWithDigestAsync("_api/web",
                () => VerboseJson(new Dictionary<string, object>
                {
                    ["__metadata"] = new { type = "SP.Web" },
                    ["Title"] = title,
                    ["Description"] = description
                }),
                "MERGE", cancellationToken);

            if (_currentSiteInfo != null) _currentSiteInfo.Title = title;
        }

        public async Task SetMasterPageAsync(string masterPageServerRelativeUrl, bool siteMaster, bool systemMaster, CancellationToken cancellationToken = default)
        {
            var payload = new Dictionary<string, object> { ["__metadata"] = new { type = "SP.Web" } };
            if (siteMaster) payload["CustomMasterUrl"] = masterPageServerRelativeUrl; // Used by publishing / content pages
            if (systemMaster) payload["MasterUrl"] = masterPageServerRelativeUrl;     // Used by settings, lists and forms

            await PostWithDigestAsync("_api/web", () => VerboseJson(payload), "MERGE", cancellationToken, "Assigning master page");
        }

        public async Task<SPWebInfo?> GetParentWebAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var d = await GetEntityAsync("_api/web/ParentWeb?$select=Title,ServerRelativeUrl", cancellationToken);
                var sru = JStr(d, "ServerRelativeUrl");
                if (string.IsNullOrEmpty(sru)) return null; // Root web of the site collection

                return new SPWebInfo
                {
                    Title = JStr(d, "Title"),
                    ServerRelativeUrl = sru,
                    Url = ToAbsoluteUrl(sru)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Root webs return null / 404 for ParentWeb, and users may lack access to the parent
                System.Diagnostics.Debug.WriteLine($"ParentWeb unavailable: {ex.Message}");
                return null;
            }
        }

        public async Task<List<SPWebInfo>> GetSubsitesAsync(CancellationToken cancellationToken = default)
        {
            var items = await GetCollectionAsync(
                "_api/web/webs?$select=Title,Url,ServerRelativeUrl,Description,WebTemplate,Configuration,Created,LastItemModifiedDate",
                cancellationToken);

            return items.Select(e => new SPWebInfo
            {
                Title = JStr(e, "Title"),
                ServerRelativeUrl = JStr(e, "ServerRelativeUrl"),
                Url = ToAbsoluteUrl(JStr(e, "ServerRelativeUrl")),
                Description = JStr(e, "Description"),
                WebTemplate = JStr(e, "WebTemplate"),
                Configuration = JInt(e, "Configuration"),
                Created = JDate(e, "Created"),
                LastItemModifiedDate = JDate(e, "LastItemModifiedDate")
            })
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        public async Task<List<SPListInfo>> GetListsAsync(CancellationToken cancellationToken = default)
        {
            var items = await GetCollectionAsync(
                "_api/web/lists?$select=Id,Title,Description,BaseTemplate,BaseType,ItemCount,Hidden,LastItemModifiedDate,DefaultViewUrl,RootFolder/ServerRelativeUrl&$expand=RootFolder",
                cancellationToken);

            return items.Select(e => new SPListInfo
            {
                Id = JStr(e, "Id"),
                Title = JStr(e, "Title"),
                Description = JStr(e, "Description"),
                BaseTemplate = JInt(e, "BaseTemplate"),
                BaseType = JInt(e, "BaseType"),
                ItemCount = JInt(e, "ItemCount"),
                Hidden = JBool(e, "Hidden"),
                LastItemModifiedDate = JDate(e, "LastItemModifiedDate"),
                DefaultViewUrl = JStr(e, "DefaultViewUrl"),
                RootFolderUrl = JStr(JObj(e, "RootFolder"), "ServerRelativeUrl")
            })
            .OrderBy(l => l.IsLibrary ? 0 : 1)
            .ThenBy(l => l.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        public async Task<List<SPListItemInfo>> GetListItemsAsync(string listId, int maxItems = 2000, CancellationToken cancellationToken = default)
        {
            var baseUrl = $"_api/web/lists(guid'{listId}')/items";
            List<JsonElement> items;
            try
            {
                items = await GetCollectionAsync($"{baseUrl}?$select=Id,Title,Created,Modified,FileRef,FSObjType&$top=500", cancellationToken, maxItems);
            }
            catch (InvalidOperationException)
            {
                // Some list types have no Title field
                items = await GetCollectionAsync($"{baseUrl}?$select=Id,Created,Modified,FileRef,FSObjType&$top=500", cancellationToken, maxItems);
            }

            return items.Select(e =>
            {
                var fileRef = JStr(e, "FileRef");
                var title = JStr(e, "Title");
                return new SPListItemInfo
                {
                    Id = JInt(e, "Id"),
                    Title = string.IsNullOrEmpty(title) ? System.IO.Path.GetFileName(fileRef) : title,
                    FileRef = fileRef,
                    IsFolder = JInt(e, "FSObjType") == 1,
                    Created = JDate(e, "Created"),
                    Modified = JDate(e, "Modified")
                };
            }).ToList();
        }

        public async Task<List<SPContentTypeInfo>> GetContentTypesAsync(CancellationToken cancellationToken = default)
        {
            // AvailableContentTypes includes those inherited from parent webs, like SharePoint Designer shows
            var items = await GetCollectionAsync("_api/web/AvailableContentTypes", cancellationToken);

            return items.Select(e =>
            {
                var id = JStr(e, "StringId");
                if (string.IsNullOrEmpty(id)) id = JStr(JObj(e, "Id"), "StringValue");
                return new SPContentTypeInfo
                {
                    Id = id,
                    Name = JStr(e, "Name"),
                    Group = JStr(e, "Group"),
                    Description = JStr(e, "Description"),
                    Hidden = JBool(e, "Hidden"),
                    ReadOnly = JBool(e, "ReadOnly"),
                    Sealed = JBool(e, "Sealed")
                };
            })
            .OrderBy(c => c.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        public async Task<List<SPFieldInfo>> GetSiteColumnsAsync(CancellationToken cancellationToken = default)
        {
            var items = await GetCollectionAsync(
                "_api/web/AvailableFields?$select=Id,Title,InternalName,TypeAsString,Group,Description,Hidden,Required,ReadOnlyField",
                cancellationToken);

            return items.Select(e => new SPFieldInfo
            {
                Id = JStr(e, "Id"),
                Title = JStr(e, "Title"),
                InternalName = JStr(e, "InternalName"),
                TypeAsString = JStr(e, "TypeAsString"),
                Group = JStr(e, "Group"),
                Description = JStr(e, "Description"),
                Hidden = JBool(e, "Hidden"),
                Required = JBool(e, "Required"),
                ReadOnlyField = JBool(e, "ReadOnlyField")
            })
            .OrderBy(f => f.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        public async Task<List<SPGroupInfo>> GetGroupsAsync(CancellationToken cancellationToken = default)
        {
            var items = await GetCollectionAsync("_api/web/sitegroups?$select=Id,Title,Description,OwnerTitle", cancellationToken);

            return items.Select(e => new SPGroupInfo
            {
                Id = JInt(e, "Id"),
                Title = JStr(e, "Title"),
                Description = JStr(e, "Description"),
                OwnerTitle = JStr(e, "OwnerTitle")
            })
            .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        public async Task<List<SPUserInfo>> GetGroupMembersAsync(int groupId, CancellationToken cancellationToken = default)
        {
            var items = await GetCollectionAsync($"_api/web/sitegroups/GetById({groupId})/users?$select=Id,Title,LoginName,Email,IsSiteAdmin", cancellationToken);

            return items.Select(e => new SPUserInfo
            {
                Id = JInt(e, "Id"),
                Title = JStr(e, "Title"),
                LoginName = JStr(e, "LoginName"),
                Email = JStr(e, "Email"),
                IsSiteAdmin = JBool(e, "IsSiteAdmin")
            })
            .OrderBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        public async Task<List<SPRoleAssignmentInfo>> GetRoleAssignmentsAsync(CancellationToken cancellationToken = default)
        {
            var items = await GetCollectionAsync("_api/web/roleassignments?$expand=Member,RoleDefinitionBindings", cancellationToken);

            return items.Select(e =>
            {
                var member = JObj(e, "Member");
                var bindings = JObj(e, "RoleDefinitionBindings");
                var levels = bindings.ValueKind == JsonValueKind.Object && bindings.TryGetProperty("results", out var arr)
                    ? string.Join(", ", arr.EnumerateArray().Select(r => JStr(r, "Name")).Where(n => n.Length > 0))
                    : string.Empty;

                return new SPRoleAssignmentInfo
                {
                    PrincipalName = JStr(member, "Title"),
                    LoginName = JStr(member, "LoginName"),
                    PrincipalType = JInt(member, "PrincipalType"),
                    PermissionLevels = levels
                };
            })
            .OrderBy(r => r.PrincipalType == 8 ? 0 : 1)
            .ThenBy(r => r.PrincipalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        /// <summary>Moves a file or folder to the site Recycle Bin (restorable).</summary>
        public async Task RecycleAsync(string serverRelativeUrl, bool isFolder, CancellationToken cancellationToken = default)
        {
            var safePath = SafeUrl(serverRelativeUrl);
            var target = isFolder ? "GetFolderByServerRelativeUrl" : "GetFileByServerRelativeUrl";
            await PostWithDigestAsync($"_api/web/{target}('{safePath}')/recycle", EmptyContent, null, cancellationToken, "Delete", serverRelativeUrl);
        }
    }
}
