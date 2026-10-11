using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Services
{
    /// <summary>One line in the History view; kept in an index so the full audits load only when opened.</summary>
    public sealed class AuditSummary
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string StartUrl { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public int Pages { get; set; }
        public int SiteHealth { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public int Notices { get; set; }
        public bool Cancelled { get; set; }
        public bool HitPageLimit { get; set; }
        public string StatusText => Cancelled ? "Stopped" : HitPageLimit ? "Page limit reached" : "Complete";
    }

    /// <summary>Settings and audit history under %AppData%\SEOAuditStudio. API keys are DPAPI-encrypted for the Windows user.</summary>
    public sealed class StorageService
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SEOAuditStudio");
        private string AuditsFolder => Path.Combine(Folder, "audits");
        private string SettingsPath => Path.Combine(Folder, "settings.json");
        private string IndexPath => Path.Combine(AuditsFolder, "index.json");

        public StorageService()
        {
            Directory.CreateDirectory(AuditsFolder);
        }

        // ---------------- settings

        public AuditSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new AuditSettings();
                var s = JsonSerializer.Deserialize<AuditSettings>(File.ReadAllText(SettingsPath), Json) ?? new AuditSettings();
                s.PageSpeedApiKey = Unprotect(s.PageSpeedApiKey);
                s.SemrushApiKey = Unprotect(s.SemrushApiKey);
                return s;
            }
            catch { return new AuditSettings(); }
        }

        public void SaveSettings(AuditSettings settings)
        {
            var copy = JsonSerializer.Deserialize<AuditSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
            copy.PageSpeedApiKey = Protect(settings.PageSpeedApiKey);
            copy.SemrushApiKey = Protect(settings.SemrushApiKey);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(copy, new JsonSerializerOptions(Json) { WriteIndented = true }));
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("dpapi:")) return value ?? "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        // ---------------- history

        public List<AuditSummary> LoadIndex()
        {
            try
            {
                return File.Exists(IndexPath)
                    ? JsonSerializer.Deserialize<List<AuditSummary>>(File.ReadAllText(IndexPath), Json) ?? new()
                    : new();
            }
            catch { return new(); }
        }

        public void SaveAudit(AuditResult audit)
        {
            File.WriteAllText(AuditPath(audit.Id), JsonSerializer.Serialize(audit, Json));
            var index = LoadIndex();
            index.RemoveAll(s => s.Id == audit.Id);
            index.Insert(0, Summarize(audit));
            WriteIndex(index);
        }

        public AuditResult? LoadAudit(string id)
        {
            var path = AuditPath(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<AuditResult>(File.ReadAllText(path), Json) : null;
        }

        public void DeleteAudit(string id)
        {
            var path = AuditPath(id);
            if (File.Exists(path)) File.Delete(path);
            var index = LoadIndex();
            index.RemoveAll(s => s.Id == id);
            WriteIndex(index);
        }

        /// <summary>The most recent earlier audit of the same start URL, used for the "change since last audit" figures.</summary>
        public AuditResult? FindPrevious(AuditResult audit)
        {
            var prev = LoadIndex()
                .Where(s => s.Id != audit.Id && s.StartedAt < audit.StartedAt &&
                            string.Equals(s.StartUrl.TrimEnd('/'), audit.StartUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefault();
            return prev == null ? null : LoadAudit(prev.Id);
        }

        public static AuditSummary Summarize(AuditResult a) => new()
        {
            Id = a.Id, Name = a.Name, StartUrl = a.StartUrl, StartedAt = a.StartedAt, Pages = a.Pages.Count,
            SiteHealth = a.SiteHealth, Errors = a.Errors, Warnings = a.Warnings, Notices = a.Notices, Cancelled = a.Cancelled, HitPageLimit = a.HitPageLimit
        };

        private void WriteIndex(List<AuditSummary> index) =>
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(index, Json));

        private string AuditPath(string id) => Path.Combine(AuditsFolder, id + ".json");
    }
}
