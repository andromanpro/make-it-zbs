using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using ZBSWindowsComponent;

namespace MakeItZBS.UiHost
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length != 1) return;
            DpiAwareness.TryEnablePerMonitorV2ForCurrentThread();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FixtureForm(args[0]));
        }
    }

    internal sealed class FixtureForm : Form
    {
        private readonly string _statePath;
        private readonly Timer _timer = new Timer { Interval = 100 };
        private readonly Label _counter;
        private readonly Panel _red;
        private readonly Panel _green;
        private readonly SettingsWebForm _settings;
        private readonly RichTextBox _editor;
        private readonly OperationStatusForm _status = new OperationStatusForm();
        private int _step;
        private int _editorEnterCount;
        private long _statusForegroundBefore;
        private long _statusForegroundAfter;
        private string _saveWarning;
        private ImagePreviewForm _imagePreview;
        private string _imageOutcome;

        internal FixtureForm(string statePath)
        {
            _statePath = statePath;
            DpiAwareness.ConfigureForm(this);
            Text = "Make it ZBS - isolated UI fixture";
            Name = "zbsUiFixture";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 410);
            BackColor = Color.White;
            _counter = new Label { Name = "counter", Text = "Step 0", Location = new Point(20, 15), AutoSize = true };
            Controls.Add(_counter);
            _red = new Panel { Name = "redSample", BackColor = Color.Red, Bounds = new Rectangle(20, 65, 120, 100) };
            _green = new Panel { Name = "greenSample", BackColor = Color.Lime, Bounds = new Rectangle(170, 65, 120, 100) };
            Controls.AddRange(new Control[] { _red, _green });
            var next = new Button { Name = "advance", Text = "Advance", Bounds = new Rectangle(20, 205, 120, 40) };
            next.Click += (_, __) => { _counter.Text = "Step " + ++_step; _red.BackColor = _step % 2 == 0 ? Color.Red : Color.Blue; Publish(); };
            Controls.Add(next);
            _settings = new SettingsWebForm(false, Path.Combine(Path.GetDirectoryName(statePath), "webview"),
                (enabled, launcher) => throw new UnauthorizedAccessException("Controlled registry denial"),
                warning => _saveWarning = warning);
            var open = new Button { Name = "openSettings", Text = "Settings", Bounds = new Rectangle(170, 205, 120, 40) };
            open.Click += (_, __) => _settings.ReloadAndShow();
            Controls.Add(open);
            var status = new Button { Name = "showStatus", Text = "Status", Bounds = new Rectangle(320, 205, 120, 40) };
            status.Click += (_, __) =>
            {
                Operations.Begin();
                _statusForegroundBefore = NativeMethods.GetForegroundWindow().ToInt64();
                _status.ShowBusy("Isolated test operation");
                _statusForegroundAfter = NativeMethods.GetForegroundWindow().ToInt64();
                Publish();
            };
            Controls.Add(status);
            var finish = new Button { Name = "finishStatus", Text = "Finish operation", Bounds = new Rectangle(20, 360, 145, 35) };
            finish.Click += (_, __) => { Operations.End(); _status.ShowReady("Done"); Publish(); };
            Controls.Add(finish);
            var imagePreview = new Button { Name = "openImagePreview", Text = "Image preview", Bounds = new Rectangle(470, 360, 145, 35) };
            imagePreview.Click += (_, __) =>
            {
                var path = Path.Combine(Path.GetDirectoryName(_statePath), "fixture-image.png");
                using (var bitmap = new Bitmap(64, 64))
                {
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Lime);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                _imagePreview = new ImagePreviewForm(path, "Image", "UI fixture", 180);
                _imagePreview.FormClosed += (sender, eventArgs) =>
                {
                    var closed = (ImagePreviewForm)sender;
                    _imageOutcome = closed.Outcome;
                    _imagePreview = null;
                    closed.Dispose();
                    Publish();
                };
                _imagePreview.Show(this);
            };
            Controls.Add(imagePreview);
            var delayedFocus = new Button { Name = "delayedEditorFocus", Text = "Delayed editor focus", Bounds = new Rectangle(180, 360, 200, 35) };
            delayedFocus.Click += (_, __) =>
            {
                _editor.Text = "delayed source module";
                var timer = new Timer { Interval = 250 };
                timer.Tick += (sender, eventArgs) => { timer.Stop(); timer.Dispose(); _editor.Focus(); };
                timer.Start();
            };
            Controls.Add(delayedFocus);
            var close = new Button { Name = "closeFixture", Text = "Close fixture", Bounds = new Rectangle(470, 205, 145, 40) };
            close.Click += (_, __) => Close();
            Controls.Add(close);
            _editor = new RichTextBox { Name = "sampleEditor", Bounds = new Rectangle(180, 260, 435, 80) };
            _editor.KeyDown += (_, e) => { if (e.KeyCode == Keys.Return) _editorEnterCount++; };
            Controls.Add(_editor);
            var select = new Button { Name = "prepareSelection", Text = "Select sample", Bounds = new Rectangle(20, 260, 145, 35) };
            select.Click += (_, __) => { _editor.Text = "before selected after"; _editor.Focus(); _editor.Select(7, 8); Publish(); };
            Controls.Add(select);
            var mutate = new Button { Name = "mutateDocument", Text = "Change document", Bounds = new Rectangle(20, 305, 145, 35) };
            mutate.Click += (_, __) => { _editor.Text = "before modified after"; _editor.Focus(); _editor.Select(7, 8); Publish(); };
            Controls.Add(mutate);
            _timer.Tick += (_, __) => Publish();
            Shown += (_, __) => { Publish(); _timer.Start(); };
        }

        private void Publish()
        {
            using (new ScreenCapture.PhysicalPixelScope()) PublishPhysicalState();
        }

        private void PublishPhysicalState()
        {
            var json = JsonConvert.SerializeObject(new
            {
                processId = Process.GetCurrentProcess().Id, window = Handle.ToInt64(),
                client = RectangleToScreen(ClientRectangle), red = _red.RectangleToScreen(_red.ClientRectangle),
                green = _green.RectangleToScreen(_green.ClientRectangle), step = _step,
                settings = _settings.Visible ? _settings.Handle.ToInt64() : 0,
                settingsReady = _settings.IsPrewarmed, settingsBroken = _settings.IsBroken,
                status = _status.Visible ? _status.Handle.ToInt64() : 0,
                statusForegroundBefore = _statusForegroundBefore, statusForegroundAfter = _statusForegroundAfter,
                statusElapsed = _status.ElapsedText, settingsOutcome = _settings.Outcome,
                saveWarning = _saveWarning, settingsVersion = SettingsState.Version,
                operationBusy = Operations.IsBusy,
                imagePreview = _imagePreview?.Visible == true ? _imagePreview.Handle.ToInt64() : 0,
                imageOutcome = _imageOutcome,
                statusLayout = _status.Controls.Cast<Control>().Select(control => new
                {
                    type = control.GetType().Name, control.Bounds,
                    children = control.Controls.Cast<Control>().Select(child => new { child.Name, child.Bounds, child.Text })
                }),
                settingsDpi = _settings.IsHandleCreated ? NativeMethods.GetDpiForWindow(_settings.Handle) : 0,
                settingsClient = _settings.Visible ? _settings.RectangleToScreen(_settings.ClientRectangle) : Rectangle.Empty,
                settingsBounds = _settings.Visible ? _settings.Bounds : Rectangle.Empty,
                dpi = DeviceDpi, editorText = _editor.Text, editorEnterCount = _editorEnterCount
            });
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, json);
            if (File.Exists(_statePath)) File.Replace(temporary, _statePath, null);
            else File.Move(temporary, _statePath);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                Operations.End();
                _settings.Shutdown(); _settings.Dispose(); _status.Dispose();
                _imagePreview?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
