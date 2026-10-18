using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ZBSWindowsComponent
{
    internal sealed class ResultFallbackForm : Form
    {
        private readonly WorkOperation _operation;
        private readonly RichTextBox _result;
        private readonly string _text;
        private readonly string _language;
        private CancellationTokenRegistration _cancellation;
        internal bool CanCopy => !IsDisposed && _operation?.Token.IsCancellationRequested != true;
        internal string ResultText => _text;
        internal bool IsReadOnly => _result.ReadOnly;

        internal ResultFallbackForm(string result, string notice, WorkOperation operation)
        {
            _operation = operation;
            _text = result ?? string.Empty;
            _language = UiText.CurrentLanguage();
            DpiAwareness.ConfigureForm(this);
            Text = L("Результат Make it ZBS", "Make it ZBS Result");
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(480, 320);
            Font = SystemFonts.MessageBoxFont;
            BackColor = Color.FromArgb(17, 20, 24);
            ForeColor = Color.WhiteSmoke;
            if (IconResources.App != null) Icon = IconResources.App;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = notice ?? string.Empty, AutoSize = true, Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 0, 12) }, 0, 0);
            _result = new RichTextBox { Text = _text, ReadOnly = true, Dock = DockStyle.Fill,
                WordWrap = false, DetectUrls = false, HideSelection = false, BackColor = Color.FromArgb(24, 28, 32),
                ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.FixedSingle, Font = new Font(FontFamily.GenericMonospace, 11) };
            layout.Controls.Add(_result, 0, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Name = "closeResult", Text = L("Закрыть", "Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
            var copy = new Button { Name = "copyResult", Text = L("Копировать", "Copy"), AutoSize = true };
            copy.Click += (sender, args) => CopyResult();
            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
            CancelButton = close;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_operation != null)
                _cancellation = _operation.Token.Register(() =>
                {
                    try { if (IsHandleCreated) BeginInvoke((Action)Close); }
                    catch (InvalidOperationException) { /* The form is already closing. */ }
                });
            if (!CanCopy) Close();
        }

        internal bool CopyResult()
        {
            if (!CanCopy || _text.Length == 0) return false;
            try
            {
                return ClipboardDispatcher.Shared.Run(() =>
                {
                    if (!CanCopy) return false;
                    // RichEdit normalizes newlines after handle creation; copy the original result.
                    Clipboard.SetText(_text);
                    return true;
                });
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(this, L("Буфер обмена занят. Попробуйте ещё раз.", "The clipboard is busy. Try again."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cancellation.Dispose();
            base.Dispose(disposing);
        }

        private string L(string ru, string en) => UiText.Get(_language, ru, en);
    }
}
