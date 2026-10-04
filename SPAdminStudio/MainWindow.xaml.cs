using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Highlighting;
using SPAdminStudio.Models;
using SPAdminStudio.ViewModels;

namespace SPAdminStudio
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            _vm.CodeViewerVM.PropertyChanged += CodeViewerVM_PropertyChanged;
            _vm.CodeViewerVM.InsertSnippetRequested += OnInsertSnippetRequested;
            _vm.CodeViewerVM.RefreshPreviewRequested += OnRefreshPreviewRequested;

            CodeEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;

            HookGroupedGridLayoutFix(ExplorerListsGrid);
            HookGroupedGridLayoutFix(ExplorerContentTypesGrid);
            HookGroupedGridLayoutFix(ExplorerFieldsGrid);

            _vm.DeploymentVM.PropertyChanged += DeploymentVM_PropertyChanged;

            Loaded += MainWindow_Loaded;
            KeyDown += MainWindow_KeyDown;
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_vm.SelectedTabIndex == MainViewModel.TabEditor && _vm.CodeViewerVM.ActiveDocument != null)
                {
                    _ = _vm.CodeViewerVM.SaveActiveDocumentAsync();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_vm.SelectedTabIndex == MainViewModel.TabEditor)
                {
                    _vm.CodeViewerVM.ToggleFindReplace();
                    e.Handled = true;
                }
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_vm.ConnectionVM.PlainPassword))
            {
                TxtPassword.Password = _vm.ConnectionVM.PlainPassword;
            }

            _vm.ConnectionVM.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(_vm.ConnectionVM.PlainPassword))
                {
                    if (TxtPassword.Password != _vm.ConnectionVM.PlainPassword)
                    {
                        TxtPassword.Password = _vm.ConnectionVM.PlainPassword ?? "";
                    }
                }
            };
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_vm.ConnectionVM.PlainPassword != TxtPassword.Password)
            {
                _vm.ConnectionVM.PlainPassword = TxtPassword.Password;
            }
        }

        private void CodeViewerVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_vm.CodeViewerVM.ActiveDocument))
            {
                SwitchActiveDocument();
            }
            else if (e.PropertyName == nameof(_vm.CodeViewerVM.ViewMode))
            {
                UpdateViewModeLayout();
            }
        }

        private void SwitchActiveDocument()
        {
            var doc = _vm.CodeViewerVM.ActiveDocument;
            if (doc != null)
            {
                CodeEditor.Document = doc.Document;
                try
                {
                    CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(doc.Syntax);
                }
                catch
                {
                    CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("HTML");
                }
                OnRefreshPreviewRequested();
            }
            else
            {
                CodeEditor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
                try
                {
                    PreviewBrowser.NavigateToString("about:blank");
                }
                catch { }
            }
        }

        private void UpdateViewModeLayout()
        {
            switch (_vm.CodeViewerVM.ViewMode)
            {
                case 0: // Code only
                    ColEditor.Width = new GridLength(1, GridUnitType.Star);
                    ColSplitter.Width = new GridLength(0);
                    SplitterControl.Visibility = Visibility.Collapsed;
                    ColPreview.Width = new GridLength(0);
                    break;
                case 1: // Split
                    ColEditor.Width = new GridLength(1, GridUnitType.Star);
                    ColSplitter.Width = new GridLength(5);
                    SplitterControl.Visibility = Visibility.Visible;
                    ColPreview.Width = new GridLength(1, GridUnitType.Star);
                    OnRefreshPreviewRequested();
                    break;
                case 2: // Preview only
                    ColEditor.Width = new GridLength(0);
                    ColSplitter.Width = new GridLength(0);
                    SplitterControl.Visibility = Visibility.Collapsed;
                    ColPreview.Width = new GridLength(1, GridUnitType.Star);
                    OnRefreshPreviewRequested();
                    break;
            }
        }

        private void Caret_PositionChanged(object? sender, EventArgs e)
        {
            _vm.CodeViewerVM.CurrentLine = CodeEditor.TextArea.Caret.Line;
            _vm.CodeViewerVM.CurrentColumn = CodeEditor.TextArea.Caret.Column;
        }

        private void OnInsertSnippetRequested(string snippet)
        {
            if (string.IsNullOrEmpty(snippet)) return;
            CodeEditor.Document.Insert(CodeEditor.CaretOffset, snippet);
        }

        private void OnRefreshPreviewRequested()
        {
            if (_vm.CodeViewerVM.ActiveDocument == null) return;
            if (_vm.CodeViewerVM.ViewMode == 0) return;

            try
            {
                var rawText = _vm.CodeViewerVM.ActiveDocument.Document.Text;
                var fileName = _vm.CodeViewerVM.ActiveDocument.FileName;

                string htmlToRender;
                if (fileName.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                {
                    htmlToRender = $"<!DOCTYPE html><html><head><style>{rawText}</style></head><body style=\"font-family:Segoe UI, sans-serif; padding:20px;\"><h2>CSS Preview</h2><p class=\"lead\">Sample content applying CSS rules.</p><button>Sample Button</button></body></html>";
                }
                else if (fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    htmlToRender = $"<!DOCTYPE html><html><head><script>{rawText}</script></head><body style=\"font-family:Segoe UI, sans-serif; padding:20px;\"><h2>JavaScript Preview</h2><p>Script loaded in preview frame.</p></body></html>";
                }
                else
                {
                    // Clean ASPX directives for HTML rendering
                    var cleanHtml = Regex.Replace(rawText, @"<%@\s*.*?\s*%>", "", RegexOptions.Singleline);
                    cleanHtml = Regex.Replace(cleanHtml, @"<asp:.*?>", "<div style=\"border:1px dashed #007ACC; padding:6px; margin:4px 0;\">[ASP.NET Control]</div>", RegexOptions.IgnoreCase);
                    cleanHtml = Regex.Replace(cleanHtml, @"</asp:.*?>", "", RegexOptions.IgnoreCase);
                    cleanHtml = Regex.Replace(cleanHtml, @"<SharePoint:.*?>", "<span style=\"color:#007ACC;\">[SharePoint Tag]</span>", RegexOptions.IgnoreCase);
                    htmlToRender = cleanHtml;
                }

                PreviewBrowser.NavigateToString(htmlToRender);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview render exception: {ex.Message}");
            }
        }

        private void DocumentTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is EditorDocument doc)
            {
                _vm.CodeViewerVM.ActiveDocument = doc;
            }
        }

        private void BtnCloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is EditorDocument doc)
            {
                _ = _vm.CodeViewerVM.CloseDocumentAsync(doc);
            }
        }

        private void SnippetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            _vm.CodeViewerVM.InsertSnippet(null);
        }

        private void BtnFindNext_Click(object sender, RoutedEventArgs e)
        {
            var textToFind = _vm.CodeViewerVM.FindText;
            if (string.IsNullOrEmpty(textToFind) || CodeEditor.Document == null) return;

            var comparison = _vm.CodeViewerVM.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var text = CodeEditor.Document.Text;
            var startIndex = CodeEditor.CaretOffset;

            var index = text.IndexOf(textToFind, startIndex, comparison);
            if (index == -1)
            {
                // Wrap around to start
                index = text.IndexOf(textToFind, 0, comparison);
            }

            if (index != -1)
            {
                CodeEditor.Select(index, textToFind.Length);
                CodeEditor.ScrollTo(CodeEditor.TextArea.Caret.Line, CodeEditor.TextArea.Caret.Column);
            }
            else
            {
                MessageBox.Show($"String '{textToFind}' not found.", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnReplace_Click(object sender, RoutedEventArgs e)
        {
            var textToFind = _vm.CodeViewerVM.FindText;
            var replaceWith = _vm.CodeViewerVM.ReplaceText ?? "";
            if (string.IsNullOrEmpty(textToFind) || CodeEditor.Document == null) return;

            if (CodeEditor.SelectionLength > 0 && CodeEditor.SelectedText.Equals(textToFind, _vm.CodeViewerVM.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                CodeEditor.Document.Replace(CodeEditor.SelectionStart, CodeEditor.SelectionLength, replaceWith);
            }
            BtnFindNext_Click(sender, e);
        }

        private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            var textToFind = _vm.CodeViewerVM.FindText;
            var replaceWith = _vm.CodeViewerVM.ReplaceText ?? "";
            if (string.IsNullOrEmpty(textToFind) || CodeEditor.Document == null) return;

            var pattern = Regex.Escape(textToFind);
            var options = _vm.CodeViewerVM.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            var count = Regex.Matches(CodeEditor.Document.Text, pattern, options).Count;

            CodeEditor.Document.Text = Regex.Replace(CodeEditor.Document.Text, pattern, replaceWith, options);
            MessageBox.Show($"Replaced {count} occurrence(s).", "Replace All", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MasterPageRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is SharePointItem item)
            {
                _vm.OpenItemInEditor(item);
                e.Handled = true;
            }
        }

        private void StyleLibraryRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is SharePointItem item)
            {
                if (item.IsFolder)
                {
                    _ = _vm.StyleLibraryVM.NavigateIntoFolderAsync(item);
                }
                else
                {
                    _vm.OpenItemInEditor(item);
                }
                e.Handled = true;
            }
        }


        private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is SharePointFolder folder)
            {
                await _vm.StyleLibraryVM.SelectFolderAsync(folder);
            }
        }

        private void MasterPageSelectAll_Checked(object sender, RoutedEventArgs e)
        {
            _vm.MasterPageVM.SelectAll(true);
        }

        private void MasterPageSelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            _vm.MasterPageVM.SelectAll(false);
        }

        private void StyleLibrarySelectAll_Checked(object sender, RoutedEventArgs e)
        {
            _vm.StyleLibraryVM.SelectAll(true);
        }

        private void StyleLibrarySelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            _vm.StyleLibraryVM.SelectAll(false);
        }

        // ---------------- Deploy to Production ----------------

        private void TargetPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_vm.DeploymentVM.TargetPassword != TargetPasswordBox.Password)
            {
                _vm.DeploymentVM.TargetPassword = TargetPasswordBox.Password;
            }
        }

        private void DeploymentVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // PasswordBox cannot be data-bound: push the saved (decrypted) profile password into it
            if (e.PropertyName == nameof(DeploymentViewModel.TargetPassword) &&
                TargetPasswordBox.Password != _vm.DeploymentVM.TargetPassword)
            {
                TargetPasswordBox.Password = _vm.DeploymentVM.TargetPassword ?? string.Empty;
            }
        }

        private void DeploySourceRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is SharePointItem item)
            {
                _ = _vm.DeploymentVM.OpenSourceItemAsync(item);
                e.Handled = true;
            }
        }

        // ---------------- Site Explorer ----------------

        /// <summary>
        /// WPF DataGrid bug: a grouped grid that is first shown with no rows loses its column
        /// headers and collapses every column when rows arrive later. Re-applying each column's
        /// width after the rows change (or the grid becomes visible) forces a correct re-measure.
        /// </summary>
        private void HookGroupedGridLayoutFix(DataGrid grid)
        {
            void Relayout() => Dispatcher.BeginInvoke(() =>
            {
                foreach (var column in grid.Columns)
                {
                    var width = column.Width; // keeps any width the user dragged to
                    column.Width = 0;
                    column.Width = width;
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            grid.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Relayout(); };
            ((System.Collections.Specialized.INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Relayout();
        }

        private void SiteSectionsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Clicking the already-selected section (e.g. "Lists and Libraries" while viewing a list's items) returns to it
            _vm.SiteExplorerVM.ReturnToSelectedSection();
        }

        private void SiteListRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is SPListInfo list)
            {
                _ = _vm.SiteExplorerVM.OpenListAsync(list);
                e.Handled = true;
            }
        }

        private void SubsiteRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is SPWebInfo web)
            {
                _ = _vm.SiteExplorerVM.OpenSubsiteAsync(web);
                e.Handled = true;
            }
        }

        private void SiteFileRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is SharePointItem item)
            {
                _ = _vm.SiteExplorerVM.OpenFileItemAsync(item);
                e.Handled = true;
            }
        }

        private void SiteFilesSelectAll_Checked(object sender, RoutedEventArgs e) => SetSiteFilesSelection(true);

        private void SiteFilesSelectAll_Unchecked(object sender, RoutedEventArgs e) => SetSiteFilesSelection(false);

        private void SetSiteFilesSelection(bool selected)
        {
            // Only the rows visible under the current filter
            foreach (var item in _vm.SiteExplorerVM.FilesView.Cast<SharePointItem>())
            {
                item.IsSelected = selected;
            }
        }

        private void BtnSetMaster_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.DataContext = DataContext;
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join(Environment.NewLine, _vm.LogEntries);
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                MessageBox.Show("Activity log copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}