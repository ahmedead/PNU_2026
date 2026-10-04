using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    public partial class SharePointRestService : ISharePointService
    {
        private HttpClient? _httpClient;
        private HttpClientHandler? _handler;
        private CookieContainer? _cookieContainer;
        private string _baseUrl = string.Empty;
        private string _formDigestValue = string.Empty;
        private DateTime _digestExpiresAt = DateTime.MinValue;
        private SharePointSiteInfo? _currentSiteInfo;

        public bool IsConnected => _httpClient != null && _currentSiteInfo != null;
        public SharePointSiteInfo? CurrentSiteInfo => _currentSiteInfo;
        public string BaseUrl => _baseUrl;

        private string SiteCollectionRootRelative =>
            (!string.IsNullOrEmpty(_currentSiteInfo?.SiteCollectionServerRelativeUrl)
                ? _currentSiteInfo!.SiteCollectionServerRelativeUrl
                : _currentSiteInfo?.ServerRelativeUrl ?? "").TrimEnd('/');

        public static string SafeUrl(string serverRelativeUrl)
        {
            if (string.IsNullOrWhiteSpace(serverRelativeUrl)) return "";
            serverRelativeUrl = serverRelativeUrl.Trim();
            if (!serverRelativeUrl.StartsWith("/")) serverRelativeUrl = "/" + serverRelativeUrl;

            // Split into segments so forward slashes '/' are preserved and NOT replaced by %2F!
            var segments = serverRelativeUrl.Split(new[] { '/' }, StringSplitOptions.None);
            var escapedSegments = segments.Select(s => Uri.EscapeDataString(s).Replace("'", "''"));
            return string.Join("/", escapedSegments);
        }

        public async Task<bool> ConnectAsync(ConnectionProfile profile, string plainPassword, CancellationToken cancellationToken = default)
        {
            Disconnect();

            _baseUrl = profile.SiteUrl.TrimEnd('/');
            _cookieContainer = new CookieContainer();
            _handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                AllowAutoRedirect = true,
                UseCookies = true
            };

            // Only bypass certificate validation when the profile explicitly opts in (self-signed internal farms)
            if (profile.TrustAllCertificates)
            {
                _handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
            }

            switch (profile.AuthMode)
            {
                case AuthenticationMode.CurrentWindowsUser:
                    _handler.UseDefaultCredentials = true;
                    break;

                case AuthenticationMode.SpecificWindowsDomain:
                    _handler.UseDefaultCredentials = false;
                    _handler.Credentials = string.IsNullOrWhiteSpace(profile.Domain)
                        ? new NetworkCredential(profile.Username, plainPassword)
                        : new NetworkCredential(profile.Username, plainPassword, profile.Domain);
                    break;

                case AuthenticationMode.FormsBasedAuth:
                    _handler.UseDefaultCredentials = false;
                    await PerformFbaLoginAsync(_baseUrl, profile.Username, plainPassword, cancellationToken);
                    break;
            }

            _httpClient = CreateClient(_baseUrl);

            // Refresh digest and load site info
            await RefreshFormDigestAsync(cancellationToken);
            _currentSiteInfo = await FetchSiteInfoAsync(cancellationToken);

            return true;
        }

        /// <summary>
        /// Creates a client bound to one web. The handler (credentials + cookies) is shared and owned
        /// by the service, so switching webs never requires re-authenticating.
        /// </summary>
        private HttpClient CreateClient(string webUrl)
        {
            var client = new HttpClient(_handler!, disposeHandler: false)
            {
                BaseAddress = new Uri(webUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json;odata=verbose");
            client.DefaultRequestHeaders.Add("User-Agent", "SPAdminStudio/1.0 (Windows; .NET 8; On-Premises)");
            return client;
        }

        /// <summary>
        /// Re-targets the session at another web (subsite, parent site or any site on the same farm)
        /// reusing the current credentials. On failure the previous web stays active.
        /// </summary>
        public async Task SwitchWebAsync(string webUrl, CancellationToken cancellationToken = default)
        {
            if (_handler == null || _httpClient == null) throw new InvalidOperationException("Not connected.");

            var previousClient = _httpClient;
            var previousBaseUrl = _baseUrl;
            var previousDigest = _formDigestValue;
            var previousDigestExpiry = _digestExpiresAt;
            var previousSiteInfo = _currentSiteInfo;

            var newBaseUrl = webUrl.Trim().TrimEnd('/');
            var newClient = CreateClient(newBaseUrl);

            _httpClient = newClient;
            _baseUrl = newBaseUrl;
            _formDigestValue = string.Empty;
            ResetProtocolState();
            _digestExpiresAt = DateTime.MinValue;

            try
            {
                await RefreshFormDigestAsync(cancellationToken, force: true);
                _currentSiteInfo = await FetchSiteInfoAsync(cancellationToken);
                previousClient.Dispose();
            }
            catch
            {
                newClient.Dispose();
                _httpClient = previousClient;
                _baseUrl = previousBaseUrl;
                _formDigestValue = previousDigest;
                _digestExpiresAt = previousDigestExpiry;
                _currentSiteInfo = previousSiteInfo;
                throw;
            }
        }

        public void Disconnect()
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _handler?.Dispose();
            _handler = null;
            _cookieContainer = null;
            _currentSiteInfo = null;
            _formDigestValue = string.Empty;
            _digestExpiresAt = DateTime.MinValue;
            ResetProtocolState();
        }

        private async Task PerformFbaLoginAsync(string baseUrl, string username, string password, CancellationToken cancellationToken)
        {
            var soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <Login xmlns=""http://schemas.microsoft.com/sharepoint/soap/"">
      <userName>{WebUtility.HtmlEncode(username)}</userName>
      <password>{WebUtility.HtmlEncode(password)}</password>
    </Login>
  </soap:Body>
</soap:Envelope>";

            using var authClient = new HttpClient(_handler!, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/_vti_bin/Authentication.asmx")
            {
                Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("SOAPAction", "http://schemas.microsoft.com/sharepoint/soap/Login");

            var response = await authClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode || (!content.Contains("<ErrorCode>NoError</ErrorCode>") && !content.Contains("ResultText=\"Success\"")))
            {
                var errorCode = System.Text.RegularExpressions.Regex.Match(content, @"<ErrorCode>(.*?)</ErrorCode>");
                var reason = errorCode.Success ? errorCode.Groups[1].Value : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                throw new InvalidOperationException($"Forms Authentication failed: {reason}");
            }
        }

        /// <summary>
        /// Extracts a readable message from a SharePoint REST error body instead of surfacing the raw payload.
        /// </summary>
        private static string ExtractErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "(empty response)";

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err) || root.TryGetProperty("odata.error", out err))
                {
                    if (err.TryGetProperty("message", out var msg))
                    {
                        if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("value", out var val))
                            return val.GetString() ?? body;
                        if (msg.ValueKind == JsonValueKind.String)
                            return msg.GetString() ?? body;
                    }
                }
            }
            catch (JsonException) { }

            var xmlMatch = System.Text.RegularExpressions.Regex.Match(body, @"<m:message[^>]*>(.*?)</m:message>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (xmlMatch.Success) return WebUtility.HtmlDecode(xmlMatch.Groups[1].Value);

            // SOAP web services (lists.asmx, copy.asmx...)
            var soapFault = System.Text.RegularExpressions.Regex.Match(body, @"<faultstring[^>]*>(.*?)</faultstring>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (soapFault.Success) return WebUtility.HtmlDecode(soapFault.Groups[1].Value);

            return body.Length > 300 ? body.Substring(0, 300) + "..." : body;
        }

        private async Task RefreshFormDigestAsync(CancellationToken cancellationToken, bool force = false)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected to SharePoint.");

            if (!force && !string.IsNullOrEmpty(_formDigestValue) && DateTime.UtcNow < _digestExpiresAt)
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "_api/contextinfo")
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
            request.Headers.Accept.Clear();
            request.Headers.TryAddWithoutValidation("Accept", "application/json;odata=verbose");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            string newDigest = string.Empty;
            int timeoutSeconds = 1800;

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                if (root.TryGetProperty("d", out var dElem) &&
                    dElem.TryGetProperty("GetContextWebInformation", out var contextElem))
                {
                    if (contextElem.TryGetProperty("FormDigestValue", out var fdVal))
                        newDigest = fdVal.GetString() ?? string.Empty;
                    if (contextElem.TryGetProperty("FormDigestTimeoutSeconds", out var timeoutElem))
                        timeoutSeconds = timeoutElem.GetInt32();
                }
                else if (root.TryGetProperty("GetContextWebInformation", out var cElem))
                {
                    if (cElem.TryGetProperty("FormDigestValue", out var fdVal2))
                        newDigest = fdVal2.GetString() ?? string.Empty;
                    if (cElem.TryGetProperty("FormDigestTimeoutSeconds", out var timeoutElem2))
                        timeoutSeconds = timeoutElem2.GetInt32();
                }
                else if (root.TryGetProperty("FormDigestValue", out var directVal))
                {
                    newDigest = directVal.GetString() ?? string.Empty;
                }
            }
            catch { }

            // Regex fallback (handles XML and alternative JSON formats)
            if (string.IsNullOrEmpty(newDigest))
            {
                var match = System.Text.RegularExpressions.Regex.Match(responseBody, @"[""']FormDigestValue[""']\s*:\s*[""']([^""']+)[""']");
                if (match.Success)
                {
                    newDigest = match.Groups[1].Value;
                }
                else
                {
                    var xmlMatch = System.Text.RegularExpressions.Regex.Match(responseBody, @"<.*?:?FormDigestValue>(.*?)</.*?:?FormDigestValue>");
                    if (xmlMatch.Success)
                    {
                        newDigest = xmlMatch.Groups[1].Value;
                    }
                }
            }

            if (string.IsNullOrEmpty(newDigest))
            {
                throw new InvalidOperationException($"Could not obtain SharePoint security validation token (FormDigestValue). Response: {ExtractErrorMessage(responseBody)}");
            }

            _formDigestValue = newDigest;
            _digestExpiresAt = DateTime.UtcNow.AddSeconds(timeoutSeconds - 120);
        }

        private async Task<SharePointSiteInfo> FetchSiteInfoAsync(CancellationToken cancellationToken)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var info = new SharePointSiteInfo { Url = _baseUrl };

            try
            {
                var webResponse = await _httpClient.GetStringAsync("_api/web?$select=Title,Url,ServerRelativeUrl", cancellationToken);
                using var webDoc = JsonDocument.Parse(webResponse);
                if (webDoc.RootElement.TryGetProperty("d", out var d))
                {
                    info.Title = d.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                    info.ServerRelativeUrl = d.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() ?? "" : "";
                }
            }
            catch (Exception ex)
            {
                info.Title = "SharePoint Site";
                System.Diagnostics.Debug.WriteLine($"Error fetching web info: {ex.Message}");
            }

            // Master Page Gallery and Style Library only exist on the site collection root web,
            // so remember where it is even when the profile points at a subsite.
            try
            {
                var siteResponse = await _httpClient.GetStringAsync("_api/site?$select=ServerRelativeUrl", cancellationToken);
                using var siteDoc = JsonDocument.Parse(siteResponse);
                if (siteDoc.RootElement.TryGetProperty("d", out var sd) && sd.TryGetProperty("ServerRelativeUrl", out var ssru))
                {
                    info.SiteCollectionServerRelativeUrl = ssru.GetString() ?? "";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching site collection info: {ex.Message}");
            }

            if (string.IsNullOrEmpty(info.SiteCollectionServerRelativeUrl))
            {
                info.SiteCollectionServerRelativeUrl = info.ServerRelativeUrl;
            }

            try
            {
                var userResponse = await _httpClient.GetStringAsync("_api/web/currentuser?$select=Title,LoginName,IsSiteAdmin", cancellationToken);
                using var userDoc = JsonDocument.Parse(userResponse);
                if (userDoc.RootElement.TryGetProperty("d", out var ud))
                {
                    info.CurrentUserTitle = ud.TryGetProperty("Title", out var ut) ? ut.GetString() ?? "" : "";
                    info.CurrentUserLogin = ud.TryGetProperty("LoginName", out var ul) ? ul.GetString() ?? "" : "";
                    info.IsSiteAdmin = ud.TryGetProperty("IsSiteAdmin", out var ia) && ia.GetBoolean();
                }
            }
            catch { }

            return info;
        }

        public async Task<string> GetStyleLibraryServerRelativeUrlAsync(CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            try
            {
                var response = await _httpClient.GetStringAsync("_api/site/rootweb/lists?$select=Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder", cancellationToken);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
                {
                    // Pass 1: Exact matches for Style Library title or rootUrl
                    foreach (var list in results.EnumerateArray())
                    {
                        var title = list.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                        string rootUrl = "";
                        if (list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru))
                        {
                            rootUrl = sru.GetString() ?? "";
                        }

                        if (title.Equals("Style Library", StringComparison.OrdinalIgnoreCase) ||
                            title.Equals("مكتبة الأنماط", StringComparison.OrdinalIgnoreCase) ||
                            rootUrl.EndsWith("/Style Library", StringComparison.OrdinalIgnoreCase) ||
                            rootUrl.EndsWith("/Style%20Library", StringComparison.OrdinalIgnoreCase))
                        {
                            return rootUrl;
                        }
                    }

                    // Pass 2: Partial matches
                    foreach (var list in results.EnumerateArray())
                    {
                        var title = list.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                        string rootUrl = "";
                        if (list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru))
                        {
                            rootUrl = sru.GetString() ?? "";
                        }

                        if (rootUrl.Contains("Style Library", StringComparison.OrdinalIgnoreCase) ||
                            rootUrl.Contains("Style%20Library", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Style Library", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("مكتبة الأنماط", StringComparison.OrdinalIgnoreCase))
                        {
                            return rootUrl;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Style Library discovery error: {ex.Message}");
            }

            var webRel = SiteCollectionRootRelative;
            return $"{webRel}/Style Library";
        }

        public async Task<string> GetMasterPageGalleryServerRelativeUrlAsync(CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            try
            {
                var response = await _httpClient.GetStringAsync("_api/site/rootweb/lists?$select=Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder", cancellationToken);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
                {
                    foreach (var list in results.EnumerateArray())
                    {
                        var baseTemplate = list.TryGetProperty("BaseTemplate", out var bt) ? bt.GetInt32() : 0;
                        var title = list.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";

                        string rootUrl = "";
                        if (list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru))
                        {
                            rootUrl = sru.GetString() ?? "";
                        }

                        if (baseTemplate == 116 ||
                            rootUrl.Contains("_catalogs/masterpage", StringComparison.OrdinalIgnoreCase) ||
                            title.Equals("Master Page Gallery", StringComparison.OrdinalIgnoreCase) ||
                            title.Equals("معرض الصفحات الرئيسية", StringComparison.OrdinalIgnoreCase))
                        {
                            return rootUrl;
                        }
                    }
                }
            }
            catch { }

            var webRel = SiteCollectionRootRelative;
            return $"{webRel}/_catalogs/masterpage";
        }

        public async Task<List<SharePointItem>> GetMasterPageGalleryItemsAsync(CancellationToken cancellationToken = default)
        {
            if (_httpClient == null || _currentSiteInfo == null) throw new InvalidOperationException("Not connected.");

            var serverRelative = await GetMasterPageGalleryServerRelativeUrlAsync(cancellationToken);
            var safePath = SafeUrl(serverRelative);
            var url = $"_api/web/GetFolderByServerRelativeUrl('{safePath}')/Files?$expand=ModifiedBy,CheckedOutByUser&$select=Name,ServerRelativeUrl,Length,CheckOutType,CheckedOutByUser/Title,CheckedOutByUser/LoginName,ModifiedBy/Title,TimeLastModified,UIVersionLabel";

            var response = await _httpClient.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(response);

            var items = new List<SharePointItem>();
            if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
            {
                foreach (var el in results.EnumerateArray())
                {
                    var item = ParseFileItem(el);
                    item.ContentType = "Master Page / Layout";
                    items.Add(item);
                }
            }

            // Try to query ListItems to attach moderation info
            try
            {
                // Resolve the list by URL rather than by (localized) title
                var itemQueryUrl = $"_api/site/rootweb/GetList('{safePath}')/items?$select=Id,FileRef,OData__ModerationStatus,OData__ModerationComments,ContentType/Name&$expand=ContentType&$top=5000";
                var itemsResponse = await _httpClient.GetStringAsync(itemQueryUrl, cancellationToken);
                using var itemsDoc = JsonDocument.Parse(itemsResponse);
                if (itemsDoc.RootElement.TryGetProperty("d", out var idElem) && idElem.TryGetProperty("results", out var itemResults))
                {
                    var map = new Dictionary<string, (int status, string comments, string ctype)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var iel in itemResults.EnumerateArray())
                    {
                        var fileRef = iel.TryGetProperty("FileRef", out var fr) ? fr.GetString() ?? "" : "";
                        var modStatus = iel.TryGetProperty("OData__ModerationStatus", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 0;
                        var modComments = iel.TryGetProperty("OData__ModerationComments", out var mc) ? mc.GetString() ?? "" : "";
                        var ctName = "";
                        if (iel.TryGetProperty("ContentType", out var ct) && ct.TryGetProperty("Name", out var ctn))
                        {
                            ctName = ctn.GetString() ?? "";
                        }
                        if (!string.IsNullOrEmpty(fileRef))
                        {
                            map[fileRef] = (modStatus, modComments, ctName);
                        }
                    }

                    foreach (var itm in items)
                    {
                        if (map.TryGetValue(itm.ServerRelativeUrl, out var meta))
                        {
                            itm.ModerationStatusCode = meta.status;
                            itm.ModerationComments = meta.comments;
                            if (!string.IsNullOrEmpty(meta.ctype)) itm.ContentType = meta.ctype;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to augment moderation metadata: {ex.Message}");
            }

            return items.OrderBy(x => x.Name).ToList();
        }

        public async Task<SharePointFolder> GetFolderHierarchyAsync(string rootRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var folderName = Path.GetFileName(rootRelativeUrl.TrimEnd('/'));
            if (string.IsNullOrEmpty(folderName)) folderName = "Style Library";

            var rootFolder = new SharePointFolder
            {
                Name = folderName,
                ServerRelativeUrl = rootRelativeUrl,
                IsExpanded = true
            };

            await LoadSubFoldersRecursiveAsync(rootFolder, 0, 3, cancellationToken);
            return rootFolder;
        }

        private async Task<string?> TryGetFolderJsonResponseAsync(string serverRelativeUrl, string queryPath, CancellationToken cancellationToken)
        {
            if (_httpClient == null) return null;

            var cleanPath = serverRelativeUrl.Trim();
            if (!cleanPath.StartsWith("/")) cleanPath = "/" + cleanPath;

            var safePath = SafeUrl(cleanPath);
            var unescapedPath = cleanPath.Replace("'", "''");

            var candidatePaths = new List<string> { safePath };
            if (!string.Equals(safePath, unescapedPath, StringComparison.OrdinalIgnoreCase))
            {
                candidatePaths.Add(unescapedPath);
            }

            foreach (var p in candidatePaths)
            {
                try
                {
                    var url = $"_api/web/GetFolderByServerRelativeUrl('{p}')/{queryPath}";
                    var response = await _httpClient.GetAsync(url, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Returns only the immediate (non-system) subfolders of a folder. One request, no recursion.
        /// </summary>
        public async Task<List<SharePointFolder>> GetSubFoldersAsync(string folderServerRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var folders = new List<SharePointFolder>();
            var json = await TryGetFolderJsonResponseAsync(folderServerRelativeUrl, "Folders?$select=Name,ServerRelativeUrl", cancellationToken);
            if (string.IsNullOrEmpty(json)) return folders;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
            {
                foreach (var f in results.EnumerateArray())
                {
                    var name = f.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name) || name.StartsWith("_vti_") || name.StartsWith("."))
                        continue;

                    folders.Add(new SharePointFolder
                    {
                        Name = name,
                        ServerRelativeUrl = f.TryGetProperty("ServerRelativeUrl", out var u) ? u.GetString() ?? "" : ""
                    });
                }
            }

            return folders;
        }

        private async Task LoadSubFoldersRecursiveAsync(SharePointFolder parent, int currentDepth, int maxDepth, CancellationToken cancellationToken)
        {
            if (currentDepth >= maxDepth || _httpClient == null) return;

            try
            {
                foreach (var child in await GetSubFoldersAsync(parent.ServerRelativeUrl, cancellationToken))
                {
                    parent.SubFolders.Add(child);
                    await LoadSubFoldersRecursiveAsync(child, currentDepth + 1, maxDepth, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching subfolders for {parent.ServerRelativeUrl}: {ex.Message}");
            }
        }

        public async Task<List<SharePointItem>> GetFolderContentsAsync(string folderServerRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var items = new List<SharePointItem>();

            // 1. Fetch SubFolders
            try
            {
                var folderJson = await TryGetFolderJsonResponseAsync(folderServerRelativeUrl, "Folders?$select=Name,ServerRelativeUrl,TimeLastModified,ItemCount", cancellationToken);
                if (!string.IsNullOrEmpty(folderJson))
                {
                    using var doc = JsonDocument.Parse(folderJson);
                    if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
                    {
                        foreach (var f in results.EnumerateArray())
                        {
                            var name = f.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                            if (name.StartsWith("_vti_") || name.StartsWith("."))
                                continue;

                            var sru = f.TryGetProperty("ServerRelativeUrl", out var u) ? u.GetString() ?? "" : "";
                            var folderItem = new SharePointItem
                            {
                                Name = name,
                                ServerRelativeUrl = sru,
                                IsFolder = true,
                                ContentType = "Folder"
                            };

                            if (f.TryGetProperty("TimeLastModified", out var tlm) && DateTime.TryParse(tlm.GetString(), out var dt))
                            {
                                folderItem.TimeLastModified = dt;
                            }

                            items.Add(folderItem);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching subfolders: {ex.Message}");
            }

            // 2. Fetch Files
            try
            {
                var files = await GetFolderFilesAsync(folderServerRelativeUrl, cancellationToken);
                items.AddRange(files);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching files: {ex.Message}");
            }

            // Return Folders first (alphabetically), then Files (alphabetically)
            return items.OrderByDescending(x => x.IsFolder)
                        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
        }

        public async Task<List<SharePointItem>> GetFolderFilesAsync(string folderServerRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var json = await TryGetFolderJsonResponseAsync(folderServerRelativeUrl, "Files?$expand=ModifiedBy,CheckedOutByUser&$select=Name,ServerRelativeUrl,Length,CheckOutType,CheckedOutByUser/Title,ModifiedBy/Title,TimeLastModified,UIVersionLabel", cancellationToken);

            var items = new List<SharePointItem>();
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
                {
                    foreach (var el in results.EnumerateArray())
                    {
                        items.Add(ParseFileItem(el));
                    }
                }
            }

            return items.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Fetches the current server state (checkout, version, etc.) of a single file.
        /// </summary>
        public async Task<SharePointItem> GetFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var safePath = SafeUrl(serverRelativeUrl);
            var url = $"_api/web/GetFileByServerRelativeUrl('{safePath}')?$expand=ModifiedBy,CheckedOutByUser&$select=Name,ServerRelativeUrl,Length,CheckOutType,CheckedOutByUser/Title,ModifiedBy/Title,TimeLastModified,UIVersionLabel";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Could not read file state for '{serverRelativeUrl}': {ExtractErrorMessage(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement.TryGetProperty("d", out var d) ? d : doc.RootElement;
            return ParseFileItem(el);
        }

        private SharePointItem ParseFileItem(JsonElement el)
        {
            var item = new SharePointItem
            {
                Name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                ServerRelativeUrl = el.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() ?? "" : "",
                Length = el.TryGetProperty("Length", out var l) && l.ValueKind == JsonValueKind.String && long.TryParse(l.GetString(), out var len) ? len :
                         (el.TryGetProperty("Length", out var lnum) && lnum.ValueKind == JsonValueKind.Number ? lnum.GetInt64() : 0),
                CheckOutType = el.TryGetProperty("CheckOutType", out var cot) ? cot.GetInt32() : 2,
                Version = el.TryGetProperty("UIVersionLabel", out var v) ? v.GetString() ?? "1.0" : "1.0",
                IsFolder = false
            };

            if (el.TryGetProperty("CheckedOutByUser", out var cou) && cou.ValueKind == JsonValueKind.Object && cou.TryGetProperty("Title", out var cout))
            {
                item.CheckedOutByUser = cout.GetString() ?? "";
            }

            if (el.TryGetProperty("ModifiedBy", out var mb) && mb.ValueKind == JsonValueKind.Object && mb.TryGetProperty("Title", out var mbt))
            {
                item.ModifiedBy = mbt.GetString() ?? "";
            }

            if (el.TryGetProperty("TimeLastModified", out var tlm) && DateTime.TryParse(tlm.GetString(), out var dt))
            {
                item.TimeLastModified = dt;
            }

            return item;
        }

        public async Task<byte[]> DownloadFileAsync(string serverRelativeUrl, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var cleanPath = serverRelativeUrl.Trim();
            if (!cleanPath.StartsWith("/")) cleanPath = "/" + cleanPath;

            var safePath = SafeUrl(cleanPath);
            var unescapedPath = cleanPath.Replace("'", "''");

            // Candidate URLs to try in order:
            // 1. REST $value with escaped path and Accept: */*
            // 2. REST $value with unescaped path (if different)
            // 3. REST OpenBinaryStream()
            // 4. Direct IIS WebDAV GET with Translate: F
            var candidateUrls = new List<string>
            {
                $"_api/web/GetFileByServerRelativeUrl('{safePath}')/$value"
            };

            if (!string.Equals(safePath, unescapedPath, StringComparison.OrdinalIgnoreCase))
            {
                candidateUrls.Add($"_api/web/GetFileByServerRelativeUrl('{unescapedPath}')/$value");
            }
            candidateUrls.Add($"_api/web/GetFileByServerRelativeUrl('{safePath}')/OpenBinaryStream");

            // Server-relative path must be resolved against the host root, not the site's BaseAddress
            // (otherwise a site at /sites/x would request /sites/x/sites/x/...)
            var directUrl = new Uri(new Uri(_baseUrl), cleanPath).AbsoluteUri;
            candidateUrls.Add(directUrl);

            HttpResponseMessage? successfulResponse = null;
            string lastError = string.Empty;

            foreach (var url in candidateUrls)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                    request.Headers.TryAddWithoutValidation("Translate", "F"); // WebDAV raw content header

                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        successfulResponse = response;
                        break;
                    }
                    else
                    {
                        lastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {url}";
                        response.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"{url}: {ex.Message}";
                }
            }

            if (successfulResponse == null)
            {
                throw new InvalidOperationException($"Failed to download '{Path.GetFileName(serverRelativeUrl)}' from SharePoint. ({lastError})");
            }

            using (successfulResponse)
            {
                var totalBytes = successfulResponse.Content.Headers.ContentLength ?? -1L;
                using var stream = await successfulResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var ms = new MemoryStream();

                var buffer = new byte[81920];
                int read;
                long totalRead = 0;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await ms.WriteAsync(buffer, 0, read, cancellationToken);
                    totalRead += read;

                    progress?.Report(new TransferProgress
                    {
                        CurrentItemName = Path.GetFileName(serverRelativeUrl),
                        BytesTransferred = totalRead,
                        TotalBytes = totalBytes,
                        StatusMessage = $"Downloading {Path.GetFileName(serverRelativeUrl)} ({totalRead / 1024} KB)..."
                    });
                }

                return ms.ToArray();
            }
        }

        public async Task UploadFileAsync(string folderServerRelativeUrl, string fileName, byte[] content, bool overwrite, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var safeFolder = SafeUrl(folderServerRelativeUrl);
            var safeFileName = Uri.EscapeDataString(fileName).Replace("'", "''");
            var url = $"_api/web/GetFolderByServerRelativeUrl('{safeFolder}')/Files/Add(url='{safeFileName}',overwrite={(overwrite ? "true" : "false")})";
            var fileUrl = folderServerRelativeUrl.TrimEnd('/') + "/" + fileName;

            progress?.Report(new TransferProgress
            {
                CurrentItemName = fileName,
                StatusMessage = $"Uploading {fileName} ({content.Length / 1024} KB)...",
                IsIndeterminate = true
            });

            async Task<(bool, string, Exception?)> Rest()
            {
                var (ok, body, status, reason) = await SendWriteAsync(url, () =>
                {
                    var c = new ByteArrayContent(content);
                    c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return c;
                }, cancellationToken);
                if (ok) return (true, body, null);

                var failure = await CreateWriteFailureAsync($"Upload of '{fileName}'", folderServerRelativeUrl, status, reason, body, cancellationToken);
                return (false, body, AddUploadHint(failure, body, fileUrl));
            }

            await WithDesignerFallbackAsync($"Upload of '{fileName}'", Rest,
                () => DesignerPutDocumentAsync(folderServerRelativeUrl, fileName, content, cancellationToken));
        }

        /// <summary>
        /// POSTs a REST write with the form digest (refreshed once if expired). Never throws for HTTP errors:
        /// returns the outcome so callers can decide between an error and SharePoint Designer's protocols.
        /// </summary>
        private async Task<(bool ok, string body, HttpStatusCode status, string? reason)> SendWriteAsync(string url, Func<HttpContent> contentFactory,
            CancellationToken cancellationToken, string? xHttpMethod = null)
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
                if (response.IsSuccessStatusCode) return (true, string.Empty, response.StatusCode, response.ReasonPhrase);

                lastError = await response.Content.ReadAsStringAsync(cancellationToken);
                lastStatus = response.StatusCode;
                lastReason = response.ReasonPhrase;
                if (IsExpiredDigest(response.StatusCode, lastError) ||
                    (response.StatusCode == HttpStatusCode.Forbidden && string.IsNullOrEmpty(_formDigestValue)))
                {
                    await RefreshFormDigestAsync(cancellationToken, force: true);
                    continue;
                }

                break;
            }

            return (false, lastError, lastStatus, lastReason);
        }

        private static bool IsExpiredDigest(HttpStatusCode status, string body) =>
            status == HttpStatusCode.Forbidden && (body.Contains("0x8102006d") || body.Contains("security validation"));

        /// <summary>SharePoint refuses to overwrite a file that is checked in, in a library that requires check-out.</summary>
        private static InvalidOperationException AddUploadHint(InvalidOperationException ex, string body, string fileUrl)
        {
            if (body.Contains("-2130575306") || body.Contains("must be checked out", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("not checked out", StringComparison.OrdinalIgnoreCase))
            {
                return new InvalidOperationException(ex.Message + $"\nThe library requires check-out: check out '{fileUrl}' first, then save again.");
            }
            return ex;
        }

        public async Task CreateFolderAsync(string parentServerRelativeUrl, string newFolderName, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            // Folders/Add on the parent folder: no hand-built JSON body, and works for folders in the root web
            // even when connected to a subsite.
            var safeParent = SafeUrl(parentServerRelativeUrl.TrimEnd('/'));
            var safeName = Uri.EscapeDataString(newFolderName).Replace("'", "''");
            var url = $"_api/web/GetFolderByServerRelativeUrl('{safeParent}')/Folders/Add('{safeName}')";

            async Task<(bool, string, Exception?)> Rest()
            {
                var (ok, body, status, reason) = await SendWriteAsync(url, () => new ByteArrayContent(Array.Empty<byte>()), cancellationToken);
                return ok
                    ? (true, body, null)
                    : (false, body, await CreateWriteFailureAsync($"Creating folder '{newFolderName}'", parentServerRelativeUrl, status, reason, body, cancellationToken));
            }

            await WithDesignerFallbackAsync($"Creating folder '{newFolderName}'", Rest,
                () => DesignerCreateFolderAsync(parentServerRelativeUrl, newFolderName, cancellationToken));
        }

        public async Task CheckOutAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            await PostFileOperationAsync(serverRelativeUrl, "CheckOut()", cancellationToken,
                () => DesignerCheckOutAsync(serverRelativeUrl, cancellationToken));
        }

        public async Task CheckInAsync(string serverRelativeUrl, int checkInType, string comment, CancellationToken cancellationToken = default)
        {
            var encodedComment = Uri.EscapeDataString(comment ?? "").Replace("'", "''");
            var operation = $"CheckIn(comment='{encodedComment}',checkInType={checkInType})";
            await PostFileOperationAsync(serverRelativeUrl, operation, cancellationToken,
                () => DesignerCheckInAsync(serverRelativeUrl, comment ?? string.Empty, checkInType, cancellationToken));
        }

        public async Task PublishAsync(string serverRelativeUrl, string comment, CancellationToken cancellationToken = default)
        {
            var encodedComment = Uri.EscapeDataString(comment ?? "").Replace("'", "''");
            var operation = $"Publish(comment='{encodedComment}')";

            // Designer channels have no "publish": check out (if needed) and check in as a major version
            await PostFileOperationAsync(serverRelativeUrl, operation, cancellationToken, async () =>
            {
                var state = await GetFileAsync(serverRelativeUrl, cancellationToken);
                if (state.CheckOutType == 2)
                {
                    await DesignerCheckOutAsync(serverRelativeUrl, cancellationToken);
                }
                await DesignerCheckInAsync(serverRelativeUrl, comment ?? string.Empty, 1, cancellationToken);
            });
        }

        public async Task UndoCheckOutAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            await PostFileOperationAsync(serverRelativeUrl, "UndoCheckOut()", cancellationToken,
                () => DesignerUndoCheckOutAsync(serverRelativeUrl, cancellationToken));
        }

        public async Task ApproveAsync(string serverRelativeUrl, string comment, CancellationToken cancellationToken = default)
        {
            var encodedComment = Uri.EscapeDataString(comment ?? "").Replace("'", "''");
            var operation = $"Approve(comment='{encodedComment}')";
            await PostFileOperationAsync(serverRelativeUrl, operation, cancellationToken,
                () => DesignerApproveAsync(serverRelativeUrl, comment ?? string.Empty, approve: true, cancellationToken));
        }

        public async Task DenyAsync(string serverRelativeUrl, string comment, CancellationToken cancellationToken = default)
        {
            var encodedComment = Uri.EscapeDataString(comment ?? "").Replace("'", "''");
            var operation = $"Deny(comment='{encodedComment}')";
            await PostFileOperationAsync(serverRelativeUrl, operation, cancellationToken,
                () => DesignerApproveAsync(serverRelativeUrl, comment ?? string.Empty, approve: false, cancellationToken));
        }

        private async Task PostFileOperationAsync(string serverRelativeUrl, string operationEndpoint, CancellationToken cancellationToken,
            Func<Task> designerFallback)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var safePath = SafeUrl(serverRelativeUrl);
            var url = $"_api/web/GetFileByServerRelativeUrl('{safePath}')/{operationEndpoint}";
            var action = $"{operationEndpoint.Split('(')[0]} of '{Path.GetFileName(serverRelativeUrl)}'";

            async Task<(bool, string, Exception?)> Rest()
            {
                var (ok, body, status, reason) = await SendWriteAsync(url, () => new ByteArrayContent(Array.Empty<byte>()), cancellationToken);
                return ok
                    ? (true, body, null)
                    : (false, body, await CreateWriteFailureAsync(action, serverRelativeUrl, status, reason, body, cancellationToken));
            }

            await WithDesignerFallbackAsync(action, Rest, designerFallback);
        }
    }
}
