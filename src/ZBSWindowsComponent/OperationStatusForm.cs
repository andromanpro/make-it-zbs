using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ZBSWindowsComponent
{
    internal sealed class OperationStatusForm : Form
    {
        private const int NoActivate = 0x08000000;
        private const int ToolWindow = 0x00000080;
        private readonly Label _message;
        private readonly ProgressBar _progress;
        private readonly Timer _timer;
        private bool _working;
        private WorkOperation _operation;
        private readonly Stopwatch _fallbackClock = new Stopwatch();
        private readonly Func<TimeSpan> _elapsed;
        private readonly Label _elapsedLabel;
        private string _elapsedText = "00:00";
        private bool _layingOut;
        internal string ElapsedText => _elapsedText;

        public OperationStatusForm() : this(null) { }

        internal OperationStatusForm(Func<TimeSpan> elapsed)
        {
            _elapsed = elapsed ?? (() => _operation?.Elapsed ?? _fallbackClock.Elapsed);
            // This compact borderless surface uses monitor-derived geometry.
            // net472 can otherwise repeatedly rescale its labels in UIA callbacks.
            AutoScaleMode = AutoScaleMode.None;
            Text = "Make it ZBS";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(380, 112);
            Padding = new Padding(16);
            BackColor = Color.FromArgb(26, 31, 38);
            ForeColor = Color.FromArgb(229, 240, 255);
            Font = SystemFonts.MessageBoxFont;
            _elapsedLabel = new Label { Name = "operationElapsed", Text = "Make it ZBS  |  00:00",
                AccessibleName = UiText.Current("Время обработки", "Processing time") };
            _message = new Label { Name = "operationMessage", AutoEllipsis = true };
            _progress = new ProgressBar { Style = ProgressBarStyle.Marquee };
            Controls.Add(_message);
            Controls.Add(_progress);
            Controls.Add(_elapsedLabel);
            _timer = new Timer { Interval = 250 };
            _timer.Tick += Timer_Tick;
            DpiChanged += (_, __) => ApplyDpiLayout();
        }

        protected override bool ShowWithoutActivation => true;

        protected override void CreateHandle()
        {
            using (new ScreenCapture.PhysicalPixelScope()) base.CreateHandle();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NoActivate | ToolWindow;
                return parameters;
            }
        }

        public void ShowBusy(string message)
        {
            if (!_working || !ReferenceEquals(_operation, Operations.Active))
            {
                _operation = Operations.Active;
                _fallbackClock.Restart();
            }
            _working = true;
            _progress.Visible = true;
            _message.Text = message;
            UpdateElapsed();
            _timer.Interval = 250;
            ShowStatus();
        }

        public void ShowReady(string message)
        {
            _working = false;
            _fallbackClock.Stop();
            UpdateElapsed();
            _progress.Visible = false;
            _message.Text = string.IsNullOrEmpty(message)
                ? UiText.Current("Ответ готов", "Response ready") : message;
            _timer.Interval = 5000;
            ShowStatus();
        }

        internal void HideStatus() { _timer.Stop(); Hide(); }

        private void ShowStatus()
        {
            using (new ScreenCapture.PhysicalPixelScope())
            {
                var area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
                if (!Visible) Show();
                ApplyDpiLayout();
                Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
                // Setting Form.TopMost before the first Show activates it on net472.
                // Native z-order placement explicitly preserves the source focus.
                NativeMethods.SetWindowPos(Handle, new IntPtr(-1), Location.X, Location.Y,
                    0, 0, 0x0001 | 0x0010 | 0x0040);
            }
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            RefreshStatus();
        }

        internal void RefreshStatus()
        {
            ApplyDpiLayout();
            if (_working)
            {
                UpdateElapsed();
                if (!Operations.IsBusy) ShowReady(UiText.Current("Обработка завершена", "Processing finished"));
                return;
            }
            _timer.Stop();
            Hide();
        }

        private void ApplyDpiLayout()
        {
            if (_layingOut || !IsHandleCreated) return;
            _layingOut = true;
            try
            {
                var dpi = DpiAwareness.GetWindowDpi(this);
                int Pixel(int value) => (int)Math.Round(value * dpi / 96.0);
                ClientSize = DpiAwareness.ScaleLogicalSize(new Size(380, 112), dpi);
                Padding = new Padding(Pixel(16));
                _elapsedLabel.Bounds = new Rectangle(Pixel(16), Pixel(16), Pixel(348), Pixel(24));
                _message.Bounds = new Rectangle(Pixel(16), Pixel(40), Pixel(348), Pixel(48));
                _progress.Bounds = new Rectangle(Pixel(16), Pixel(88), Pixel(348), Pixel(8));
            }
            finally { _layingOut = false; }
        }

        private void UpdateElapsed()
        {
            _elapsedText = OperationFeedbackOptions.FormatElapsed(_elapsed());
            _elapsedLabel.Text = "Make it ZBS  |  " + _elapsedText;
            _elapsedLabel.AccessibleName = UiText.Current("Время обработки: ", "Processing time: ") + _elapsedText;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
