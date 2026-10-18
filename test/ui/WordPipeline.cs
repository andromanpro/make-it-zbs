using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    internal static class WordPipeline
    {
        internal static void RunIfRequested(object wordObject, object documentObject, object contentObject, IntPtr source, int sourcePid, string output,
            string clipboardMarker, Action<int> allowClipboardOwner, Action restoreClipboard)
        {
            var root = Environment.GetEnvironmentVariable("ZBS_WORD_PIPELINE_APP");
            if (string.IsNullOrEmpty(root)) return;
            dynamic word = wordObject, document = documentObject, content = contentObject;
            try
            {
                using (Mutex.OpenExisting(@"Local\MakeItZBS-SingleInstance")) Assert.Fail("Stop the normal Make it ZBS before the isolated pipeline check");
            }
            catch (WaitHandleCannotBeOpenedException) { /* No other application owns this session. */ }
            Assert.That(KeyIsAvailable(), Is.True, "Ctrl+Shift+F24 is already in use");
            var info = new ProcessStartInfo(Path.Combine(root, "runtime/bin/oscript.exe"),
                "-encoding=utf-8 " + OwnedProcessRunner.QuoteArgument(Path.Combine(root, "src/onescript/zbs-run.os")))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            var profile = Path.Combine(root, "webview-pipeline");
            info.EnvironmentVariables["WEBVIEW2_USER_DATA_FOLDER"] = profile;
            var sync = new object();
            var applied = false;
            using (var log = new StreamWriter(Path.Combine(output, "word-pipeline-app.log")) { AutoFlush = true })
            using (var app = Process.Start(info))
            {
                allowClipboardOwner(app.Id);
                DataReceivedEventHandler write = (sender, args) =>
                {
                    if (args.Data == null) return;
                    lock (sync)
                    {
                        log.WriteLine(args.Data);
                        if (args.Data.Contains("[zbs.main]") && args.Data.Contains("Replace-in-place выполнен")) applied = true;
                    }
                };
                app.OutputDataReceived += write;
                app.ErrorDataReceived += write;
                app.BeginOutputReadLine();
                app.BeginErrorReadLine();
                dynamic selection = null;
                try
                {
                    Wait(() => !KeyIsAvailable(), app, "OneScript did not register the hotkey", 30000);
                    const string rough = "мы проверили пилот интерфейс работает но где выбирать модель непонятно пожалуйста напишите понятнее";
                    const string prefix = "PREFIX ";
                    const string suffix = " SUFFIX";
                    var original = prefix + rough + suffix;
                    content.Text = original;
                    word.Activate();
                    selection = document.Range(prefix.Length, prefix.Length + rough.Length);
                    selection.Select();
                    word.ScreenRefresh();
                    Wait(() => NativeMethods.GetForegroundWindow() == source, app, "Disposable Word document did not acquire focus", 10000);
                    SendHotkey(source, sourcePid);
                    AutomationElement preview = null;
                    Wait(() => ReadUi(() => (preview = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                        new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                            new PropertyCondition(AutomationElement.NameProperty, "Предпросмотр Make it ZBS")))) != null),
                        app, "OneScript did not display its real Preview", 180000);
                    AutomationElement apply = null;
                    Wait(() => ReadUi(() => (apply = preview.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "applyButton"))) != null
                        && apply.Current.IsEnabled && !apply.Current.IsOffscreen), app, "Preview did not offer verified replacement", 20000);
                    Assert.That(((string)content.Text).TrimEnd('\r', '\n'), Is.EqualTo(original), "Request must not apply before confirmation");
                    Assert.That(WordUiTests.ClipboardMarkerRestored(clipboardMarker, output), Is.True,
                        "The full capture pipeline must preserve the clipboard marker");
                    var capture = new ScreenCapture();
                    var previewPath = Path.Combine(output, "word-pipeline-preview.png");
                    capture.CaptureWindow(previewPath, ReadUi(() => preview.Current.NativeWindowHandle), app.Id);
                    TestContext.AddTestAttachment(previewPath);
                    ReadUi(() => { ((InvokePattern)apply.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return true; });
                    Wait(() => ((string)content.Text).TrimEnd('\r', '\n') != original, app, "Confirmed Preview did not change Word", 20000);
                    var result = ((string)content.Text).TrimEnd('\r', '\n');
                    Assert.That(result, Does.StartWith(prefix).And.EndWith(suffix), "Pipeline changed text outside the original selection");
                    Wait(() => { lock (sync) return applied; }, app, "OneScript did not confirm completed replacement", 10000);
                    Assert.That(WordUiTests.ClipboardMarkerRestored(clipboardMarker, output), Is.True,
                        "The full replacement pipeline must preserve the clipboard marker");
                    document.Undo();
                    Wait(() => ((string)content.Text).TrimEnd('\r', '\n') == original, app, "Word Undo did not restore the pipeline source", 10000);
                    Assert.That(Directory.Exists(profile), Is.True, "WebView2 did not use the isolated profile");
                    var backups = Path.Combine(root, "logs/backups");
                    var files = Directory.GetFiles(backups);
                    Assert.That(files.Length, Is.EqualTo(1), "Only one completed backup is expected, without plaintext or temporary files");
                    var store = ReplacementBackupStore.Constructor();
                    Assert.That(Path.GetExtension(files[0]), Is.EqualTo(".zbsbackup"));
                    Assert.That(store.Read(files[0]), Is.EqualTo(rough), "The real OneScript route must preserve the exact selection through DPAPI");
                    Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(files[0])), Does.Not.Contain(rough));
                    VerifyRecoveryTool(root, files[0], rough);
                    File.WriteAllText(Path.Combine(output, "word-pipeline-result.json"), new JObject
                    {
                        ["status"] = "passed", ["appPid"] = app.Id, ["sourcePid"] = sourcePid,
                        ["context"] = "Word", ["unchangedBeforeApply"] = true, ["selectionOnly"] = true,
                        ["applicationConfirmedApplied"] = true, ["undo"] = true, ["result"] = result, ["webViewProfile"] = profile,
                        ["backupEncrypted"] = true, ["backupExactSelection"] = true, ["recoveryToolVerified"] = true,
                        ["clipboardRestored"] = true
                    }.ToString());
                }
                finally
                {
                    if (selection != null && Marshal.IsComObject(selection)) Marshal.ReleaseComObject(selection);
                    try { WordUiTests.RunClient(() => { restoreClipboard(); return true; }); }
                    finally
                    {
                        if (!app.HasExited) { app.Kill(); app.WaitForExit(5000); }
                        app.WaitForExit();
                    }
                }
            }
        }

        private static T ReadUi<T>(Func<T> read) => WordUiTests.RunClient(() => UiaDispatcher.Shared.Run(read));

        private static void VerifyRecoveryTool(string root, string backup, string expected)
        {
            var destination = Path.Combine(root, "explicit-test-recovery.txt");
            var arguments = "-encoding=utf-8 " + OwnedProcessRunner.QuoteArgument(Path.Combine(root, "src/onescript/restore-backup.os"))
                + " " + OwnedProcessRunner.QuoteArgument(backup) + " " + OwnedProcessRunner.QuoteArgument(destination);
            try
            {
                RunRecovery(root, arguments, 0);
                Assert.That(File.ReadAllText(destination, Encoding.UTF8), Is.EqualTo(expected));
                File.WriteAllText(destination, "existing output must survive");
                RunRecovery(root, arguments, 1);
                Assert.That(File.ReadAllText(destination), Is.EqualTo("existing output must survive"));
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        private static void RunRecovery(string root, string arguments, int expectedCode)
        {
            using (var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "runtime/bin/oscript.exe"), arguments)
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                try
                {
                    Assert.That(WordUiTests.RunClient(() => process.WaitForExit(15000)), Is.True, "Recovery tool timed out");
                    Assert.That(process.ExitCode, Is.EqualTo(expectedCode), "Recovery tool result");
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            }
        }

        private static bool KeyIsAvailable()
        {
            if (!NativeMethods.RegisterHotKey(IntPtr.Zero, 901, 0x4006, 0x87)) return false;
            NativeMethods.UnregisterHotKey(IntPtr.Zero, 901);
            return true;
        }

        private static void SendHotkey(IntPtr window, int processId)
        {
            Assert.That(NativeMethods.GetForegroundWindow(), Is.EqualTo(window));
            NativeMethods.GetWindowThreadProcessId(window, out var actualPid);
            Assert.That(actualPid, Is.EqualTo(processId));
            NativeMethods.Input Input(ushort key, bool up) => new NativeMethods.Input { type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KeyboardInput { wVk = key, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } } };
            var release = new[] { Input(0x87, true), Input(0x10, true), Input(0x11, true) };
            try
            {
                var keys = new[] { Input(0x11, false), Input(0x10, false), Input(0x87, false), release[0], release[1], release[2] };
                Assert.That(NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(NativeMethods.Input))), Is.EqualTo(keys.Length));
            }
            finally { NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(NativeMethods.Input))); }
        }

        private static void Wait(Func<bool> condition, Process app, string message, int timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeout)
            {
                Assert.That(app.HasExited, Is.False, "Isolated application exited; inspect word-pipeline-app.log");
                Application.DoEvents();
                if (condition()) return;
                Thread.Sleep(50);
            }
            Assert.Fail(message);
        }
    }
}
