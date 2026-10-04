using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    /// <summary>
    /// Copies master pages, page layouts, pages, assets and folders from a source site (UAT)
    /// to a target site (Production): content + list item properties, then check-in / publish / approve.
    /// </summary>
    public class DeploymentService
    {
        private static readonly HashSet<string> ExcludedFieldTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            // Values reference farm-specific IDs or are system-managed
            "Lookup", "LookupMulti", "User", "UserMulti", "TaxonomyFieldType", "TaxonomyFieldTypeMulti",
            "Computed", "Counter", "Attachments", "File", "ModStat", "ContentTypeId", "Guid", "WorkflowStatus",
            "Calculated", "ThreadIndex", "Recurrence", "CrossProjectLink", "WorkflowEventType", "PageSeparator",
            "OutcomeChoice", "AllDayEvent"
        };

        private static readonly HashSet<string> ExcludedFieldNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ContentType", "ContentTypeId", "FileLeafRef", "_ModerationStatus", "_ModerationComments", "MetaInfo",
            "_CopySource", "_SourceUrl", "_SharedFileIndex", "TemplateUrl", "xd_ProgID", "xd_Signature", "Order",
            "GUID", "owshiddenversion", "HtmlDesignAssociated", "HtmlDesignConversionStatus", "HtmlDesignStatusAndPreview",
            "HtmlDesignLockedFile", "HtmlDesignFromMaster", "HtmlDesignPreviewUrl"
        };

        /// <summary>Hidden fields that still carry meaning for branding files and publishing pages.</summary>
        private static readonly HashSet<string> AllowedHiddenFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "PublishingAssociatedContentType", "PublishingHidden", "UIVersion", "MasterPageDescription",
            "PublishingPreviewImage", "PublishingAssociatedVariations", "PublishingPageLayout", "PublishingPageContent"
        };

        private readonly ISharePointService _source;
        private readonly ISharePointService _target;

        public event Action<string>? Log;

        public DeploymentService(ISharePointService source, ISharePointService target)
        {
            _source = source;
            _target = target;
        }

        #region Path & URL mapping

        private static string SiteCollectionRoot(ISharePointService svc) =>
            (svc.CurrentSiteInfo?.SiteCollectionServerRelativeUrl ?? string.Empty).TrimEnd('/');

        private static string Origin(ISharePointService svc) =>
            Uri.TryCreate(svc.BaseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : string.Empty;

        /// <summary>Same path relative to the site collection, re-rooted on the target site collection.</summary>
        public string MapToTarget(string sourceUrl)
        {
            var sourceRoot = SiteCollectionRoot(_source);
            var targetRoot = SiteCollectionRoot(_target);
            var path = sourceUrl.StartsWith("/") ? sourceUrl : "/" + sourceUrl;

            var relative = sourceRoot.Length > 0 && path.StartsWith(sourceRoot + "/", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(sourceRoot.Length)
                : path;

            return targetRoot + relative;
        }

        /// <summary>Replaces absolute links to the source host (http or https) with the target host.</summary>
        public string RewriteUrls(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sourceOrigin = Origin(_source);
            var targetOrigin = Origin(_target);
            if (string.IsNullOrEmpty(sourceOrigin) || string.IsNullOrEmpty(targetOrigin) ||
                sourceOrigin.Equals(targetOrigin, StringComparison.OrdinalIgnoreCase))
                return text;

            var host = new Uri(sourceOrigin).Authority;
            return Regex.Replace(text, @"https?://" + Regex.Escape(host) + @"(?=[/""'\s<)]|$)", targetOrigin, RegexOptions.IgnoreCase);
        }

        private static string ParentOf(string serverRelativeUrl)
        {
            var trimmed = serverRelativeUrl.TrimEnd('/');
            var index = trimmed.LastIndexOf('/');
            return index <= 0 ? "/" : trimmed.Substring(0, index);
        }

        private static string NameOf(string serverRelativeUrl) => Path.GetFileName(serverRelativeUrl.TrimEnd('/'));

        #endregion

        #region Analyze

        public async Task<List<DeploymentPlanItem>> AnalyzeAsync(IEnumerable<DeploymentQueueEntry> queue, DeploymentOptions options,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var plan = new List<DeploymentPlanItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in queue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Reading {entry.SourceUrl}...");

                if (entry.IsFolder)
                {
                    await ExpandFolderAsync(entry.SourceUrl, plan, seen, progress, cancellationToken);
                    continue;
                }

                if (!seen.Add(entry.SourceUrl)) continue;
                var file = await _source.TryGetFileAsync(entry.SourceUrl, cancellationToken);
                if (file == null)
                {
                    plan.Add(new DeploymentPlanItem
                    {
                        Kind = DeployItemKind.File, SourceUrl = entry.SourceUrl, TargetUrl = MapToTarget(entry.SourceUrl),
                        Status = DeployStatus.Failed, Message = "Not found on source"
                    });
                    continue;
                }
                plan.Add(CreateFilePlanItem(file));
            }

            int index = 0;
            foreach (var item in plan.Where(p => p.Status == DeployStatus.Pending))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Checking target ({++index}/{plan.Count}) {item.TargetUrl}...");
                await CheckTargetStateAsync(item, options, cancellationToken);
            }

            return Order(plan);
        }

        private DeploymentPlanItem CreateFilePlanItem(SharePointItem file) => new()
        {
            Kind = DeployItemKind.File,
            SourceUrl = file.ServerRelativeUrl,
            TargetUrl = MapToTarget(file.ServerRelativeUrl),
            Size = file.Length,
            SourceModified = file.TimeLastModified == DateTime.MinValue ? null : file.TimeLastModified
        };

        private async Task ExpandFolderAsync(string folderUrl, List<DeploymentPlanItem> plan, HashSet<string> seen,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            folderUrl = folderUrl.TrimEnd('/');
            if (!seen.Add(folderUrl)) return;

            progress?.Report($"Reading folder {folderUrl}...");
            plan.Add(new DeploymentPlanItem { Kind = DeployItemKind.Folder, SourceUrl = folderUrl, TargetUrl = MapToTarget(folderUrl) });

            foreach (var file in await _source.GetFolderFilesAsync(folderUrl, cancellationToken))
            {
                if (seen.Add(file.ServerRelativeUrl)) plan.Add(CreateFilePlanItem(file));
            }

            foreach (var sub in await _source.GetSubFoldersAsync(folderUrl, cancellationToken))
            {
                await ExpandFolderAsync(sub.ServerRelativeUrl, plan, seen, progress, cancellationToken);
            }
        }

        private async Task CheckTargetStateAsync(DeploymentPlanItem item, DeploymentOptions options, CancellationToken cancellationToken)
        {
            try
            {
                if (item.Kind == DeployItemKind.Folder)
                {
                    var exists = await _target.FolderExistsAsync(item.TargetUrl, cancellationToken);
                    item.Status = exists ? DeployStatus.Pending : DeployStatus.New;
                    item.Message = exists ? "Folder exists" : "Folder will be created";
                    return;
                }

                var existing = await _target.TryGetFileAsync(item.TargetUrl, cancellationToken);
                if (existing == null)
                {
                    item.Status = DeployStatus.New;
                    item.Message = "New file";
                }
                else
                {
                    item.TargetModified = existing.TimeLastModified;
                    if (existing.CheckOutType != 2 && !IsTargetUser(existing.CheckedOutByUser))
                    {
                        item.Status = DeployStatus.Blocked;
                        item.Message = $"Checked out to {existing.CheckedOutByUser} on target";
                    }
                    else if (!options.Overwrite)
                    {
                        item.Status = DeployStatus.Skip;
                        item.Message = "Exists on target (overwrite is off)";
                    }
                    else
                    {
                        item.Status = DeployStatus.Overwrite;
                        item.Message = existing.Length == item.Size ? "Exists (same size)" : $"Exists ({existing.FormattedSize} on target)";
                    }
                }

                if (IsPage(item.SourceUrl))
                {
                    item.Message += " — page fields are copied; web parts in zones are not";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                item.Status = DeployStatus.Failed;
                item.Message = ex.Message;
            }
        }

        private bool IsTargetUser(string checkedOutBy) =>
            !string.IsNullOrEmpty(checkedOutBy) &&
            string.Equals(checkedOutBy, _target.CurrentSiteInfo?.CurrentUserTitle, StringComparison.OrdinalIgnoreCase);

        private static bool IsMasterPageGallery(string url) => url.Contains("/_catalogs/masterpage", StringComparison.OrdinalIgnoreCase);

        private static bool IsPage(string url) =>
            url.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) && !IsMasterPageGallery(url);

        /// <summary>
        /// Master pages before page layouts (layouts reference them), branding assets next, pages last
        /// (pages reference layouts). Folders always precede their contents.
        /// </summary>
        private static List<DeploymentPlanItem> Order(List<DeploymentPlanItem> plan)
        {
            static int Priority(DeploymentPlanItem p)
            {
                if (IsMasterPageGallery(p.SourceUrl)) return 0;
                if (p.SourceUrl.Contains("/Style Library", StringComparison.OrdinalIgnoreCase)) return 1;
                if (IsPage(p.SourceUrl)) return 3;
                return 2;
            }

            static int ExtensionRank(DeploymentPlanItem p)
            {
                if (p.Kind == DeployItemKind.Folder) return 0;
                var ext = Path.GetExtension(p.SourceUrl).ToLowerInvariant();
                return ext switch { ".master" => 1, ".html" => 2, ".aspx" => 3, _ => 4 };
            }

            return plan
                .OrderBy(p => p.Kind == DeployItemKind.Folder ? 0 : Priority(p))
                .ThenBy(p => p.Kind == DeployItemKind.Folder ? p.SourceUrl : ParentOf(p.SourceUrl), StringComparer.OrdinalIgnoreCase)
                .ThenBy(ExtensionRank)
                .ThenBy(p => p.SourceUrl, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion

        #region Deploy

        /// <summary>Caches target lookups for the duration of one deployment run.</summary>
        public sealed class RunContext
        {
            public string BackupFolder { get; init; } = string.Empty;
            internal readonly Dictionary<string, List<SPFieldInfo>> Fields = new();
            internal readonly Dictionary<string, List<SPContentTypeInfo>> ListContentTypes = new();
            internal List<SPContentTypeInfo>? SiteContentTypes;
            internal readonly HashSet<string> KnownFolders = new(StringComparer.OrdinalIgnoreCase);
        }

        public async Task DeployItemAsync(DeploymentPlanItem item, DeploymentOptions options, RunContext context, CancellationToken cancellationToken)
        {
            if (!item.IsActionable) return;

            item.Status = DeployStatus.Deploying;
            item.Message = "Deploying...";

            try
            {
                if (item.Kind == DeployItemKind.Folder)
                {
                    var existed = await _target.FolderExistsAsync(item.TargetUrl, cancellationToken);
                    await EnsureFolderAsync(item.TargetUrl, context, cancellationToken);
                    item.Status = DeployStatus.Done;
                    item.Message = existed ? "Folder exists" : "Folder created";
                }
                else
                {
                    await DeployFileAsync(item, options, context, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = DeployStatus.Failed;
                item.Message = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                item.Status = DeployStatus.Failed;
                item.Message = ex.Message;
                Log?.Invoke($"[ERROR] Deploy '{item.SourceUrl}' → '{item.TargetUrl}': {ex.Message}");
            }
        }

        private async Task DeployFileAsync(DeploymentPlanItem item, DeploymentOptions options, RunContext context, CancellationToken cancellationToken)
        {
            var warnings = new List<string>();

            // 1. Read source content + properties
            var content = await _source.DownloadFileAsync(item.SourceUrl, null, cancellationToken);
            ListItemSnapshot? snapshot = null;
            if (options.CopyMetadata)
            {
                try
                {
                    snapshot = await _source.GetFileListItemAsync(item.SourceUrl, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Source properties not read: {ex.Message}");
                }
            }

            // 2. Prepare target (re-checked now; the plan may be stale)
            var existing = await _target.TryGetFileAsync(item.TargetUrl, cancellationToken);
            string backupNote = string.Empty;
            if (existing != null)
            {
                if (existing.CheckOutType != 2 && !IsTargetUser(existing.CheckedOutByUser))
                {
                    throw new InvalidOperationException($"Checked out to {existing.CheckedOutByUser} on target.");
                }

                if (!options.Overwrite)
                {
                    item.Status = DeployStatus.Skip;
                    item.Message = "Exists on target (overwrite is off)";
                    return;
                }

                if (options.BackupTarget && !string.IsNullOrEmpty(context.BackupFolder))
                {
                    var previous = await _target.DownloadFileAsync(item.TargetUrl, null, cancellationToken);
                    var backupPath = Path.Combine(context.BackupFolder, item.TargetUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    await File.WriteAllBytesAsync(backupPath, previous, cancellationToken);
                    backupNote = " (backup saved)";
                }

                if (existing.CheckOutType == 2)
                {
                    var existingList = await _target.GetFileParentListAsync(item.TargetUrl, cancellationToken);
                    if (existingList.ForceCheckout)
                    {
                        await _target.CheckOutAsync(item.TargetUrl, cancellationToken);
                    }
                }
            }
            else
            {
                await EnsureFolderAsync(ParentOf(item.TargetUrl), context, cancellationToken);
            }

            // 3. Upload content
            await _target.UploadFileAsync(ParentOf(item.TargetUrl), NameOf(item.TargetUrl), content, overwrite: true, null, cancellationToken);
            Log?.Invoke($"Deployed {item.SourceUrl} → {item.TargetUrl} ({content.Length / 1024} KB)");

            var list = await _target.GetFileParentListAsync(item.TargetUrl, cancellationToken);

            // 4. Properties (content type, page layout, page content, associated content type...)
            if (snapshot != null)
            {
                try
                {
                    await ApplyMetadataAsync(item, snapshot, list, options, context, warnings, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Properties not copied: {ex.Message}");
                }
            }

            // 5. Check in / publish / approve
            try
            {
                await FinalizeAsync(item, list, options, warnings, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Check-in/publish: {ex.Message}");
            }

            item.Status = warnings.Count > 0 ? DeployStatus.Warning : DeployStatus.Done;
            item.Message = (existing != null ? "Overwritten" + backupNote : "Created") +
                           (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : string.Empty);
        }

        private async Task EnsureFolderAsync(string folderUrl, RunContext context, CancellationToken cancellationToken)
        {
            folderUrl = folderUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(folderUrl) || context.KnownFolders.Contains(folderUrl)) return;

            if (await _target.FolderExistsAsync(folderUrl, cancellationToken))
            {
                context.KnownFolders.Add(folderUrl);
                return;
            }

            var parent = ParentOf(folderUrl);
            if (parent == "/")
            {
                throw new InvalidOperationException($"'{folderUrl}' does not exist on the target and cannot be created at the site root.");
            }

            await EnsureFolderAsync(parent, context, cancellationToken);

            // Never create a plain folder directly in a web: that would not be a library
            if (!await _target.FolderIsInListAsync(parent, cancellationToken))
            {
                throw new InvalidOperationException($"Library '{folderUrl}' does not exist on the target. Create the library first, then deploy again.");
            }

            await _target.CreateFolderAsync(parent, NameOf(folderUrl), cancellationToken);
            context.KnownFolders.Add(folderUrl);
            Log?.Invoke($"Created folder on target: {folderUrl}");
        }

        private static bool IsPermissionFailure(Exception ex) =>
            ex.Message.Contains("HTTP 401") || ex.Message.Contains("HTTP 403");

        private static string FirstLine(string text)
        {
            var line = text.Split('\n')[0];
            var dash = line.IndexOf(": ", StringComparison.Ordinal);
            return dash > 0 && dash < line.Length - 2 ? line[(dash + 2)..] : line;
        }

        private static string ToRestName(string internalName) =>
            internalName.StartsWith("_") ? "OData_" + internalName : internalName;

        private static bool IsCopyable(SPFieldInfo field) =>
            !field.ReadOnlyField &&
            (!field.Hidden || AllowedHiddenFields.Contains(field.InternalName)) &&
            !ExcludedFieldTypes.Contains(field.TypeAsString) &&
            !ExcludedFieldNames.Contains(field.InternalName);

        private async Task ApplyMetadataAsync(DeploymentPlanItem item, ListItemSnapshot snapshot, SPListSettings list,
            DeploymentOptions options, RunContext context, List<string> warnings, CancellationToken cancellationToken)
        {
            var itemId = await _target.GetFileListItemIdAsync(item.TargetUrl, cancellationToken);

            // Content type is matched by name: IDs of UI-created content types differ between farms
            if (!string.IsNullOrEmpty(snapshot.ContentTypeName))
            {
                if (!context.ListContentTypes.TryGetValue(list.Id, out var listContentTypes))
                {
                    listContentTypes = await _target.GetListContentTypesAsync(list.Id, cancellationToken);
                    context.ListContentTypes[list.Id] = listContentTypes;
                }

                var match = listContentTypes.FirstOrDefault(c => c.Name.Equals(snapshot.ContentTypeName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    warnings.Add($"Content type '{snapshot.ContentTypeName}' is not in target library '{list.Title}'");
                }
                else
                {
                    await _target.UpdateListItemAsync(list.Id, itemId, list.EntityTypeName,
                        new Dictionary<string, object?> { ["ContentTypeId"] = match.Id }, cancellationToken, item.TargetUrl);
                }
            }

            if (!context.Fields.TryGetValue(list.Id, out var fields))
            {
                fields = await _target.GetListFieldsAsync(list.Id, cancellationToken);
                context.Fields[list.Id] = fields;
            }

            var values = new Dictionary<string, object?>();
            foreach (var field in fields.Where(IsCopyable))
            {
                var property = ToRestName(field.InternalName);
                if (!snapshot.Values.TryGetValue(property, out var sourceValue)) continue;

                var converted = await ConvertValueAsync(field, sourceValue, options, context, warnings, cancellationToken);
                if (converted.include) values[property] = converted.value;
            }

            if (values.Count > 0)
            {
                try
                {
                    await _target.UpdateListItemAsync(list.Id, itemId, list.EntityTypeName, values, cancellationToken, item.TargetUrl);
                }
                catch (InvalidOperationException ex) when (values.Count > 1 && !IsPermissionFailure(ex))
                {
                    // One rejected field fails the whole MERGE: retry individually so the rest still copy
                    Log?.Invoke($"Bulk property update rejected for {item.TargetUrl}; retrying field by field. ({ex.Message})");
                    var rejected = new List<string>();
                    foreach (var kv in values)
                    {
                        try
                        {
                            await _target.UpdateListItemAsync(list.Id, itemId, list.EntityTypeName,
                                new Dictionary<string, object?> { [kv.Key] = kv.Value }, cancellationToken, item.TargetUrl);
                        }
                        catch (InvalidOperationException fieldEx)
                        {
                            rejected.Add($"{kv.Key} ({FirstLine(fieldEx.Message)})");
                        }
                    }

                    if (rejected.Count > 0)
                    {
                        warnings.Add($"{rejected.Count} of {values.Count} properties not copied: {string.Join("; ", rejected)}");
                    }
                }
            }

            // A page whose layout is missing on the target renders an error
            if (values.TryGetValue("PublishingPageLayout", out var layoutValue) &&
                layoutValue is Dictionary<string, object?> layout && layout.TryGetValue("Url", out var layoutUrl) &&
                layoutUrl is string url && Uri.TryCreate(url, UriKind.Absolute, out var layoutUri))
            {
                var layoutPath = Uri.UnescapeDataString(layoutUri.AbsolutePath);
                if (await _target.TryGetFileAsync(layoutPath, cancellationToken) == null)
                {
                    warnings.Add($"Page layout '{layoutPath}' does not exist on target — deploy it too");
                }
            }
        }

        private async Task<(bool include, object? value)> ConvertValueAsync(SPFieldInfo field, JsonElement value,
            DeploymentOptions options, RunContext context, List<string> warnings, CancellationToken cancellationToken)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString() ?? string.Empty;
                    if (field.InternalName.Equals("PublishingAssociatedContentType", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, await RemapAssociatedContentTypeAsync(text, context, warnings, cancellationToken));
                    }
                    return (true, options.RewriteUrls ? RewriteUrls(text) : text);

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return (true, value);

                case JsonValueKind.Object:
                    var type = value.TryGetProperty("__metadata", out var meta) && meta.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "SP.FieldUrlValue")
                    {
                        var url = value.TryGetProperty("Url", out var u) ? u.GetString() : null;
                        if (string.IsNullOrEmpty(url)) return (false, null);
                        var description = value.TryGetProperty("Description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty;
                        return (true, new Dictionary<string, object?>
                        {
                            ["__metadata"] = new { type = "SP.FieldUrlValue" },
                            ["Url"] = options.RewriteUrls ? RewriteUrls(url) : url,
                            ["Description"] = options.RewriteUrls ? RewriteUrls(description) : description
                        });
                    }
                    if (type == "Collection(Edm.String)") return (true, value);
                    return (false, null);

                default:
                    // null: never clear a value on the target
                    return (false, null);
            }
        }

        /// <summary>";#Article Page;#0x0101...;#" — replace the ID with the target's ID for the same content type name.</summary>
        private async Task<string> RemapAssociatedContentTypeAsync(string value, RunContext context, List<string> warnings, CancellationToken cancellationToken)
        {
            var parts = value.Split(";#", StringSplitOptions.None);
            if (parts.Length < 3 || string.IsNullOrEmpty(parts[1])) return value;

            context.SiteContentTypes ??= await _target.GetContentTypesAsync(cancellationToken);
            var match = context.SiteContentTypes.FirstOrDefault(c => c.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (match == null || string.IsNullOrEmpty(match.Id))
            {
                warnings.Add($"Associated content type '{parts[1]}' not found on target");
                return value;
            }

            return $";#{match.Name};#{match.Id};#";
        }

        private async Task FinalizeAsync(DeploymentPlanItem item, SPListSettings list, DeploymentOptions options,
            List<string> warnings, CancellationToken cancellationToken)
        {
            var state = await _target.TryGetFileAsync(item.TargetUrl, cancellationToken)
                        ?? throw new InvalidOperationException("File disappeared after upload.");

            if (state.CheckOutType != 2)
            {
                switch (options.CheckIn)
                {
                    case CheckInMode.LeaveCheckedOut:
                        warnings.Add("Left checked out");
                        return;
                    case CheckInMode.Draft:
                        await _target.CheckInAsync(item.TargetUrl, list.EnableMinorVersions ? 0 : 1, options.Comment, cancellationToken);
                        break;
                    case CheckInMode.PublishMajor:
                        await _target.CheckInAsync(item.TargetUrl, 1, options.Comment, cancellationToken);
                        break;
                }
            }
            else if (options.CheckIn == CheckInMode.PublishMajor && list.EnableMinorVersions && !state.Version.EndsWith(".0"))
            {
                await _target.PublishAsync(item.TargetUrl, options.Comment, cancellationToken);
            }

            if (options.Approve && list.EnableModeration && options.CheckIn == CheckInMode.PublishMajor)
            {
                try
                {
                    await _target.ApproveAsync(item.TargetUrl, options.Comment, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Approve: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
