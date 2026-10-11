using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SEOAuditStudio.Models;
using SEOAuditStudio.Services;

namespace SEOAuditStudio.ViewModels
{
    public partial class MainViewModel
    {
        // ---------------------------------------------------------------- export

        private bool CanExport() => HasAudit;

        [RelayCommand(CanExecute = nameof(CanExport))]
        private Task ExportPdf() => ExportAsync("PDF report|*.pdf", ".pdf", (a, path) => ExportService.ExportPdf(a, path, DomainMetrics));

        [RelayCommand(CanExecute = nameof(CanExport))]
        private Task ExportExcel() => ExportAsync("Excel workbook|*.xlsx", ".xlsx", (a, path) => ExportService.ExportExcel(a, path, DomainMetrics));

        [RelayCommand(CanExecute = nameof(CanExport))]
        private Task ExportPagesCsv() => ExportAsync("CSV file|*.csv", "-pages.csv", ExportService.ExportPagesCsv);

        [RelayCommand(CanExecute = nameof(CanExport))]
        private Task ExportIssuesCsv() => ExportAsync("CSV file|*.csv", "-issues.csv", ExportService.ExportIssuesCsv);

        /// <summary>The pages affected by the selected issue, as CSV or Excel (by the chosen extension).</summary>
        [RelayCommand]
        private Task ExportSelectedIssue()
        {
            var issue = SelectedIssue;
            if (issue == null || CurrentAudit == null) return Task.CompletedTask;
            return ExportAsync("Excel workbook|*.xlsx|CSV file|*.csv", "-" + issue.Definition.Id + ".xlsx", (_, path) =>
            {
                var headers = new[] { "Issue", "Severity", "Page URL", "Broken / target URL", "Linked from page", "Anchor text", "Detail" };
                var rows = AffectedRows.Select(r => new object[] { issue.Definition.Title, issue.Definition.Severity.ToString(), r.PageUrl, r.TargetUrl, r.SourceUrl, r.SourceUrl.Length > 0 ? r.SourceNote : "", r.SourceUrl.Length > 0 ? r.Detail : (r.SourceNote.Length > 0 ? $"{r.Detail} — {r.SourceNote}" : r.Detail) });
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ExportService.ExportRowsCsv(path, headers, rows);
                else ExportService.ExportRowsExcel(path, "Affected pages", headers, rows);
            });
        }

        [RelayCommand]
        private Task ExportKeywords()
        {
            var d = DomainMetrics;
            if (d == null) return Task.CompletedTask;
            return ExportAsync("Excel workbook|*.xlsx|CSV file|*.csv", $"{d.Domain}-keywords.xlsx", (_, path) =>
            {
                var headers = new[] { "Keyword", "Position", "Search volume", "CPC", "Traffic %", "URL" };
                var rows = d.TopKeywords.Select(k => new object[] { k.Keyword, k.Position, k.SearchVolume, k.Cpc, k.TrafficPercent, k.Url });
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ExportService.ExportRowsCsv(path, headers, rows);
                else ExportService.ExportRowsExcel(path, "Organic keywords", headers, rows);
            }, requireAudit: false);
        }

        private async Task ExportAsync(string filter, string suffix, Action<AuditResult, string> write, bool requireAudit = true)
        {
            var audit = CurrentAudit;
            if (audit == null && requireAudit) return;

            var baseName = audit == null ? "export" : $"{SafeName(audit.Name)}-{audit.StartedAt:yyyyMMdd-HHmm}";
            var dlg = new SaveFileDialog
            {
                Filter = filter,
                FileName = suffix.StartsWith('.') || suffix.StartsWith('-') ? baseName + suffix : suffix,
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            StatusText = "Exporting...";
            try
            {
                await Task.Run(() => write(audit!, dlg.FileName));
                StatusText = $"Exported {Path.GetFileName(dlg.FileName)}";
                AddLog($"Exported {dlg.FileName}");
                if (MessageBox.Show($"Saved {Path.GetFileName(dlg.FileName)}.\n\nOpen it now?", "Export",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenPath(dlg.FileName);
            }
            catch (IOException ex)
            {
                ShowError("Export failed — is the file open in another program?", ex);
            }
            catch (Exception ex)
            {
                ShowError("Export failed", ex);
            }
        }

        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var s = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
            return s.Length > 60 ? s[..60] : s;
        }

        // ---------------------------------------------------------------- history

        public ObservableCollection<AuditSummary> History { get; } = new();

        private void RefreshHistory()
        {
            History.Clear();
            foreach (var s in _storage.LoadIndex()) History.Add(s);
        }

        [RelayCommand]
        private void OpenHistory(AuditSummary? summary)
        {
            if (summary == null) return;
            try
            {
                var audit = _storage.LoadAudit(summary.Id);
                if (audit == null)
                {
                    MessageBox.Show("That audit file no longer exists.", "History");
                    _storage.DeleteAudit(summary.Id);
                    RefreshHistory();
                    return;
                }
                LoadAudit(audit);
                SelectedSection = SectionDashboard;
                StatusText = $"Opened audit of {audit.Name} from {audit.StartedAt:dd MMM yyyy HH:mm}";
            }
            catch (Exception ex)
            {
                ShowError("Could not open the audit", ex);
            }
        }

        [RelayCommand]
        private void DeleteHistory(AuditSummary? summary)
        {
            if (summary == null) return;
            if (MessageBox.Show($"Delete the audit of {summary.Name} from {summary.StartedAt:dd MMM yyyy HH:mm}?", "History",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _storage.DeleteAudit(summary.Id);
            RefreshHistory();
        }

        [RelayCommand]
        private void RefreshHistoryList() => RefreshHistory();
    }
}
