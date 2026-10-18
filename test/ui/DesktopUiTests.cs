using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using MakeItZBS.Capture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ScriptEngine.Machine;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    [TestFixture, NonParallelizable]
    public class DesktopUiTests
    {
        private Process _host;
        private string _statePath;
        private string _output;
        private AutomationElement _fixture;
        private IntPtr _previousDpi;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);

        [SetUp]
        public void StartOwnedFixture()
        {
            if (!Environment.UserInteractive) Assert.Fail("UI tests require an unlocked interactive desktop.");
            _previousDpi = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
            var root = Environment.GetEnvironmentVariable("ZBS_UI_ARTIFACT_DIR");
            var executable = Environment.GetEnvironmentVariable("ZBS_UI_HOST_PATH");
            Assert.That(root, Is.Not.Null.And.Not.Empty, "Run through tools/run-ui-tests.py");
            Assert.That(File.Exists(executable), Is.True);
            _output = Path.Combine(root, TestContext.CurrentContext.Test.Name.Replace('"', '_').Replace('(', '_').Replace(')', '_'));
            Directory.CreateDirectory(_output);
            _statePath = Path.Combine(_output, "fixture.json");
            _host = Process.Start(new ProcessStartInfo(executable, OwnedProcessRunner.QuoteArgument(_statePath))
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)
            });
            WaitUntil(() => File.Exists(_statePath), "Fixture did not start");
            var state = State();
            Assert.That(state.Value<int>("processId"), Is.EqualTo(_host.Id));
            _fixture = AutomationElement.FromHandle(new IntPtr(state.Value<long>("window")));
            Assert.That(_fixture.Current.ProcessId, Is.EqualTo(_host.Id));
            _fixture.SetFocus();
            WaitUntil(() => GetForegroundWindow().ToInt64() == state.Value<long>("window"), "Fixture could not acquire focus");
        }

        [TearDown]
        public void CloseOnlyOwnedFixture()
        {
            try
            {
                if (_host != null && !_host.HasExited)
                {
                    _host.CloseMainWindow();
                    if (!_host.WaitForExit(5000)) { _host.Kill(); _host.WaitForExit(3000); }
                }
            }
            finally
            {
                _host?.Dispose(); _host = null;
                if (_previousDpi != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(_previousDpi);
            }
        }

        [TestCase(CaptureBackend.Dxgi)]
        [TestCase(CaptureBackend.Gdi)]
        [TestCase(CaptureBackend.Auto)]
        public void CaptureContainsExpectedPixelsAndAChangedFrame(CaptureBackend selected)
        {
            var state = State();
            var first = CaptureFixture(state, selected, "step-0");
            AssertPixel(first.Path, RelativeCenter(state, "red"), Color.Red);
            AssertPixel(first.Path, RelativeCenter(state, "green"), Color.Lime);
            if (selected != CaptureBackend.Auto)
                Assert.That(first.Metadata.Backend, Is.EqualTo(selected.ToString().ToLowerInvariant()));
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "advance"));
            WaitUntil(() => State().Value<int>("step") == 1, "UIA button invocation did not advance the fixture");
            var counter = Find(_fixture, AutomationElement.AutomationIdProperty, "counter");
            Assert.That(counter.Current.Name, Is.EqualTo("Step 1"));
            state = State();
            var second = CaptureFixture(state, selected, "step-1");
            AssertPixel(second.Path, RelativeCenter(state, "red"), Color.Blue);
            AssertPixel(second.Path, RelativeCenter(state, "green"), Color.Lime);
            Assert.That(second.Metadata.CapturedAtUtc, Is.GreaterThanOrEqualTo(first.Metadata.CapturedAtUtc));
        }

        [Test]
        public void ImagePreviewSavesOriginalPngThroughNativeDialog()
        {
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "openImagePreview"));
            WaitUntil(() => State().Value<long>("imagePreview") != 0, "Image Preview did not open");
            var preview = AutomationElement.FromHandle(new IntPtr(State().Value<long>("imagePreview")));
            Invoke(Find(preview, AutomationElement.NameProperty, "Сохранить..."));
            AutomationElement dialog = null;
            WaitUntil(() => (dialog = P0AppSession.Handles(_host.Id).Select(AutomationElement.FromHandle).FirstOrDefault(candidate =>
                candidate.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "1001")) != null)) != null,
                "Native Save dialog did not open");
            var controls = dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Where(e => e.Current.ControlType == ControlType.Edit || e.Current.ControlType == ControlType.Button)
                .Select(e => new { e.Current.Name, e.Current.AutomationId, e.Current.ClassName,
                    type = e.Current.ControlType.ProgrammaticName, patterns = e.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray() });
            File.WriteAllText(Path.Combine(_output, "save-dialog-tree.json"), JsonConvert.SerializeObject(controls, Formatting.Indented));
            var destination = Path.Combine(_output, "saved-preview.png");
            ((ValuePattern)P0AppSession.DialogControl(dialog, "1001", ControlType.Edit).GetCurrentPattern(ValuePattern.Pattern)).SetValue(destination);
            Invoke(P0AppSession.DialogControl(dialog, "1", ControlType.Button));
            try { WaitUntil(() => File.Exists(destination), "Image Preview did not save its PNG"); }
            catch
            {
                var foreground = GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
                if (pid == _host.Id)
                    new ScreenCapture().CaptureWindow(Path.Combine(_output, "save-dialog-failure.png"), foreground.ToInt32(), _host.Id);
                throw;
            }
            WaitUntil(() => State().Value<string>("imageOutcome") == "saved", "Image Preview did not return saved");
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(File.ReadAllBytes(Path.Combine(_output, "fixture-image.png"))));
        }

        [Test]
        public void OneCFullModuleWaitsForSourceEditorFocus()
        {
            var button = Find(_fixture, AutomationElement.AutomationIdProperty, "delayedEditorFocus");
            P0AppSession.Invoke(button, true);
            Assert.That(AutomationElement.FocusedElement.Current.ControlType, Is.EqualTo(ControlType.Button));
            var window = new IntPtr(State().Value<long>("window"));
            var result = new WindowInfo().GetOneCModuleFullText(window.ToInt32(), _host.Id, _fixture.Current.Name);
            Assert.That(result, Is.EqualTo("delayed source module"));
            Assert.That(GetForegroundWindow(), Is.EqualTo(window));
        }

        [Test]
        public void OneCFullModuleRefusesNonEditorWithoutStealingFocus()
        {
            P0AppSession.Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "advance"), true);
            var window = new IntPtr(State().Value<long>("window"));
            var info = new WindowInfo();
            Assert.That(info.GetOneCModuleFullText(window.ToInt32(), _host.Id, _fixture.Current.Name), Is.Empty);
            Assert.That(info.GetLastCaptureDiagnostic(), Does.Contain("TextPattern"));
            Assert.That(GetForegroundWindow(), Is.EqualTo(window));
        }

        [Test]
        public void ProductionScreenCaptureUsesOwnedWorkerAndReportsItsBackend()
        {
            var state = State();
            var path = Path.Combine(_output, "production-window.png");
            var capture = new ScreenCapture();
            capture.CaptureWindow(path, checked((int)state.Value<long>("window")), _host.Id);
            var metadata = JsonConvert.DeserializeObject<CaptureMetadata>(capture.GetLastCaptureMetadata());
            Assert.That(metadata.Backend, Is.EqualTo("dxgi").Or.EqualTo("gdi"));
            Assert.That(metadata.ProcessId, Is.EqualTo(_host.Id));
            Assert.That(metadata.WindowHandle, Is.EqualTo(state.Value<long>("window")));
            File.WriteAllText(Path.Combine(_output, "production-window.json"), capture.GetLastCaptureMetadata());
        }

        [Test]
        public void SettingsRenderWithOwnTaskbarWindowAndCancelDoesNotSaveChanges()
        {
            var config = ProjectPaths.ConfigDir;
            var before = Directory.GetFiles(config).ToDictionary(Path.GetFileName, File.ReadAllBytes);
            var settings = OpenSettings();
            Assert.That(GetWindowLong(new IntPtr(settings.Current.NativeWindowHandle), -20) & 0x80, Is.Zero,
                "Settings must not be a tool window hidden from the taskbar");
            Assert.That(GetWindow(new IntPtr(settings.Current.NativeWindowHandle), 4), Is.EqualTo(IntPtr.Zero),
                "Settings must be an independent top-level window");
            var background = WaitForElement(settings, AutomationElement.NameProperty, "Фоновый режим", ControlType.CheckBox);
            var toggle = (TogglePattern)background.GetCurrentPattern(TogglePattern.Pattern);
            var original = toggle.Current.ToggleState;
            toggle.Toggle();
            WaitUntil(() => ((TogglePattern)Find(settings, AutomationElement.NameProperty, "Фоновый режим", ControlType.CheckBox)
                .GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != original, "Checkbox did not change");
            CaptureSettings(settings, "settings-edited");
            Invoke(WaitForElement(settings, AutomationElement.NameProperty, "Отмена", ControlType.Button));
            WaitUntil(() => State().Value<long>("settings") == 0, "Cancel did not hide settings");
            foreach (var file in before)
                Assert.That(File.ReadAllBytes(Path.Combine(config, file.Key)), Is.EqualTo(file.Value), file.Key);
            settings = OpenSettings();
            background = WaitForElement(settings, AutomationElement.NameProperty, "Фоновый режим", ControlType.CheckBox);
            Assert.That(((TogglePattern)background.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState, Is.EqualTo(original));
        }

        [Test]
        public void SettingsRemainUsableAfterResizeAndSectionNavigation()
        {
            var settings = OpenSettings();
            var transform = (TransformPattern)settings.GetCurrentPattern(TransformPattern.Pattern);
            Assert.That(transform.Current.CanResize, Is.True);
            var dpi = State().Value<int>("settingsDpi");
            var expectedWidth = Math.Max(800, (int)Math.Round(720 * dpi / 96.0));
            transform.Resize(expectedWidth, Math.Max(600, 520 * dpi / 96.0));
            WaitUntil(() => Math.Abs(settings.Current.BoundingRectangle.Width - expectedWidth) <= 10, "Settings did not resize");
            var about = WaitForElement(settings, AutomationElement.NameProperty, "Об авторе", ControlType.Hyperlink);
            Invoke(about);
            var telegram = WaitForElement(settings, AutomationElement.NameProperty, "Открыть личный Telegram автора", ControlType.Button);
            WaitUntil(() => !telegram.Current.IsOffscreen, "Section navigation did not reveal author links");
            var cancel = WaitForElement(settings, AutomationElement.NameProperty, "Отмена", ControlType.Button);
            Assert.That(cancel.Current.IsOffscreen, Is.False, "Footer must stay visible at the minimum size");
            CaptureSettings(settings, "settings-800x600-about");
            Invoke(cancel);
        }

        [Test]
        public void StatusPopupDoesNotStealTheSourceFocus()
        {
            var foreground = GetForegroundWindow();
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "showStatus"));
            WaitUntil(() => State().Value<long>("status") != 0, "Status popup was not shown");
            var status = new IntPtr(State().Value<long>("status"));
            AssertSourceForeground(foreground, "status-shown");
            Assert.That(GetWindowLong(status, -20) & 0x08000080, Is.EqualTo(0x08000080));
            WaitUntil(() => State().Value<string>("statusElapsed") != "00:00", "Visible operation clock did not advance");
            var statusElement = AutomationElement.FromHandle(status);
            var bounds = statusElement.Current.BoundingRectangle;
            foreach (var name in new[] { "operationElapsed" })
            {
                var label = Find(statusElement, AutomationElement.AutomationIdProperty, name);
                Assert.That(label.Current.IsOffscreen, Is.False);
                Assert.That(bounds.Contains(label.Current.BoundingRectangle), Is.True, name + " is clipped by the popup");
                Assert.That(label.Current.BoundingRectangle.Width, Is.GreaterThan(70), name + " is too narrow to read");
            }
            var path = Path.Combine(_output, "status-with-elapsed.png");
            DesktopCaptureClient.Capture(new CaptureRequest
            {
                Bounds = new Rectangle((int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height)
            }, path, CancellationToken.None, () =>
            {
                Assert.That(statusElement.Current.ProcessId, Is.EqualTo(_host.Id));
                AssertSourceForeground(foreground, "capture-admitted");
                Assert.That(State().Value<long>("status"), Is.EqualTo(status.ToInt64()));
            });
            TestContext.AddTestAttachment(path);
            AssertSourceForeground(foreground, "capture-completed");
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "finishStatus"));
            WaitUntil(() => !State().Value<bool>("operationBusy"), "Operation did not finish");
            AssertSourceForeground(foreground, "status-ready");
            var last = State().Value<string>("statusElapsed");
            Thread.Sleep(1200);
            Assert.That(State().Value<string>("statusElapsed"), Is.EqualTo(last), "Completed clock must stop");
            AssertSourceForeground(foreground, "clock-stopped");
        }

        private void AssertSourceForeground(IntPtr expected, string phase)
        {
            var actual = GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(actual, out var processId);
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(actual, className, className.Capacity);
            var observation = new { phase, expected = expected.ToInt64(), actual = actual.ToInt64(),
                processId, hostProcessId = _host.Id, windowClass = className.ToString() };
            File.AppendAllText(Path.Combine(_output, "focus-trace.jsonl"), JsonConvert.SerializeObject(observation) + Environment.NewLine);
            Assert.That(actual, Is.EqualTo(expected), "Source focus changed at " + phase + ": " + JsonConvert.SerializeObject(observation));
        }

        [Test]
        public void SettingsSaveAppliesCapturedHotkeyAndFeedbackDespiteDeniedAutostart()
        {
            var settings = OpenSettings();
            var config = ProjectPaths.ConfigDir;
            var before = Directory.GetFiles(config).ToDictionary(Path.GetFileName, File.ReadAllBytes);
            try
            {
                var capture = Find(settings, AutomationElement.AutomationIdProperty, "hotkeyCaptureButton");
                P0AppSession.Invoke(capture, true);
                WaitUntil(() => Find(settings, AutomationElement.AutomationIdProperty, "hotkeyCaptureButton").Current.Name == "Отмена",
                    "Settings hotkey recorder did not enter capture mode");
                var hotkey = Find(settings, AutomationElement.AutomationIdProperty, "hotkeyInput");
                WaitUntil(() => hotkey.Current.HasKeyboardFocus, "Hotkey input did not receive keyboard focus");
                File.WriteAllText(Path.Combine(_output, "hotkey-input-target.json"), JsonConvert.SerializeObject(new
                {
                    foreground = GetForegroundWindow().ToInt64(), settings = settings.Current.NativeWindowHandle,
                    inputHasKeyboardFocus = hotkey.Current.HasKeyboardFocus
                }, Formatting.Indented));
                PressSettingsKeys(settings, 0x11, 0x10, 0x86);
                try
                {
                    WaitUntil(() => ((ValuePattern)hotkey.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "Ctrl+Shift+F23",
                        "Settings hotkey recorder did not capture actual keys");
                }
                catch
                {
                    try { CaptureSettings(settings, "hotkey-capture-failure"); }
                    catch (Exception error) { TestContext.Error.WriteLine("Hotkey diagnostic capture failed: " + error.Message); }
                    throw;
                }
                var mode = Find(settings, AutomationElement.AutomationIdProperty, "operationFeedbackSelect");
                P0AppSession.Invoke(mode, true);
                PressSettingsKeys(settings, 0x23); // End selects the last native HTML option (none).
                PressSettingsKeys(settings, 0x0D);
                CaptureSettings(settings, "feedback-mode-edited");
                var version = State().Value<int>("settingsVersion");
                Invoke(WaitForElement(settings, AutomationElement.NameProperty, "Сохранить", ControlType.Button));
                WaitUntil(() => State().Value<string>("settingsOutcome") == "saved" && State().Value<long>("settings") == 0,
                    "Denied registry write prevented the normal saved outcome");
                Assert.That(State().Value<string>("saveWarning"), Does.Contain("Controlled registry denial"));
                Assert.That(State().Value<int>("settingsVersion"), Is.GreaterThan(version));
                var saved = JObject.Parse(File.ReadAllText(Path.Combine(config, "settings.json")));
                Assert.That((string)saved["general"]["hotkey"], Is.EqualTo("Ctrl+Shift+F23"));
                Assert.That((string)saved["general"]["operationFeedback"], Is.EqualTo("none"));
            }
            finally
            {
                foreach (var entry in before) File.WriteAllBytes(Path.Combine(config, entry.Key), entry.Value);
            }
        }

        private void PressSettingsKeys(AutomationElement settings, params ushort[] keys)
        {
            Assert.That(settings.Current.ProcessId, Is.EqualTo(_host.Id));
            Assert.That(GetForegroundWindow().ToInt64(), Is.EqualTo(settings.Current.NativeWindowHandle));
            var inputs = keys.Select(key => new NativeMethods.Input { type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KeyboardInput { wVk = key } } }).ToList();
            inputs.AddRange(keys.AsEnumerable().Reverse().Select(key => new NativeMethods.Input { type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KeyboardInput { wVk = key, dwFlags = NativeMethods.KEYEVENTF_KEYUP } } }));
            try
            {
                Assert.That(NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(NativeMethods.Input))), Is.EqualTo(inputs.Count));
            }
            finally
            {
                var release = inputs.Skip(keys.Length).ToArray();
                NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(NativeMethods.Input)));
            }
        }

        [Test]
        public void NativeGlobalHotkeyWorksAfterUnregisterAndReregister()
        {
            using (var poller = new HotkeyPoller(new HotkeyManager(() => null)))
            {
                poller.Register("Ctrl+Shift+F24");
                _fixture.SetFocus();
                PressOwnedHotkey(0x87);
                Assert.That(poller.WaitForPressedKey(3000), Is.EqualTo("Ctrl+Shift+F24"));
                poller.ClearQueue(); // The completed operation releases the admission latch.
                poller.Unregister();
                poller.Register("Ctrl+Shift+F23");
                PressOwnedHotkey(0x87);
                Assert.That(poller.WaitForPressedKey(300), Is.Empty, "Old key is no longer registered");
                PressOwnedHotkey(0x86);
                Assert.That(poller.WaitForPressedKey(3000), Is.EqualTo("Ctrl+Shift+F23"));
            }
        }

        [Test]
        public void SelectionReplacementChangesOnlyCapturedRangeAndCannotBeReplayed()
        {
            PrepareSelection();
            var window = new WindowInfo();
            var handle = checked((int)State().Value<long>("window"));
            Assert.That(window.GetSelectedText(handle, _host.Id), Is.EqualTo("selected"));
            var token = window.CaptureReplacement(handle, _host.Id, "selection", "selected");
            Assert.That(token, Is.Not.Empty, window.GetLastCaptureDiagnostic());
            Assert.That(PasteSucceeded(window.ReplaceCapturedText(token, "improved")), Is.True, window.GetLastCaptureDiagnostic());
            WaitUntil(() => State().Value<string>("editorText") == "before improved after", "Wrong replacement range");
            Assert.That(PasteSucceeded(window.ReplaceCapturedText(token, "replayed")), Is.False);
            CaptureFixture(State(), CaptureBackend.Auto, "replaced-selection");
        }

        [Test]
        public void ChangedDocumentRejectsAPreviouslyCapturedReplacement()
        {
            PrepareSelection();
            var window = new WindowInfo();
            var handle = checked((int)State().Value<long>("window"));
            var token = window.CaptureReplacement(handle, _host.Id, "selection", "selected");
            Assert.That(token, Is.Not.Empty, window.GetLastCaptureDiagnostic());
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "mutateDocument"));
            WaitUntil(() => State().Value<string>("editorText") == "before modified after", "Fixture did not change");
            Assert.That(PasteSucceeded(window.ReplaceCapturedText(token, "must-not-appear")), Is.False);
            Assert.That(State().Value<string>("editorText"), Is.EqualTo("before modified after"));
        }

        [Test]
        public void WrongProcessIdCannotCaptureOrSendTextToAnOtherwiseValidWindow()
        {
            PrepareSelection();
            var window = new WindowInfo();
            var handle = checked((int)State().Value<long>("window"));
            Assert.That(() => window.GetSelectedText(handle, Process.GetCurrentProcess().Id), Throws.TypeOf<RuntimeException>());
            Assert.That(PasteSucceeded(window.InsertAiChatDraft(handle, Process.GetCurrentProcess().Id, "must-not-appear")), Is.False);
            Assert.That(State().Value<string>("editorText"), Is.EqualTo("before selected after"));
        }

        [TestCase(false), TestCase(true)]
        public void ChatDeliveryToOwnedEditorSeparatesDraftFromEnterSubmission(bool submit)
        {
            PrepareSelection();
            var window = new WindowInfo();
            var handle = checked((int)State().Value<long>("window"));
            var result = submit ? window.SendAiChatMessage(handle, _host.Id, "NEXT")
                : window.InsertAiChatDraft(handle, _host.Id, "NEXT");
            Assert.That(PasteSucceeded(result), Is.True, window.GetLastCaptureDiagnostic());
            WaitUntil(() => State().Value<string>("editorText").Contains("NEXT"), "Draft did not reach the owned editor");
            if (submit) WaitUntil(() => State().Value<int>("editorEnterCount") == 1, "Submission did not send Enter");
            else Assert.That(State().Value<int>("editorEnterCount"), Is.Zero, "Draft mode must never send Enter");
        }

        [Test]
        public void ClosedSourceCannotPasteIntoAnotherRemainingApplicationWindow()
        {
            PrepareSelection();
            var window = new WindowInfo();
            var handle = checked((int)State().Value<long>("window"));
            var token = window.CaptureReplacement(handle, _host.Id, "selection", "selected");
            Assert.That(token, Is.Not.Empty, window.GetLastCaptureDiagnostic());
            var remainingState = Path.Combine(_output, "remaining-fixture.json");
            using (var remaining = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ZBS_UI_HOST_PATH"),
                OwnedProcessRunner.QuoteArgument(remainingState)) { UseShellExecute = false }))
            {
                try
                {
                    WaitUntil(() => File.Exists(remainingState), "Remaining fixture did not start");
                    var state = ReadState(remainingState, remaining);
                    var element = AutomationElement.FromHandle(new IntPtr(state.Value<long>("window")));
                    Assert.That(element.Current.ProcessId, Is.EqualTo(remaining.Id));
                    element.SetFocus();
                    Invoke(Find(element, AutomationElement.AutomationIdProperty, "prepareSelection"));
                    WaitUntil(() => ReadState(remainingState, remaining).Value<string>("editorText") == "before selected after",
                        "Remaining editor did not become ready");
                    _host.CloseMainWindow();
                    Assert.That(_host.WaitForExit(5000), Is.True, "Source fixture did not close");
                    Assert.That(GetForegroundWindow().ToInt64(), Is.EqualTo(state.Value<long>("window")));
                    Assert.That(PasteSucceeded(window.InsertAiChatDraft(handle, _host.Id, "must-not-appear")), Is.False);
                    Assert.That(PasteSucceeded(window.ReplaceCapturedText(token, "must-not-appear")), Is.False);
                    state = ReadState(remainingState, remaining);
                    Assert.That(state.Value<string>("editorText"), Is.EqualTo("before selected after"));
                    Assert.That(state.Value<int>("editorEnterCount"), Is.Zero);
                }
                finally
                {
                    if (!remaining.HasExited) { remaining.CloseMainWindow(); if (!remaining.WaitForExit(5000)) { remaining.Kill(); remaining.WaitForExit(3000); } }
                }
            }
        }

        [Test]
        public void NativeClipboardRoundTripPreservesTextRichTextAndBitmapAndRejectsAChangedSequence()
        {
            using (var original = new ClipboardTestScope())
            {
                ClipboardDispatcher.Shared.Run(() =>
                {
                    using (var bitmap = new Bitmap(8, 8))
                    {
                        bitmap.SetPixel(3, 3, Color.Red);
                        var data = new System.Windows.Forms.DataObject();
                        data.SetText("ZBS-marker", System.Windows.Forms.TextDataFormat.UnicodeText);
                        data.SetData(System.Windows.Forms.DataFormats.Rtf, false, @"{\rtf1\ansi fixture}");
                        data.SetImage(bitmap);
                        System.Windows.Forms.Clipboard.SetDataObject(data, true);
                        using (var captured = ClipboardTestScope.Capture())
                        {
                            System.Windows.Forms.Clipboard.SetText("temporary");
                            var sequence = NativeMethods.GetClipboardSequenceNumber();
                            captured.Restore(sequence);
                            Assert.That(System.Windows.Forms.Clipboard.GetText(), Is.EqualTo("ZBS-marker"));
                            Assert.That(System.Windows.Forms.Clipboard.GetData(System.Windows.Forms.DataFormats.Rtf), Is.EqualTo(@"{\rtf1\ansi fixture}"));
                            using (var image = new Bitmap(System.Windows.Forms.Clipboard.GetImage()))
                                Assert.That(image.GetPixel(3, 3).ToArgb(), Is.EqualTo(Color.Red.ToArgb()));
                            Assert.Throws<InvalidOperationException>(() => captured.Restore(sequence));
                            Assert.That(System.Windows.Forms.Clipboard.GetText(), Is.EqualTo("ZBS-marker"));
                        }
                    }
                    return true;
                });
            }
        }

        [Test, Category("LiveCodex")]
        public void SignedInCodexReturnsTransformedSyntheticTextAndCodeWithoutOpenRouter()
        {
            var status = CodexCliClient.CheckStatus(false);
            Assert.That(status.Installed && status.Authenticated && status.UsesChatGptSubscription, Is.True, status.Diagnostic);
            var text = CodexCliClient.Execute("Improve the Russian text, preserving all facts and dates. Return only the improved text.",
                "мы проверили пилот. два клиента не нашли выбор модели. срок 15 августа. бюджет не менялся.", "", 120, "", "low");
            Assert.That(text.Success, Is.True, text.Diagnostic);
            Assert.That(text.Content, Does.Contain("15").And.Contain("августа"));
            File.WriteAllText(Path.Combine(_output, "text-result.txt"), text.Content);
            var code = CodexCliClient.Execute("Fix the JavaScript bug. Return only the function, no markdown. Preserve its name.",
                "function calculateOrderTotal(items) { let total=0; for(let index=0; index<=items.length; index++) { total += items[index].price * items[index].quantity; } return total; }", "", 120, "", "low");
            Assert.That(code.Success, Is.True, code.Diagnostic);
            Assert.That(code.Content, Does.Contain("calculateOrderTotal"));
            Assert.That(code.Content.Replace(" ", ""), Does.Not.Contain("index<=items.length"));
            File.WriteAllText(Path.Combine(_output, "code-result.js"), code.Content);
        }

        [Test, Category("LiveCodex")]
        public void SignedInCodexRecognizesColorsInIndependentFixtureCapture()
        {
            var captured = CaptureFixture(State(), CaptureBackend.Auto, "codex-vision-input");
            var result = CodexCliClient.Execute("Describe only the two large colored rectangles in the image. Ignore all text and buttons. Reply with the two English color names.",
                "Name the two rectangle colors.", "", 120, captured.Path, "low");
            Assert.That(result.Success, Is.True, result.Diagnostic);
            Assert.That(result.Content.ToLowerInvariant(), Does.Contain("red").And.Contain("green"));
            File.WriteAllText(Path.Combine(_output, "vision-result.txt"), result.Content);
        }

        [Test, Category("LiveImage")]
        public void SignedInCodexCanGenerateAnImageFromBundledPaintSketch()
        {
            var source = Path.Combine(Environment.GetEnvironmentVariable("ZBS_SMOKE_SAMPLES"), "paint-cabin-sketch.png");
            var path = Path.Combine(_output, "photorealistic-cabin.png");
            var result = (IRuntimeContextInstance)new CodexCliClient().GenerateImage(
                "Create a photorealistic image from this rough drawing, preserving its composition: cabin, lake, mountains, sun and two pine trees.",
                "Improve the synthetic sketch into a realistic photograph. Return the generated artifact path only.", source, path, "", 240, "low");
            var diagnostic = result.GetPropValue(result.FindProperty("Диагностика")).AsString();
            File.WriteAllText(Path.Combine(_output, "generation-diagnostic.txt"), diagnostic);
            Assert.That(result.GetPropValue(result.FindProperty("Успех")).AsBoolean(), Is.True, diagnostic);
            using (var image = new Bitmap(path)) Assert.That(image.Width * image.Height, Is.GreaterThan(10000));
            Assert.That(File.ReadAllBytes(path), Is.Not.EqualTo(File.ReadAllBytes(source)), "The input must not be returned as the generated result");
            TestContext.AddTestAttachment(path);
        }

        private void PrepareSelection()
        {
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "prepareSelection"));
            WaitUntil(() => State().Value<string>("editorText") == "before selected after", "Sample was not prepared");
        }

        private static bool PasteSucceeded(IValue value)
        {
            var result = (IRuntimeContextInstance)value;
            return result.GetPropValue(result.FindProperty("Успех")).AsBoolean();
        }

        private void PressOwnedHotkey(ushort key)
        {
            Assert.That(GetForegroundWindow().ToInt64(), Is.EqualTo(State().Value<long>("window")));
            var keys = new ushort[] { 0x11, 0x10, key };
            NativeMethods.Input Input(ushort vk, bool up) => new NativeMethods.Input
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KeyboardInput { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } }
            };
            try
            {
                var inputs = keys.Select(k => Input(k, false)).Concat(keys.AsEnumerable().Reverse().Select(k => Input(k, true))).ToArray();
                Assert.That(NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()), Is.EqualTo(inputs.Length));
            }
            finally
            {
                var release = keys.AsEnumerable().Reverse().Select(k => Input(k, true)).ToArray();
                NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf<NativeMethods.Input>());
            }
        }

        private AutomationElement OpenSettings()
        {
            _fixture.SetFocus();
            Invoke(Find(_fixture, AutomationElement.AutomationIdProperty, "openSettings"));
            WaitUntil(() => State().Value<long>("settings") != 0 && State().Value<bool>("settingsReady"),
                "Real SettingsWebForm/WebView2 did not initialize", 20000);
            var state = State();
            Assert.That(state.Value<bool>("settingsBroken"), Is.False);
            var settings = AutomationElement.FromHandle(new IntPtr(state.Value<long>("settings")));
            Assert.That(settings.Current.ProcessId, Is.EqualTo(_host.Id));
            var hotkey = WaitForElement(settings, AutomationElement.AutomationIdProperty, "hotkeyInput", ControlType.Edit);
            WaitUntil(() => ((ValuePattern)hotkey.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "Ctrl+F12", "Settings load payload was not rendered");
            Thread.Sleep(300); // NavigationCompleted precedes the queued WebView2 load payload.
            return settings;
        }

        private void CaptureSettings(AutomationElement settings, string name)
        {
            var bounds = State()["settingsClient"].ToObject<Rectangle>();
            var path = Path.Combine(_output, name + ".png");
            var metadata = DesktopCaptureClient.Capture(new CaptureRequest
            {
                Bounds = bounds, WindowHandle = settings.Current.NativeWindowHandle, ProcessId = (uint)_host.Id
            }, path, CancellationToken.None);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));
            TestContext.AddTestAttachment(path);
        }

        private (string Path, CaptureMetadata Metadata) CaptureFixture(JObject state, CaptureBackend backend, string name)
        {
            var request = new CaptureRequest
            {
                Bounds = state["client"].ToObject<Rectangle>(), Backend = backend,
                WindowHandle = state.Value<long>("window"), ProcessId = (uint)_host.Id
            };
            _fixture.SetFocus();
            WaitUntil(() => GetForegroundWindow().ToInt64() == request.WindowHandle, "Capture fixture lost focus");
            var path = Path.Combine(_output, name + ".png");
            var metadata = DesktopCaptureClient.Capture(request, path, CancellationToken.None);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));
            TestContext.AddTestAttachment(path);
            return (path, metadata);
        }

        private static Point RelativeCenter(JObject state, string sample)
        {
            var client = state["client"].ToObject<Rectangle>();
            var panel = state[sample].ToObject<Rectangle>();
            return new Point(panel.X + panel.Width / 2 - client.X, panel.Y + panel.Height / 2 - client.Y);
        }

        private static void AssertPixel(string path, Point point, Color expected)
        {
            using (var bitmap = new Bitmap(path))
            {
                // A software cursor may cover the center; keep a strict color
                // check at spatially separate points instead of widening tolerance.
                var offsets = new[] { Point.Empty, new Point(-40, -30), new Point(40, -30), new Point(-40, 30), new Point(40, 30) };
                var matching = offsets.Count(offset =>
                {
                    var pixel = bitmap.GetPixel(point.X + offset.X, point.Y + offset.Y);
                    return Math.Abs(pixel.R - expected.R) + Math.Abs(pixel.G - expected.G) + Math.Abs(pixel.B - expected.B) < 15;
                });
                Assert.That(matching, Is.GreaterThanOrEqualTo(3), "Wrong or stale capture around " + point);
            }
        }

        private JObject State() => ReadState(_statePath, _host);

        private static JObject ReadState(string path, Process process)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(file)) return JObject.Parse(reader.ReadToEnd());
                }
                catch (IOException) when (attempt < 5 && !process.HasExited)
                {
                    // The owned host replaces this status file on every timer tick.
                    Thread.Sleep(20);
                }
            }
        }

        private static void Invoke(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        private static AutomationElement Find(AutomationElement root, AutomationProperty property, string value, ControlType controlType = null)
        {
            Condition condition = new PropertyCondition(property, value);
            if (controlType != null) condition = new AndCondition(condition, new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
            return root.FindFirst(TreeScope.Descendants, condition);
        }

        private static AutomationElement WaitForElement(AutomationElement root, AutomationProperty property, string value, ControlType controlType)
        {
            AutomationElement result = null;
            WaitUntil(() => (result = Find(root, property, value, controlType)) != null, "Missing UIA element: " + value);
            return result;
        }

        private static void WaitUntil(Func<bool> condition, string message, int timeout = 10000)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeout)
            {
                if (condition()) return;
                Thread.Sleep(100);
            }
            Assert.Fail(message);
        }
    }
}
