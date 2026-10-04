using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SPAdminStudio.Services
{
    /// <summary>
    /// SharePoint Designer's own protocols, used when a reverse proxy lets REST/CSOM reads through but
    /// blocks REST/CSOM writes (seen on the public Production address): FrontPage RPC (author.dll) for
    /// check-out, check-in, save and folders, and the Lists web service (lists.asmx) for major check-in,
    /// approval and properties. These are the same requests SharePoint Designer sends.
    /// </summary>
    public partial class SharePointRestService
    {
        private const string FrontPageVersion = "15.0.0.0000";

        /// <summary>Set once a REST write is answered by a web page instead of the API; reset on connect.</summary>
        private bool _restWritesBlocked;
        private readonly Dictionary<string, string> _containingWebCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Messages worth showing in the Activity Log (protocol switches).</summary>
        public event Action<string>? Notice;

        public bool UsingDesignerProtocols => _restWritesBlocked;

        private void ResetProtocolState()
        {
            _restWritesBlocked = false;
            _containingWebCache.Clear();
        }

        private string HostOrigin => new Uri(_baseUrl).GetLeftPart(UriPartial.Authority);

        /// <summary>A web page (not JSON/XML, not a FrontPage RPC packet) means the API never saw the request.</summary>
        private static bool IsNonApiResponse(string body)
        {
            var trimmed = body.TrimStart();
            var isHtml = trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
            return isHtml && !body.Contains("vermeer RPC packet", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs the REST write; if the API is unreachable for writes (answered with a web page), switches this
        /// connection to SharePoint Designer's protocols and runs <paramref name="designerFallback"/> instead.
        /// </summary>
        private async Task WithDesignerFallbackAsync(string action, Func<Task<(bool ok, string body, Exception? failure)>> restAttempt,
            Func<Task> designerFallback)
        {
            if (!_restWritesBlocked)
            {
                var (ok, body, failure) = await restAttempt();
                if (ok) return;
                if (!IsNonApiResponse(body)) throw failure!;

                _restWritesBlocked = true;
                Notice?.Invoke($"REST API writes are blocked on {_baseUrl} (answered by a web page). " +
                               "Switching this connection to SharePoint Designer's protocols (FrontPage RPC / Lists web service).");
            }

            try
            {
                await designerFallback();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{action} failed. The REST API is blocked on this address, and SharePoint Designer's protocol also failed: {ex.Message}", ex);
            }
        }

        #region Web resolution

        /// <summary>
        /// FrontPage RPC addresses documents relative to their own web. Requesting {path}/_api/web makes
        /// SharePoint resolve the deepest web containing that path (a read, so it is not blocked).
        /// </summary>
        public async Task<string> GetContainingWebUrlAsync(string serverRelativePath, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var candidate = ParentPath(serverRelativePath);
            var visited = new List<string>();
            while (true)
            {
                if (_containingWebCache.TryGetValue(candidate, out var cached)) return Remember(visited, cached);
                visited.Add(candidate);

                var url = $"{HostOrigin}{(candidate == "/" ? "" : EscapePath(candidate))}/_api/web?$select=ServerRelativeUrl";
                try
                {
                    using var response = await _httpClient.GetAsync(url, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("ServerRelativeUrl", out var sru))
                        {
                            return Remember(visited, sru.GetString() ?? "/");
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException)
                {
                    System.Diagnostics.Debug.WriteLine($"Web lookup for {candidate} failed: {ex.Message}");
                }

                if (candidate == "/") return Remember(visited, SiteCollectionRootRelative.Length == 0 ? "/" : SiteCollectionRootRelative);
                candidate = ParentPath(candidate);
            }
        }

        private string Remember(IEnumerable<string> paths, string web)
        {
            foreach (var p in paths) _containingWebCache[p] = web;
            return web;
        }

        private static string ParentPath(string path)
        {
            var trimmed = path.TrimEnd('/');
            var index = trimmed.LastIndexOf('/');
            return index <= 0 ? "/" : trimmed.Substring(0, index);
        }

        private static string EscapePath(string serverRelativePath) =>
            string.Join("/", serverRelativePath.Split('/').Select(Uri.EscapeDataString));

        private static string WebRelative(string serverRelativeUrl, string webUrl)
        {
            var web = webUrl.TrimEnd('/');
            return (serverRelativeUrl.StartsWith(web + "/", StringComparison.OrdinalIgnoreCase) ? serverRelativeUrl.Substring(web.Length) : serverRelativeUrl)
                .TrimStart('/');
        }

        #endregion

        #region FrontPage RPC (author.dll)

        /// <summary>Backslash-escapes the characters FrontPage RPC uses as delimiters inside [...] values.</summary>
        private static string FrontPageEscape(string value) => Regex.Replace(value, @"([\\;\[\]=])", @"\$1");

        private async Task<string> FrontPageRpcAsync(string webUrl, string method, IEnumerable<(string Key, string Value)> parameters,
            byte[]? document, string action, CancellationToken cancellationToken)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var web = webUrl.TrimEnd('/');
            var header = new StringBuilder()
                .Append("method=").Append(Uri.EscapeDataString($"{method}:{FrontPageVersion}"))
                .Append("&service_name=").Append(Uri.EscapeDataString(web.Length == 0 ? "/" : web));
            foreach (var (key, value) in parameters)
            {
                header.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
            }

            var headerBytes = Encoding.UTF8.GetBytes(header.ToString());
            byte[] payload;
            if (document == null)
            {
                payload = headerBytes;
            }
            else
            {
                // put document: "<parameters>\n<raw file bytes>"
                payload = new byte[headerBytes.Length + 1 + document.Length];
                headerBytes.CopyTo(payload, 0);
                payload[headerBytes.Length] = (byte)'\n';
                document.CopyTo(payload, headerBytes.Length + 1);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{HostOrigin}{EscapePath(web)}/_vti_bin/_vti_aut/author.dll")
            {
                Content = new ByteArrayContent(payload)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Headers.TryAddWithoutValidation("X-Vermeer-Content-Type", "application/x-www-form-urlencoded");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!body.Contains("vermeer RPC packet", StringComparison.OrdinalIgnoreCase))
            {
                throw await CreateWriteFailureAsync($"{action} (FrontPage RPC)", null, response.StatusCode, response.ReasonPhrase, body, cancellationToken);
            }

            // Error packets carry "<li>status=<non-zero>" and "<li>msg=..."
            var status = Regex.Match(body, @"<li>status=(\d+)");
            if (status.Success && status.Groups[1].Value != "0")
            {
                var msg = Regex.Match(body, @"<li>msg=([^\n<]*)");
                var text = msg.Success ? WebUtility.HtmlDecode(msg.Groups[1].Value).Trim() : $"status {status.Groups[1].Value}";
                throw new InvalidOperationException($"{action}: {text} [FrontPage RPC status {status.Groups[1].Value}]");
            }

            return body;
        }

        private async Task DesignerCheckOutAsync(string fileUrl, CancellationToken cancellationToken)
        {
            var web = await GetContainingWebUrlAsync(fileUrl, cancellationToken);
            await FrontPageRpcAsync(web, "checkout document",
                new[] { ("document_name", WebRelative(fileUrl, web)), ("force", "0"), ("timeout", "0") },
                null, $"CheckOut of '{Path.GetFileName(fileUrl)}'", cancellationToken);
        }

        private async Task DesignerUndoCheckOutAsync(string fileUrl, CancellationToken cancellationToken)
        {
            var web = await GetContainingWebUrlAsync(fileUrl, cancellationToken);
            await FrontPageRpcAsync(web, "uncheckout document",
                new[] { ("document_name", WebRelative(fileUrl, web)), ("force", "false"), ("rlsshortterm", "false") },
                null, $"UndoCheckOut of '{Path.GetFileName(fileUrl)}'", cancellationToken);
        }

        private async Task FrontPageCheckInAsync(string fileUrl, string comment, CancellationToken cancellationToken)
        {
            var web = await GetContainingWebUrlAsync(fileUrl, cancellationToken);
            await FrontPageRpcAsync(web, "checkin document",
                new[] { ("document_name", WebRelative(fileUrl, web)), ("comment", comment ?? string.Empty), ("keep_checked_out", "false") },
                null, $"CheckIn of '{Path.GetFileName(fileUrl)}'", cancellationToken);
        }

        private async Task DesignerPutDocumentAsync(string folderUrl, string fileName, byte[] content, CancellationToken cancellationToken)
        {
            var fileUrl = folderUrl.TrimEnd('/') + "/" + fileName;
            var web = await GetContainingWebUrlAsync(fileUrl, cancellationToken);
            var document = $"[document_name={FrontPageEscape(WebRelative(fileUrl, web))};meta_info=[]]";

            // keep_checked_out=true: like REST Files/Add, saving never changes the check-out state
            await FrontPageRpcAsync(web, "put document",
                new[] { ("document", document), ("put_option", "overwrite,createdir"), ("comment", string.Empty), ("keep_checked_out", "true") },
                content, $"Upload of '{fileName}'", cancellationToken);
        }

        private async Task DesignerCreateFolderAsync(string parentUrl, string folderName, CancellationToken cancellationToken)
        {
            var folderUrl = parentUrl.TrimEnd('/') + "/" + folderName;
            var web = await GetContainingWebUrlAsync(folderUrl + "/x", cancellationToken);
            await FrontPageRpcAsync(web, "create url-directories",
                new[] { ("urldirs", $"[[url={FrontPageEscape(WebRelative(folderUrl, web))};meta_info=[]]]") },
                null, $"Creating folder '{folderName}'", cancellationToken);
        }

        #endregion

        #region Lists web service (lists.asmx)

        private async Task<string> ListsSoapAsync(string webUrl, string operation, string innerXml, string action, CancellationToken cancellationToken)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var envelope =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<soap:Envelope xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                $"<soap:Body><{operation} xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\">{innerXml}</{operation}></soap:Body></soap:Envelope>";

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{HostOrigin}{EscapePath(webUrl.TrimEnd('/'))}/_vti_bin/lists.asmx")
            {
                Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://schemas.microsoft.com/sharepoint/soap/{operation}\"");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!body.Contains("soap:Envelope", StringComparison.OrdinalIgnoreCase))
            {
                throw await CreateWriteFailureAsync($"{action} (Lists web service)", null, response.StatusCode, response.ReasonPhrase, body, cancellationToken);
            }

            var fault = Regex.Match(body, @"<errorstring[^>]*>(.*?)</errorstring>", RegexOptions.Singleline);
            if (!fault.Success) fault = Regex.Match(body, @"<faultstring[^>]*>(.*?)</faultstring>", RegexOptions.Singleline);
            if (fault.Success && body.Contains("Fault>", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{action}: {WebUtility.HtmlDecode(fault.Groups[1].Value).Trim()}");
            }

            return body;
        }

        private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

        private async Task ListsCheckInAsync(string fileUrl, string comment, int checkInType, CancellationToken cancellationToken)
        {
            var web = await GetContainingWebUrlAsync(fileUrl, cancellationToken);
            var body = await ListsSoapAsync(web, "CheckInFile",
                $"<pageUrl>{Xml(HostOrigin + EscapePath(fileUrl))}</pageUrl><comment>{Xml(comment)}</comment><CheckinType>{checkInType}</CheckinType>",
                $"CheckIn of '{Path.GetFileName(fileUrl)}'", cancellationToken);

            if (!body.Contains("<CheckInFileResult>true</CheckInFileResult>", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"CheckIn of '{Path.GetFileName(fileUrl)}' was refused by SharePoint (Lists web service returned false).");
            }
        }

        /// <summary>Check-in with a version type: Lists web service first; FrontPage RPC (default version type) if that is blocked too.</summary>
        private async Task DesignerCheckInAsync(string fileUrl, string comment, int checkInType, CancellationToken cancellationToken)
        {
            try
            {
                await ListsCheckInAsync(fileUrl, comment, checkInType, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("(Lists web service) failed"))
            {
                Notice?.Invoke($"Lists web service unavailable ({ex.Message.Split('\n')[0]}); checking in '{Path.GetFileName(fileUrl)}' with FrontPage RPC — the library's default version type applies.");
                await FrontPageCheckInAsync(fileUrl, comment, cancellationToken);
            }
        }

        private async Task ListsUpdateItemAsync(string fileUrl, string listId, string command, IEnumerable<(string Field, string Value)> fields,
            string action, CancellationToken cancellationToken)
        {
            var web = await GetContainingWebUrlAsync(fileUrl, cancellationToken);
            var fieldXml = string.Concat(fields.Select(f => $"<Field Name=\"{Xml(f.Field)}\">{Xml(f.Value)}</Field>"));
            var body = await ListsSoapAsync(web, "UpdateListItems",
                $"<listName>{{{listId}}}</listName><updates><Batch OnError=\"Continue\"><Method ID=\"1\" Cmd=\"{command}\">{fieldXml}</Method></Batch></updates>",
                action, cancellationToken);

            var code = Regex.Match(body, @"<ErrorCode>(.*?)</ErrorCode>", RegexOptions.Singleline);
            if (code.Success && code.Groups[1].Value.Trim() != "0x00000000")
            {
                var text = Regex.Match(body, @"<ErrorText>(.*?)</ErrorText>", RegexOptions.Singleline);
                throw new InvalidOperationException($"{action}: {(text.Success ? WebUtility.HtmlDecode(text.Groups[1].Value).Trim() : code.Groups[1].Value)}");
            }
        }

        private async Task DesignerApproveAsync(string fileUrl, string comment, bool approve, CancellationToken cancellationToken)
        {
            var list = await GetFileParentListAsync(fileUrl, cancellationToken);
            var itemId = await GetFileListItemIdAsync(fileUrl, cancellationToken);
            await ListsUpdateItemAsync(fileUrl, list.Id, "Moderate", new[]
            {
                ("ID", itemId.ToString()),
                ("FileRef", HostOrigin + EscapePath(fileUrl)),
                ("_ModerationStatus", approve ? "0" : "1"),
                ("_ModerationComments", comment ?? string.Empty)
            }, $"{(approve ? "Approve" : "Deny")} of '{Path.GetFileName(fileUrl)}'", cancellationToken);
        }

        /// <summary>Converts REST-shaped values (as built for MERGE) to the string format of UpdateListItems.</summary>
        private static IEnumerable<(string Field, string Value)> ToListsFieldValues(IDictionary<string, object?> values)
        {
            foreach (var (property, value) in values)
            {
                var field = property.StartsWith("OData_", StringComparison.Ordinal) ? property.Substring("OData_".Length) : property;
                string? text = value switch
                {
                    null => null,
                    string s => s,
                    bool b => b ? "1" : "0",
                    JsonElement { ValueKind: JsonValueKind.True } => "1",
                    JsonElement { ValueKind: JsonValueKind.False } => "0",
                    JsonElement { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
                    JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
                    JsonElement { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                        => ";#" + string.Join(";#", results.EnumerateArray().Select(r => r.GetString())) + ";#",
                    IDictionary<string, object?> url when url.TryGetValue("Url", out var u) => $"{u}, {(url.TryGetValue("Description", out var d) ? d : u)}",
                    _ => value.ToString()
                };
                if (text != null) yield return (field, text);
            }
        }

        #endregion
    }
}
