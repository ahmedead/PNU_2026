using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace SEOAuditStudio.Services
{
    /// <summary>Turns pasted text, CSV and Excel files into a clean, de-duplicated list of absolute URLs.</summary>
    public static class UrlInputService
    {
        private static readonly string[] UrlHeaderNames = { "url", "urls", "link", "links", "address", "page", "website", "site", "domain" };

        public static List<string> ParseText(string text)
        {
            var parts = Regex.Split(text ?? "", @"[\r\n\t,; ]+");
            return Clean(parts);
        }

        public static List<string> ImportFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".xlsx" or ".xlsm" => ImportExcel(path),
                ".xls" => throw new NotSupportedException("Old .xls files are not supported. Open the file in Excel and save it as .xlsx or .csv."),
                _ => ImportCsv(path)
            };
        }

        /// <summary>
        /// Reads the column whose header looks like "URL" (or the first column holding URLs) from each row.
        /// </summary>
        public static List<string> ImportCsv(string path)
        {
            var rows = new List<string[]>();
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = false,
                BadDataFound = null,
                MissingFieldFound = null,
                DetectDelimiter = true
            };
            using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
            using (var csv = new CsvReader(reader, config))
            {
                while (csv.Read())
                {
                    var record = csv.Parser.Record;
                    if (record != null) rows.Add(record);
                }
            }
            return PickUrls(rows);
        }

        public static List<string> ImportExcel(string path)
        {
            // Open read-only and shared so a workbook that is open in Excel can still be imported
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);
            var rows = new List<string[]>();
            foreach (var sheet in workbook.Worksheets)
            {
                var used = sheet.RangeUsed();
                if (used == null) continue;
                foreach (var row in used.Rows())
                {
                    rows.Add(row.Cells().Select(c =>
                        c.HasHyperlink && c.GetHyperlink().IsExternal ? c.GetHyperlink().ExternalAddress.ToString() : c.GetString()).ToArray());
                }
                if (rows.Count > 0) break; // first non-empty sheet
            }
            return PickUrls(rows);
        }

        private static List<string> PickUrls(List<string[]> rows)
        {
            if (rows.Count == 0) return new List<string>();

            int column = -1;
            var header = rows[0];
            for (int i = 0; i < header.Length; i++)
            {
                if (UrlHeaderNames.Contains(header[i].Trim().ToLowerInvariant())) { column = i; break; }
            }

            if (column < 0)
            {
                // No recognisable header: use the column holding the most URL-like values
                int width = rows.Max(r => r.Length);
                int best = 0;
                for (int i = 0; i < width; i++)
                {
                    int hits = rows.Count(r => i < r.Length && Normalize(r[i]) != null);
                    if (hits > best) { best = hits; column = i; }
                }
                if (column < 0) return new List<string>();
            }

            return Clean(rows.Select(r => column < r.Length ? r[column] : ""));
        }

        private static List<string> Clean(IEnumerable<string> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var v in values)
            {
                var url = Normalize(v);
                if (url != null && seen.Add(url)) result.Add(url);
            }
            return result;
        }

        /// <summary>Returns an absolute http(s) URL, adding https:// to bare domains; null when the value is not a URL.</summary>
        public static string? Normalize(string? value)
        {
            var v = (value ?? "").Trim().Trim('"', '\'', '<', '>');
            if (v.Length < 4 || v.Contains(' ')) return null;
            if (!v.Contains("://"))
            {
                // Bare domain like example.com or www.example.com/path
                if (!Regex.IsMatch(v, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9-]+)*\.[a-z][a-z0-9-]+(:\d+)?([/?].*)?$", RegexOptions.IgnoreCase)) return null;
                v = "https://" + v;
            }
            if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            if (!uri.Host.Contains('.') && uri.Host != "localhost") return null;
            return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
        }
    }
}
