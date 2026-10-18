using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Threading;

namespace ZBSWindowsComponent
{
    public class ImagePreviewForm : Form
    {
        private const string CancelledOutcome = "cancelled";
        private readonly string _imagePath;
        private readonly int _timeoutSec;
        private readonly string _language;
        private PictureBox _pictureBox;
        private Image _image;
        private System.Windows.Forms.Timer _autoCloseTimer;
        private bool _closed;
        private bool _loadFailed;
        private WorkOperation _operation;
        private CancellationTokenRegistration _operationCancellation;
        internal bool CanUseResult => !_closed && !_loadFailed && _operation?.Token.IsCancellationRequested != true;

        internal void BindOperation(WorkOperation operation)
        {
            _operation = operation;
            if (operation == null) return;
            _operationCancellation = operation.Token.Register(() =>
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => CloseWith(CancelledOutcome)));
                }
                catch (InvalidOperationException)
                {
                    // The image form can finish closing before the callback is queued.
                }
            });
        }

        public string Outcome { get; private set; }

        public ImagePreviewForm(string imagePath, string context, string model, int timeoutSec)
        {
            _imagePath = imagePath ?? string.Empty;
            _timeoutSec = timeoutSec;
            _language = UiText.CurrentLanguage();
            Outcome = CancelledOutcome;

            InitializeComponent(model ?? string.Empty);
            LoadImage();
        }

        private void InitializeComponent(string model)
        {
            DpiAwareness.ConfigureForm(this);
            Text = "Make it ZBS - " + L("изображение", "image") + " (" + model + ")";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(900, 680);
            MinimumSize = new Size(480, 360);
            BackColor = Color.FromArgb(13, 17, 23);
            Font = SystemFonts.MessageBoxFont;
            KeyPreview = true;
            if (IconResources.App != null)
            {
                Icon = IconResources.App;
            }

            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(13, 17, 23),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                BackColor = Color.FromArgb(13, 17, 23),
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(12),
                WrapContents = false
            };

            var saveButton = CreateButton(L("Сохранить...", "Save..."));
            saveButton.Click += SaveButton_Click;

            var copyButton = CreateButton(L("Копировать", "Copy"));
            copyButton.Click += CopyButton_Click;

            var cancelButton = CreateButton(L("Отмена", "Cancel"));
            cancelButton.Click += CancelButton_Click;

            buttonPanel.Controls.Add(saveButton);
            buttonPanel.Controls.Add(copyButton);
            buttonPanel.Controls.Add(cancelButton);

            CancelButton = cancelButton;

            Controls.Add(_pictureBox);
            Controls.Add(buttonPanel);
            buttonPanel.BringToFront();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.TryEnableDarkTitleBar(Handle);
        }

        private static Button CreateButton(string text)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(104, 34),
                Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(230, 237, 243)
            };

            button.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            return button;
        }

        private void LoadImage()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_imagePath) || !File.Exists(_imagePath))
                {
                    throw new FileNotFoundException(L(
                        "Файл изображения не найден.", "The image file was not found."), _imagePath);
                }

                // new Bitmap(tmp) обязателен и НЕ удалять: Image.FromStream
                // требует, чтобы поток жил всё время жизни Image; копия
                // в Bitmap отвязывает картинку от fs (закрываем без лока
                // файла). Уберёшь копию — GDI+ упадёт при показе _image.
                using (var fs = File.OpenRead(_imagePath))
                using (var tmp = Image.FromStream(fs))
                {
                    _image = new Bitmap(tmp);
                }

                _pictureBox.Image = _image;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                Outcome = CancelledOutcome;
                MessageBox.Show(this, L("Не удалось открыть изображение: ", "Could not open the image: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyInitialDpiLayout();

            if (!CanUseResult)
            {
                BeginInvoke((Action)(() => CloseWith(CancelledOutcome)));
                return;
            }

            StartAutoCloseTimer();
        }

        internal void ApplyInitialDpiLayout()
        {
            DpiAwareness.ApplyInitialFormSize(
                this, new Size(900, 680), new Size(480, 360));
            CenterToScreen();
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (!CanUseResult) return;
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG|*.png";
                dialog.FileName = "improved_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                dialog.AddExtension = true;
                dialog.DefaultExt = "png";
                dialog.OverwritePrompt = true;

                if (dialog.ShowDialog(this) != DialogResult.OK || !CanUseResult)
                {
                    return;
                }

                try
                {
                    File.Copy(_imagePath, dialog.FileName, true);
                    CloseWith("saved");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, L("Не удалось сохранить изображение: ", "Could not save the image: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CopyButton_Click(object sender, EventArgs e)
        {
            if (!CanUseResult) return;
            try
            {
                Clipboard.SetImage(_image);
                CloseWith("copied");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось скопировать изображение: ", "Could not copy the image: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            CloseWith(CancelledOutcome);
        }

        private string L(string russian, string english)
        {
            return UiText.Get(_language, russian, english);
        }

        private void StartAutoCloseTimer()
        {
            if (_timeoutSec <= 0 || _closed || _autoCloseTimer != null)
            {
                return;
            }

            _autoCloseTimer = new System.Windows.Forms.Timer
            {
                Interval = _timeoutSec * 1000
            };
            _autoCloseTimer.Tick += AutoCloseTimer_Tick;
            _autoCloseTimer.Start();
        }

        private void AutoCloseTimer_Tick(object sender, EventArgs e)
        {
            CloseWith("timeout");
        }

        private void CloseWith(string outcome)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Outcome = outcome;
            DisposeAutoCloseTimer();
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closed)
            {
                _closed = true;
                Outcome = CancelledOutcome;
                DisposeAutoCloseTimer();
            }

            base.OnFormClosing(e);
        }

        private void DisposeAutoCloseTimer()
        {
            if (_autoCloseTimer == null)
            {
                return;
            }

            _autoCloseTimer.Tick -= AutoCloseTimer_Tick;
            _autoCloseTimer.Stop();
            _autoCloseTimer.Dispose();
            _autoCloseTimer = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _operationCancellation.Dispose();
                DisposeAutoCloseTimer();

                if (_pictureBox != null && ReferenceEquals(_pictureBox.Image, _image))
                {
                    _pictureBox.Image = null;
                }

                if (_image != null)
                {
                    _image.Dispose();
                    _image = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
