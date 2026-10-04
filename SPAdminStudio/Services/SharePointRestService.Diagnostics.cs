using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    /// <summary>
    /// Turns failed write requests into actionable messages: HTTP status, SharePoint error code,
    /// "a firewall/sign-in page answered" detection, and the account's real permissions on the target.
    /// </summary>
    public partial class SharePointRestService
    {
        /// <summary>
        /// Reads the current user's effective permissions on a list (root URL), a file or a folder.
        /// Returns null when SharePoint will not tell us (e.g. no access at all).
        /// </summary>
        public async Task<PermissionInfo?> GetEffectivePermissionsAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var safePath = SafeUrl(serverRelativeUrl.TrimEnd('/'));
            var candidates = new[]
            {
                ($"_api/web/GetList('{safePath}')/EffectiveBasePermissions", "library"),
                ($"_api/web/GetFileByServerRelativeUrl('{safePath}')/ListItemAllFields/EffectiveBasePermissions", "file"),
                ($"_api/web/GetFolderByServerRelativeUrl('{safePath}')/ListItemAllFields/EffectiveBasePermissions", "folder")
            };

            foreach (var (url, scope) in candidates)
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Object) continue;
                if (d.TryGetProperty("EffectiveBasePermissions", out var inner)) d = inner;

                if (ulong.TryParse(JStr(d, "Low"), out var low) && ulong.TryParse(JStr(d, "High"), out var high))
                {
                    return new PermissionInfo
                    {
                        Low = low,
                        High = high,
                        Scope = scope,
                        Account = _currentSiteInfo?.CurrentUserLogin ?? string.Empty
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Sends one probe request and reports whether it reached the SharePoint API. Probe POSTs carry
        /// NO X-RequestDigest, so SharePoint refuses them before doing anything: probing never changes data.
        /// </summary>
        public async Task<WriteProbeResult> ProbeAsync(string name, HttpMethod method, string relativeUrl, CancellationToken cancellationToken = default,
            string? requestBody = null, string? requestContentType = null)
        {
            if (_httpClient == null) throw new InvalidOperationException("Not connected.");

            var result = new WriteProbeResult { Name = name, Method = method.Method, Url = relativeUrl };
            try
            {
                using var request = new HttpRequestMessage(method, relativeUrl);
                if (method == HttpMethod.Post)
                {
                    request.Content = requestBody == null ? new ByteArrayContent(Array.Empty<byte>()) : new StringContent(requestBody);
                    if (requestContentType != null)
                    {
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(requestContentType);
                    }
                }
                var soapOperation = Regex.Match(requestBody ?? string.Empty, @"<soap:Body>\s*<(\w+)");
                if (relativeUrl.EndsWith(".asmx", StringComparison.OrdinalIgnoreCase) && soapOperation.Success)
                {
                    request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://schemas.microsoft.com/sharepoint/soap/{soapOperation.Groups[1].Value}\"");
                }
                if (relativeUrl.Contains("_vti_aut/author.dll"))
                {
                    // Same header SharePoint Designer sends with FrontPage RPC calls
                    request.Headers.TryAddWithoutValidation("X-Vermeer-Content-Type", "application/x-www-form-urlencoded");
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var trimmed = body.TrimStart();
                var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);

                result.StatusCode = (int)response.StatusCode;
                result.ServedBySharePoint = response.Headers.Contains("SPRequestGuid") || response.Headers.Contains("MicrosoftSharePointTeamServices");
                result.Server = response.Headers.Server.ToString();
                // FrontPage RPC (SharePoint Designer) answers with an HTML "vermeer RPC packet"
                var isFrontPageRpc = body.Contains("vermeer RPC packet", StringComparison.OrdinalIgnoreCase);
                // OPTIONS (WebDAV) has no body: SharePoint's own headers prove it answered
                var isOptions = method == HttpMethod.Options && response.IsSuccessStatusCode && result.ServedBySharePoint;

                result.ReachedApi = isFrontPageRpc || isOptions ||
                                    (!isHtml && (contentType.Contains("json") || contentType.Contains("xml") || trimmed.StartsWith("{") || trimmed.StartsWith("<?xml") || trimmed.StartsWith("<m:error") || trimmed.StartsWith("<soap:")));
                result.Detail = isFrontPageRpc
                    ? "FrontPage RPC answered: " + FrontPageRpcSummary(body)
                    : isOptions
                        ? "WebDAV verbs allowed: " + string.Join(", ", response.Content.Headers.Allow)
                        : result.ReachedApi
                            ? response.IsSuccessStatusCode ? "OK" : "SharePoint refused it as expected: " + FirstSentence(ExtractErrorMessage(body))
                            : isHtml ? $"web page \"{HtmlTitle(body)}\"" : $"unexpected response ({contentType})";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Detail = $"request failed: {ex.Message}";
            }

            return result;
        }

        private static string FrontPageRpcSummary(string body)
        {
            var version = Regex.Match(body, @"<li>major ver=(\d+)", RegexOptions.IgnoreCase);
            if (version.Success) return $"server version {version.Groups[1].Value}";
            var message = Regex.Match(body, @"<li>msg=([^\n<]*)", RegexOptions.IgnoreCase);
            return message.Success ? WebUtility.HtmlDecode(message.Groups[1].Value).Trim() : "OK";
        }

        private static string HtmlTitle(string html)
        {
            var title = Regex.Match(html, @"<title[^>]*>\s*(.*?)\s*</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : "untitled";
        }

        private static string FirstSentence(string text)
        {
            var end = text.IndexOf(". ", StringComparison.Ordinal);
            var sentence = end > 0 ? text[..(end + 1)] : text;
            return sentence.Length > 120 ? sentence[..120] + "..." : sentence;
        }

        /// <summary>Readable description of an error response body.</summary>
        private static string DescribeErrorBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "(empty response)";

            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                var title = Regex.Match(body, @"<title[^>]*>\s*(.*?)\s*</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var pageTitle = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : "(untitled page)";
                return $"the server returned a web page (\"{pageTitle}\") instead of a SharePoint response — " +
                       "a firewall / reverse proxy, or a sign-in page, may be blocking this request.\n" +
                       "Run 🩺 Diagnose Write Access (Site Explorer → Site Summary) to see which requests are blocked from this address.";
            }

            var message = ExtractErrorMessage(body);

            // SharePoint error codes, e.g. "-2147024891, System.UnauthorizedAccessException"
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    message += $" [{code.GetString()}]";
                }
            }
            catch (JsonException) { }

            return message;
        }

        /// <summary>
        /// Builds the exception for a failed write. On 401/403 it appends what the account is actually
        /// allowed to do on the resource, which separates "no permission" from "blocked by something else".
        /// </summary>
        private async Task<InvalidOperationException> CreateWriteFailureAsync(string action, string? resourcePath,
            HttpStatusCode status, string? reason, string body, CancellationToken cancellationToken)
        {
            var message = $"{action} failed — HTTP {(int)status} {reason}: {DescribeErrorBody(body)}";

            if ((status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden) && !string.IsNullOrEmpty(resourcePath))
            {
                try
                {
                    var permissions = await GetEffectivePermissionsAsync(resourcePath, cancellationToken);
                    message += permissions != null
                        ? $"\nPermissions of '{permissions.Account}' on this {permissions.Scope}: {permissions.Describe()}"
                        : $"\nSharePoint does not report any permissions for '{_currentSiteInfo?.CurrentUserLogin}' on '{resourcePath}'.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"Permission check failed: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[SP WRITE FAILURE] {message}\n{body}");
            return new InvalidOperationException(message);
        }
    }
}
