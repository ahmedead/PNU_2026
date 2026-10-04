using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SPAdminStudio.Models;
using SPAdminStudio.Services;
using SPAdminStudio.Views;

namespace SPAdminStudio.ViewModels
{
    public partial class CodeViewerViewModel : ObservableObject
    {
        private readonly ISharePointService _spService;

        [ObservableProperty]
        private ObservableCollection<EditorDocument> _openDocuments = new();

        [ObservableProperty]
        private EditorDocument? _activeDocument;

        [ObservableProperty]
        private ObservableCollection<SharePointSnippet> _snippets = new();

        [ObservableProperty]
        private SharePointSnippet? _selectedSnippet;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready.";

        [ObservableProperty]
        private bool _autoCheckOut = true;

        [ObservableProperty]
        private bool _autoPublish = false;

        // View mode: 0 = Code View, 1 = Split View (Code + Browser Preview), 2 = Preview Only
        [ObservableProperty]
        private int _viewMode = 0;

        // Search & Replace
        [ObservableProperty]
        private bool _isFindReplaceVisible;

        [ObservableProperty]
        private string _findText = string.Empty;

        [ObservableProperty]
        private string _replaceText = string.Empty;

        [ObservableProperty]
        private bool _matchCase;

        [ObservableProperty]
        private int _currentLine = 1;

        [ObservableProperty]
        private int _currentColumn = 1;

        public event Action<string>? LogRequested;
        public event Action<string>? InsertSnippetRequested;
        public event Action? RefreshPreviewRequested;

        public CodeViewerViewModel(ISharePointService spService)
        {
            _spService = spService;
            InitializeSnippets();
        }

        private void InitializeSnippets()
        {
            Snippets = new ObservableCollection<SharePointSnippet>
            {
                // Content Placeholders
                new SharePointSnippet
                {
                    Category = "Content Placeholders",
                    Name = "PlaceHolderMain (Main Content)",
                    Description = "The primary content container for SharePoint pages",
                    SnippetCode = "<asp:ContentPlaceHolder id=\"PlaceHolderMain\" runat=\"server\" />"
                },
                new SharePointSnippet
                {
                    Category = "Content Placeholders",
                    Name = "PlaceHolderPageTitle",
                    Description = "Browser title tag content placeholder",
                    SnippetCode = "<asp:ContentPlaceHolder id=\"PlaceHolderPageTitle\" runat=\"server\">\r\n    <SharePoint:ProjectProperty Property=\"Title\" runat=\"server\" />\r\n</asp:ContentPlaceHolder>"
                },
                new SharePointSnippet
                {
                    Category = "Content Placeholders",
                    Name = "PlaceHolderAdditionalPageHead",
                    Description = "Inject custom CSS, scripts, or meta tags into <head>",
                    SnippetCode = "<asp:ContentPlaceHolder id=\"PlaceHolderAdditionalPageHead\" runat=\"server\" />"
                },
                new SharePointSnippet
                {
                    Category = "Content Placeholders",
                    Name = "PlaceHolderLeftNavBar",
                    Description = "Quick Launch / Left navigation area placeholder",
                    SnippetCode = "<asp:ContentPlaceHolder id=\"PlaceHolderLeftNavBar\" runat=\"server\" />"
                },

                // SharePoint Asset Controls
                new SharePointSnippet
                {
                    Category = "SharePoint Controls",
                    Name = "CssRegistration (Style Library)",
                    Description = "Registers a CSS stylesheet from Style Library with caching",
                    SnippetCode = "<SharePoint:CssRegistration Name=\"<% $SPUrl:~sitecollection/Style Library/custom.css %>\" runat=\"server\" After=\"corev15.css\" />"
                },
                new SharePointSnippet
                {
                    Category = "SharePoint Controls",
                    Name = "ScriptLink (Style Library)",
                    Description = "Registers a JavaScript file from Style Library with auto-delay load",
                    SnippetCode = "<SharePoint:ScriptLink Name=\"~sitecollection/Style Library/custom.js\" Language=\"javascript\" runat=\"server\" OnDemand=\"false\" />"
                },
                new SharePointSnippet
                {
                    Category = "SharePoint Controls",
                    Name = "AjaxDelta Wrapper",
                    Description = "SharePoint Minimal Download Strategy (MDS) container",
                    SnippetCode = "<SharePoint:AjaxDelta id=\"DeltaPlaceHolderMain\" IsMainContent=\"true\" runat=\"server\">\r\n    <asp:ContentPlaceHolder id=\"PlaceHolderMain\" runat=\"server\" />\r\n</SharePoint:AjaxDelta>"
                },
                new SharePointSnippet
                {
                    Category = "SharePoint Controls",
                    Name = "WebPartZone",
                    Description = "Web part drop zone for page layouts",
                    SnippetCode = "<WebPartPages:WebPartZone runat=\"server\" Title=\"Main Zone\" ID=\"MainZone\" FrameType=\"TitleBarOnly\"><ZoneTemplate></ZoneTemplate></WebPartPages:WebPartZone>"
                },
                new SharePointSnippet
                {
                    Category = "SharePoint Controls",
                    Name = "WebPartManager",
                    Description = "Required WebPartManager declaration for master pages and page layouts",
                    SnippetCode = "<WebPartPages:SPWebPartManager runat=\"server\" />"
                },

                // HTML5 / Modern Helpers
                new SharePointSnippet
                {
                    Category = "HTML & Responsive",
                    Name = "Responsive Viewport Meta",
                    Description = "Mobile viewport configuration for modern portal design",
                    SnippetCode = "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no\" />"
                },
                new SharePointSnippet
                {
                    Category = "HTML & Responsive",
                    Name = "Bootstrap 5 CSS CDN",
                    Description = "Bootstrap 5 stylesheet link",
                    SnippetCode = "<link rel=\"stylesheet\" href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css\" />"
                },
                new SharePointSnippet
                {
                    Category = "HTML & Responsive",
                    Name = "FontAwesome 6 Icons CDN",
                    Description = "FontAwesome icons stylesheet link",
                    SnippetCode = "<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.1/css/all.min.css\" />"
                }
            };
        }

        public async Task LoadFileAsync(SharePointItem item)
        {
            var existing = OpenDocuments.FirstOrDefault(d => d.ServerRelativeUrl.Equals(item.ServerRelativeUrl, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ActiveDocument = existing;
                return;
            }

            if (!EditorDocument.IsTextFile(item.Name))
            {
                MessageBox.Show($"'{item.Name}' is not a text file and cannot be opened in the editor.\nUse Export to download it instead.",
                    "Unsupported File", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            StatusMessage = $"Loading {item.Name}...";

            try
            {
                var bytes = await _spService.DownloadFileAsync(item.ServerRelativeUrl);
                var doc = new EditorDocument(item, bytes);
                var ext = Path.GetExtension(item.Name)?.ToLowerInvariant();
                doc.Syntax = ext switch
                {
                    ".css" => "CSS",
                    ".js" => "JavaScript",
                    ".xml" => "XML",
                    ".json" => "JavaScript",
                    ".master" or ".html" or ".htm" or ".aspx" => "HTML",
                    _ => "HTML"
                };

                OpenDocuments.Add(doc);
                ActiveDocument = doc;
                doc.MarkSaved();

                StatusMessage = $"Opened: {item.Name} ({item.FormattedSize})";
                LogRequested?.Invoke($"Opened file in editor: {item.ServerRelativeUrl}");

                RefreshPreviewRequested?.Invoke();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load {item.Name}: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Failed to load {item.ServerRelativeUrl}: {ex.Message}");
                MessageBox.Show($"Failed to load file content:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task SaveActiveDocumentAsync()
        {
            if (ActiveDocument == null) return;
            await SaveDocumentAsync(ActiveDocument, allowAutoPublish: true);
        }

        /// <summary>
        /// Saves the given document (not necessarily the active one) back to SharePoint.
        /// Returns true only when the upload (and optional auto-publish) succeeded.
        /// </summary>
        public async Task<bool> SaveDocumentAsync(EditorDocument doc, bool allowAutoPublish = false, bool showErrors = true)
        {
            IsBusy = true;
            StatusMessage = $"Saving {doc.FileName} to SharePoint...";

            try
            {
                if (AutoCheckOut)
                {
                    await EnsureCheckedOutAsync(doc);
                }

                var bytes = doc.Encode();
                await _spService.UploadFileAsync(GetParentFolder(doc.ServerRelativeUrl), doc.FileName, bytes, overwrite: true);
                doc.MarkSaved();

                LogRequested?.Invoke($"Saved {doc.FileName} back to SharePoint ({bytes.Length / 1024} KB).");

                if (allowAutoPublish && AutoPublish)
                {
                    await PublishMajorAsync(doc, "Published via SPAdminStudio Designer Editor");
                }

                StatusMessage = $"Successfully saved {doc.FileName} to SharePoint.";
                RefreshPreviewRequested?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to save: {ex.Message}";
                LogRequested?.Invoke($"[ERROR] Save failed for {doc.ServerRelativeUrl}: {ex.Message}");
                if (showErrors)
                {
                    MessageBox.Show($"Failed to save '{doc.FileName}' to SharePoint:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string GetParentFolder(string serverRelativeUrl)
        {
            var index = serverRelativeUrl.TrimEnd('/').LastIndexOf('/');
            return index > 0 ? serverRelativeUrl.Substring(0, index) : "/";
        }

        /// <summary>
        /// Refreshes the file's checkout state from the server and checks it out if nobody has.
        /// </summary>
        private async Task EnsureCheckedOutAsync(EditorDocument doc)
        {
            var current = await _spService.GetFileAsync(doc.ServerRelativeUrl);
            doc.Item.CheckOutType = current.CheckOutType;
            doc.Item.CheckedOutByUser = current.CheckedOutByUser;

            if (current.CheckOutType == 2)
            {
                LogRequested?.Invoke($"Auto-checking out: {doc.ServerRelativeUrl}");
                await _spService.CheckOutAsync(doc.ServerRelativeUrl);
                doc.Item.CheckOutType = 0;
                doc.CheckOutStatus = "Checked Out";
            }
        }

        /// <summary>
        /// SharePoint refuses to Publish() a checked-out file, so a checked-out file is
        /// checked in as a major version instead; a checked-in draft is published.
        /// </summary>
        private async Task PublishMajorAsync(EditorDocument doc, string comment)
        {
            var current = await _spService.GetFileAsync(doc.ServerRelativeUrl);

            if (current.CheckOutType != 2)
            {
                LogRequested?.Invoke($"Checking in as major version: {doc.ServerRelativeUrl} ('{comment}')");
                await _spService.CheckInAsync(doc.ServerRelativeUrl, 1, comment);
            }
            else
            {
                LogRequested?.Invoke($"Publishing major version: {doc.ServerRelativeUrl} ('{comment}')");
                await _spService.PublishAsync(doc.ServerRelativeUrl, comment);
            }

            doc.Item.CheckOutType = 2;
            doc.Item.CheckedOutByUser = string.Empty;
            doc.CheckOutStatus = "Checked In (Published)";
        }

        [RelayCommand]
        public async Task CheckInActiveDocumentAsync()
        {
            if (ActiveDocument == null) return;
            var doc = ActiveDocument;

            var dlg = new CheckInDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            if (doc.IsDirty && !await SaveDocumentAsync(doc))
            {
                return;
            }

            IsBusy = true;
            try
            {
                LogRequested?.Invoke($"Checking in: {doc.ServerRelativeUrl} (Type: {dlg.CheckInType}, Comment: '{dlg.Comment}')");
                await _spService.CheckInAsync(doc.ServerRelativeUrl, dlg.CheckInType, dlg.Comment);
                doc.Item.CheckOutType = 2;
                doc.Item.CheckedOutByUser = string.Empty;
                doc.CheckOutStatus = "Checked In";
                StatusMessage = $"Checked in {doc.FileName}.";
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Check-in failed for {doc.FileName}: {ex.Message}");
                MessageBox.Show($"Check-in failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task PublishActiveDocumentAsync()
        {
            if (ActiveDocument == null) return;
            var doc = ActiveDocument;

            var dlg = new CheckInDialog(publishMode: true) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            if (doc.IsDirty && !await SaveDocumentAsync(doc))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await PublishMajorAsync(doc, dlg.Comment);
                StatusMessage = $"Published {doc.FileName} successfully.";
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Publish failed for {doc.FileName}: {ex.Message}");
                MessageBox.Show($"Publish failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task UndoCheckOutActiveDocumentAsync()
        {
            if (ActiveDocument == null) return;
            var doc = ActiveDocument;

            var res = MessageBox.Show($"Discard checkout for '{doc.FileName}'? Any unsaved server changes will be reverted.", "Confirm Discard", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            IsBusy = true;
            try
            {
                LogRequested?.Invoke($"Discarding checkout: {doc.ServerRelativeUrl}");
                await _spService.UndoCheckOutAsync(doc.ServerRelativeUrl);
                doc.Item.CheckOutType = 2;
                doc.Item.CheckedOutByUser = string.Empty;
                doc.CheckOutStatus = "Checked In";

                // Reload the reverted server content into the open tab
                var bytes = await _spService.DownloadFileAsync(doc.ServerRelativeUrl);
                doc.ReplaceContent(bytes);
                RefreshPreviewRequested?.Invoke();

                StatusMessage = $"Reverted checkout for {doc.FileName}.";
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke($"[ERROR] Undo checkout failed for {doc.FileName}: {ex.Message}");
                MessageBox.Show($"Discard checkout failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task CloseDocumentAsync(EditorDocument? doc)
        {
            doc ??= ActiveDocument;
            if (doc == null) return;

            if (doc.IsDirty)
            {
                var res = MessageBox.Show($"Save changes to '{doc.FileName}' before closing?", "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel) return;
                if (res == MessageBoxResult.Yes && !await SaveDocumentAsync(doc))
                {
                    return; // Keep the tab open so the edits are not lost
                }
            }

            OpenDocuments.Remove(doc);
            if (ActiveDocument == doc)
            {
                ActiveDocument = OpenDocuments.LastOrDefault();
            }
        }

        public async Task<bool> CloseAllDocumentsAsync(bool promptIfDirty = true)
        {
            if (promptIfDirty)
            {
                var dirtyDocs = OpenDocuments.Where(d => d.IsDirty).ToList();
                if (dirtyDocs.Any())
                {
                    var names = string.Join("\n• ", dirtyDocs.Select(d => d.FileName));
                    var res = MessageBox.Show(
                        $"The following file(s) have unsaved changes in the editor:\n• {names}\n\nDo you want to save them before changing the connection?",
                        "Unsaved Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning);

                    if (res == MessageBoxResult.Cancel)
                    {
                        return false;
                    }

                    if (res == MessageBoxResult.Yes)
                    {
                        var failed = new List<string>();
                        foreach (var d in dirtyDocs)
                        {
                            if (await SaveDocumentAsync(d, showErrors: false))
                            {
                                LogRequested?.Invoke($"Saved {d.FileName} prior to session reset.");
                            }
                            else
                            {
                                failed.Add(d.FileName);
                            }
                        }

                        if (failed.Any())
                        {
                            var discard = MessageBox.Show(
                                $"These file(s) could not be saved:\n• {string.Join("\n• ", failed)}\n\nDiscard their changes and continue?",
                                "Save Failed",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);
                            if (discard != MessageBoxResult.Yes)
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            OpenDocuments.Clear();
            ActiveDocument = null;
            StatusMessage = "No files open.";
            IsFindReplaceVisible = false;
            RefreshPreviewRequested?.Invoke();
            return true;
        }

        [RelayCommand]
        public void InsertSnippet(SharePointSnippet? snippet)
        {
            if (snippet == null) snippet = SelectedSnippet;
            if (snippet == null || string.IsNullOrEmpty(snippet.SnippetCode)) return;

            InsertSnippetRequested?.Invoke(snippet.SnippetCode);
            LogRequested?.Invoke($"Inserted snippet: {snippet.Name}");
        }

        [RelayCommand]
        public void ToggleFindReplace()
        {
            IsFindReplaceVisible = !IsFindReplaceVisible;
        }

        [RelayCommand]
        public void SetViewMode(string mode)
        {
            if (int.TryParse(mode, out var m))
            {
                ViewMode = m;
                if (m == 1 || m == 2)
                {
                    RefreshPreviewRequested?.Invoke();
                }
            }
        }
    }
}
