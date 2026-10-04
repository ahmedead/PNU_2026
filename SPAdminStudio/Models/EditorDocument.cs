using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace SPAdminStudio.Models
{
    public class EditorDocument : INotifyPropertyChanged
    {
        private bool _isDirty;
        private string _checkOutStatus = "Checked In";
        private string _statusMessage = string.Empty;

        public SharePointItem Item { get; }
        public string FileName => Item.Name;
        public string ServerRelativeUrl => Item.ServerRelativeUrl;
        public string Syntax { get; set; } = "HTML";
        public TextDocument Document { get; }

        public string TabHeader => IsDirty ? $"{FileName} *" : FileName;

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (_isDirty != value)
                {
                    _isDirty = value;
                    OnPropertyChanged(nameof(IsDirty));
                    OnPropertyChanged(nameof(TabHeader));
                }
            }
        }

        public string CheckOutStatus
        {
            get => _checkOutStatus;
            set
            {
                _checkOutStatus = value;
                OnPropertyChanged(nameof(CheckOutStatus));
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }

        /// <summary>Encoding the file was read with; used again on save so bytes round-trip.</summary>
        public Encoding TextEncoding { get; private set; } = new UTF8Encoding(false);
        public string EncodingDisplay => TextEncoding is UTF8Encoding
            ? (TextEncoding.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8")
            : TextEncoding.WebName.ToUpperInvariant();

        private static readonly string[] TextExtensions =
        {
            ".master", ".aspx", ".ascx", ".html", ".htm", ".css", ".js", ".json", ".xml", ".xsl", ".xslt",
            ".txt", ".svg", ".less", ".scss", ".map", ".webpart", ".dwp", ".config", ".resx", ".md", ".preview"
        };

        public static bool IsTextFile(string fileName) =>
            TextExtensions.Contains(Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant());

        public EditorDocument(SharePointItem item, byte[] content)
            : this(item, Decode(content, out var encoding))
        {
            TextEncoding = encoding;
        }

        public byte[] Encode()
        {
            var preamble = TextEncoding.GetPreamble();
            var body = TextEncoding.GetBytes(Document.Text);
            if (preamble.Length == 0) return body;

            var result = new byte[preamble.Length + body.Length];
            preamble.CopyTo(result, 0);
            body.CopyTo(result, preamble.Length);
            return result;
        }

        public void ReplaceContent(byte[] content)
        {
            Document.Text = Decode(content, out var encoding);
            TextEncoding = encoding;
            OnPropertyChanged(nameof(EncodingDisplay));
            MarkSaved();
        }

        /// <summary>
        /// Detects BOM / valid UTF-8; falls back to the system ANSI code page (e.g. Windows-1256)
        /// so legacy files are not corrupted on save.
        /// </summary>
        private static string Decode(byte[] bytes, out Encoding encoding)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                return encoding.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(false, true);
                return encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(true, true);
                return encoding.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                var text = strictUtf8.GetString(bytes);
                encoding = new UTF8Encoding(false);
                return text;
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
                return encoding.GetString(bytes);
            }
        }

        public EditorDocument(SharePointItem item, string initialContent)
        {
            Item = item;
            Document = new TextDocument(initialContent ?? string.Empty);
            CheckOutStatus = item.CheckOutStatusDisplay;

            Document.TextChanged += (s, e) =>
            {
                IsDirty = true;
            };
        }

        public void MarkSaved()
        {
            IsDirty = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class SharePointSnippet
    {
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SnippetCode { get; set; } = string.Empty;
    }
}
