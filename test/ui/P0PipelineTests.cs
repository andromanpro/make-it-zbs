using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    [TestFixture, NonParallelizable, Category("P0Pipeline")]
    public partial class P0PipelineTests
    {
        private string _root, _output;
        private byte[] _settings, _models;
        private IntPtr _previousDpi;
        [SetUp]
        public void Prepare()
        {
            _previousDpi = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
            _root = Environment.GetEnvironmentVariable("ZBS_P0_PIPELINE_APP");
            Assert.That(_root, Is.Not.Null.And.Not.Empty, "Use --p0-pipeline");
            _output = Path.Combine(Environment.GetEnvironmentVariable("ZBS_UI_ARTIFACT_DIR"), TestContext.CurrentContext.Test.Name);
            Directory.CreateDirectory(_output);
            _settings = File.ReadAllBytes(Path.Combine(_root, "config/settings.json"));
            _models = File.ReadAllBytes(Path.Combine(_root, "config/models.json"));
            var settings = JObject.Parse(File.ReadAllText(Path.Combine(_root, "config/settings.json")));
            settings["general"]["hotkey"] = "Ctrl+Shift+F24";
            settings["general"]["codexOfferShown"] = true;
            File.WriteAllText(Path.Combine(_root, "config/settings.json"), settings.ToString());
        }
        [TearDown]
        public void Restore()
        {
            if (_settings != null) File.WriteAllBytes(Path.Combine(_root, "config/settings.json"), _settings);
            if (_models != null) File.WriteAllBytes(Path.Combine(_root, "config/models.json"), _models);
            if (_previousDpi != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(_previousDpi);
        }

        [Test]
        public void FirstLaunchTraySettingsHotkeyReloadAndRealNotepadRoute()
        {
            var path = Path.Combine(_root, "config/settings.json");
            var settings = JObject.Parse(File.ReadAllText(path));
            settings["general"]["codexOfferShown"] = false;
            File.WriteAllText(path, settings.ToString());
            var modelsPath = Path.Combine(_root, "config/models.json");
            var originalModels = JObject.Parse(File.ReadAllText(modelsPath));
            originalModels["contexts"]["Text"]["provider"] = "openrouter";
            File.WriteAllText(modelsPath, originalModels.ToString());
            var modelsBefore = File.ReadAllBytes(modelsPath);
            using (var first = new P0AppSession(_root, Path.Combine(_output, "first-start")))
            {
                first.Wait(() => JObject.Parse(File.ReadAllText(path)).Value<JObject>("general").Value<bool>("codexOfferShown"),
                    "First launch did not persist its one-time Codex offer", 30000);
                Assert.That(File.ReadAllBytes(modelsPath), Is.EqualTo(modelsBefore), "Discovery silently changed the provider");
                var window = first.OpenSettingsFromTray(true);
                first.Snapshot(window, "onboarding-settings.png");
                P0AppSession.Invoke(first.Element(window, null, "Отмена"));
                first.Wait(() => !P0AppSession.Handles(first.Process.Id).Any(h => P0AppSession.WindowTitle(h) == "Настройки Make it ZBS"),
                    "Settings did not close after Cancel");
                first.QuitFromTray();
            }
            var afterFirst = File.ReadAllBytes(path);
            using (var startup = new StartupSnapshot(_root))
            using (var app = new P0AppSession(_root, Path.Combine(_output, "second-start")))
            using (var clipboard = new ClipboardTestScope())
            {
                clipboard.AllowOwner(app.Process.Id);
                Thread.Sleep(1500);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(afterFirst), "The offer was persisted again on the second launch");
                Assert.That(File.ReadAllBytes(modelsPath), Is.EqualTo(modelsBefore));
                var window = app.OpenSettingsFromTray();
                var record = app.Element(window, "hotkeyCaptureButton");
                P0AppSession.Invoke(record);
                app.Wait(() => P0AppSession.Ui(() => record.Current.Name == "Отмена"), "Hotkey capture was not armed");
                Assert.That(new WindowInfo().ActivateWindow(P0AppSession.Ui(() => window.Current.NativeWindowHandle)), Is.True);
                P0AppSession.Keys(new IntPtr(P0AppSession.Ui(() => window.Current.NativeWindowHandle)), app.Process.Id, 0x11, 0x10, 0x86);
                app.Wait(() => P0AppSession.Ui(() => ((ValuePattern)app.Element(window, "hotkeyInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "Ctrl+Shift+F23"),
                    "New shortcut was not captured");
                P0AppSession.Invoke(app.Element(window, null, "Провайдеры ИИ"));
                P0AppSession.Invoke(app.Element(window, null, "Использовать Codex"));
                P0AppSession.Invoke(app.Element(window, null, "Общие"));
                app.Snapshot(window, "changed-shortcut.png");
                P0AppSession.Invoke(app.Element(window, null, "Сохранить"));
                app.Wait(() => P0AppSession.KeyAvailable(0x87) && !P0AppSession.KeyAvailable(0x86), "The real OneScript loop did not replace its registered shortcut", 20000);
                Assert.That(app.Logged("Настройки перезагружены на лету"), Is.True);
                RunEditor(app, Path.Combine(Environment.SystemDirectory, "notepad.exe"),
                    Path.Combine(Environment.GetEnvironmentVariable("ZBS_SMOKE_SAMPLES"), "text-rough-ru.txt"), "Text", 0x86, clipboard);
                app.Result(new JObject { ["status"] = "passed", ["oneTimeOfferPersisted"] = true, ["providerUnchanged"] = true,
                    ["traySettings"] = true, ["hotkeyLiveReload"] = true, ["newHotkeyInvokedText"] = true });
            }
        }

        [Test]
        public void RealVsCodeSelectionCodexDiffAndCancelPreserveTheFile()
        {
            using (var app = new P0AppSession(_root, _output))
            using (var clipboard = new ClipboardTestScope())
            {
                clipboard.AllowOwner(app.Process.Id);
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs/Microsoft VS Code/Code.exe");
                RunEditor(app, executable, Path.Combine(Environment.GetEnvironmentVariable("ZBS_SMOKE_SAMPLES"), "code-buggy.js"), "Code", 0x87, clipboard);
                app.Result(new JObject { ["status"] = "passed", ["context"] = "Code", ["realEditor"] = true,
                    ["realCodex"] = true, ["diff"] = true, ["cancelPreservesOriginal"] = true });
            }
        }

        private void RunEditor(P0AppSession app, string executable, string sample, string context, ushort key, ClipboardTestScope clipboard)
        {
            Assert.That(File.Exists(executable), Is.True, "Editor is not installed: " + executable);
            var file = Path.Combine(_output, Path.GetFileName(sample));
            File.Copy(sample, file);
            var before = File.ReadAllBytes(file);
            var arguments = OwnedProcessRunner.QuoteArgument(file);
            if (context == "Code") arguments = "--user-data-dir " + OwnedProcessRunner.QuoteArgument(Path.Combine(_output, "code-profile"))
                + " --extensions-dir " + OwnedProcessRunner.QuoteArgument(Path.Combine(_output, "code-extensions"))
                + " --new-window --skip-welcome --skip-release-notes --force-renderer-accessibility " + arguments;
            using (var editor = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false }))
            {
                try
                {
                    clipboard.AllowOwner(editor.Id);
                    IntPtr handle = IntPtr.Zero;
                    app.Wait(() => { editor.Refresh(); handle = editor.MainWindowHandle; return handle != IntPtr.Zero; }, "Editor did not show a window", 30000);
                    Thread.Sleep(context == "Code" ? 5000 : 500);
                    editor.Refresh();
                    handle = editor.MainWindowHandle;
                    Assert.That(P0AppSession.WindowTitle(handle), Does.Contain(Path.GetFileName(file)), "Editor startup did not finish opening the owned fixture");
                    Assert.That(new WindowInfo().ActivateWindow(handle.ToInt32()), Is.True);
                    var element = P0AppSession.Ui(() => AutomationElement.FromHandle(handle).FindFirst(TreeScope.Descendants,
                        new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))));
                    Assert.That(element, Is.Not.Null, "No editable work surface exposed by the real editor");
                    P0AppSession.Ui(() => { element.SetFocus(); return true; });
                    var foreground = NativeMethods.GetForegroundWindow();
                    NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
                    File.WriteAllText(Path.Combine(_output, context.ToLowerInvariant() + "-input-target.json"), new JObject
                    { ["source"] = handle.ToInt64(), ["sourcePid"] = editor.Id, ["foreground"] = foreground.ToInt64(), ["foregroundPid"] = foregroundPid }.ToString());
                    P0AppSession.Keys(handle, editor.Id, 0x11, 0x41);
                    Thread.Sleep(200);
                    foreground = NativeMethods.GetForegroundWindow();
                    NativeMethods.GetWindowThreadProcessId(foreground, out foregroundPid);
                    File.WriteAllText(Path.Combine(_output, context.ToLowerInvariant() + "-after-selection.json"), new JObject
                    { ["source"] = handle.ToInt64(), ["sourcePid"] = editor.Id, ["foreground"] = foreground.ToInt64(), ["foregroundPid"] = foregroundPid,
                        ["foregroundProcess"] = foregroundPid == 0 ? "none" : Process.GetProcessById((int)foregroundPid).ProcessName }.ToString());
                    if (foregroundPid == editor.Id && foreground != handle)
                        new ScreenCapture().CaptureWindow(Path.Combine(_output, "editor-secondary-window.png"), foreground.ToInt32(), editor.Id);
                    P0AppSession.Keys(handle, editor.Id, 0x11, 0x10, key);
                    var preview = app.Window("Предпросмотр Make it ZBS", 180000);
                    app.Element(preview, "cancelButton");
                    Assert.That(app.Logged("Контекст: " + context), Is.True, "Wrong source context");
                    app.Snapshot(preview, context.ToLowerInvariant() + "-preview.png");
                    var result = P0AppSession.DocumentText(preview);
                    File.WriteAllText(Path.Combine(_output, context.ToLowerInvariant() + "-result.txt"), result ?? "");
                    if (context == "Code")
                    {
                        Assert.That(result, Does.Contain("calculateOrderTotal"));
                        Assert.That(result.Replace(" ", ""), Does.Not.Contain("index<=items.length"));
                        P0AppSession.Invoke(app.Element(preview, null, "Изменения"));
                        app.Snapshot(preview, "code-diff.png");
                    }
                    else Assert.That(result, Does.Contain("15").And.Contain("августа"));
                    Assert.That(File.ReadAllBytes(file), Is.EqualTo(before), "The file changed before confirmation");
                    P0AppSession.Invoke(app.Element(preview, "cancelButton"));
                    app.Wait(() => app.Logged("Preview закрыт пользователем: cancelled"), "Cancel outcome was not received");
                    Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
                    var remaining = P0AppSession.DocumentText(P0AppSession.Ui(() => AutomationElement.FromHandle(handle)));
                    if (context == "Code") Assert.That(remaining.Replace(" ", ""), Does.Contain("index<=items.length"), "Cancel changed the unsaved editor buffer");
                    else Assert.That(remaining, Does.Contain("мы посмотрели результаты пилота"), "Cancel changed the Notepad buffer");
                    clipboard.Dispose();
                }
                finally
                {
                    if (!editor.HasExited && (!editor.CloseMainWindow() || !editor.WaitForExit(5000))) { editor.Kill(); editor.WaitForExit(5000); }
                }
            }
        }

        [Test]
        public void RealPaintHotkeyCropImageGenerationPreviewAndSave()
        {
            using (var app = new P0AppSession(_root, _output))
            using (var clipboard = new ClipboardTestScope())
            using (var paint = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mspaint.exe"),
                OwnedProcessRunner.QuoteArgument(Path.Combine(Environment.GetEnvironmentVariable("ZBS_SMOKE_SAMPLES"), "paint-cabin-sketch.png"))) { UseShellExecute = false }))
            {
                clipboard.AllowOwner(app.Process.Id);
                try
                {
                    IntPtr handle = IntPtr.Zero;
                    app.Wait(() => { paint.Refresh(); handle = paint.MainWindowHandle; return handle != IntPtr.Zero; }, "Paint did not show its own window");
                    Assert.That(new WindowInfo().ActivateWindow(handle.ToInt32()), Is.True);
                    Thread.Sleep(1500);
                    P0AppSession.Keys(handle, paint.Id, 0x11, 0x10, 0x87);
                    AutomationElement preview = null;
                    app.Wait(() =>
                    {
                        var found = P0AppSession.Handles(app.Process.Id).FirstOrDefault(h => P0AppSession.WindowTitle(h).StartsWith("Make it ZBS - изображение", StringComparison.Ordinal));
                        if (found == IntPtr.Zero) return false;
                        preview = P0AppSession.Ui(() => AutomationElement.FromHandle(found));
                        return true;
                    }, "Image Preview was not displayed", 360000);
                    app.Snapshot(preview, "paint-image-preview.png");
                    P0AppSession.Invoke(app.Element(preview, null, "Сохранить..."));
                    AutomationElement dialog = null;
                    app.Wait(() => P0AppSession.Ui(() => (dialog = P0AppSession.Handles(app.Process.Id)
                        .Select(AutomationElement.FromHandle).FirstOrDefault(candidate => candidate.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "1001")) != null)) != null), "Save dialog did not open");
                    var filename = P0AppSession.DialogControl(dialog, "1001", ControlType.Edit);
                    Assert.That(filename, Is.Not.Null);
                    var destination = Path.Combine(_output, "saved-generated-image.png");
                    P0AppSession.Ui(() => { ((ValuePattern)filename.GetCurrentPattern(ValuePattern.Pattern)).SetValue(destination); return true; });
                    P0AppSession.Invoke(P0AppSession.DialogControl(dialog, "1", ControlType.Button));
                    app.Wait(() => File.Exists(destination), "Image Preview did not save a PNG");
                    app.Wait(() => !P0AppSession.Handles(app.Process.Id).Any(h => P0AppSession.WindowTitle(h)
                        .StartsWith("Make it ZBS - изображение", StringComparison.Ordinal)), "Saved Image Preview did not close");
                    using (var image = new Bitmap(destination)) Assert.That(image.Width * image.Height, Is.GreaterThan(10000));
                    Assert.That(app.Logged("Контекст: Image"), Is.True);
                    Assert.That(app.Logged("Image: vision-bbox обрезка"), Is.True, "The model must receive the canvas crop, not the entire Paint window");
                    Assert.That(File.ReadAllBytes(destination), Is.Not.EqualTo(File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("ZBS_SMOKE_SAMPLES"), "paint-cabin-sketch.png"))));
                    app.Result(new JObject { ["status"] = "passed", ["context"] = "Image", ["realPaint"] = true,
                        ["realCodexImageGeneration"] = true, ["saved"] = true, ["image"] = destination });
                    clipboard.Dispose();
                }
                finally
                {
                    if (!paint.HasExited && (!paint.CloseMainWindow() || !paint.WaitForExit(5000))) { paint.Kill(); paint.WaitForExit(5000); }
                }
            }
        }

        [Test]
        public void RealOneCConfiguratorModuleCapture()
        {
            const string database = "F:/temp/zbs-p0-20261017/onec-db";
            Assert.That(File.Exists(Path.Combine(database, "1Cv8.1CD")), Is.True, "Create the dedicated synthetic 1C database first");
            using (var app = new P0AppSession(_root, _output))
            using (var clipboard = new ClipboardTestScope())
            using (var process = Process.Start(new ProcessStartInfo("C:/Program Files/1cv8/8.3.27.1989/bin/1cv8.exe",
                "DESIGNER /F " + OwnedProcessRunner.QuoteArgument(database) + " /DisableStartupMessages /DisableStartupDialogs") { UseShellExecute = false }))
            {
                try
                {
                    clipboard.AllowOwner(app.Process.Id);
                    clipboard.AllowOwner(process.Id);
                    var clock = Stopwatch.StartNew();
                    IntPtr handle = IntPtr.Zero;
                    while (clock.ElapsedMilliseconds < 30000)
                    {
                        handle = P0AppSession.Handles(process.Id).FirstOrDefault();
                        if (handle != IntPtr.Zero) break;
                        Thread.Sleep(200);
                    }
                    Assert.That(handle, Is.Not.EqualTo(IntPtr.Zero));
                    Assert.That(new WindowInfo().ActivateWindow(handle.ToInt32()), Is.True);
                    Thread.Sleep(1000);
                    var root = P0AppSession.Ui(() => AutomationElement.FromHandle(handle));
                    var openConfiguration = P0AppSession.Ui(() => root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "Открыть конфигурацию")));
                    Assert.That(openConfiguration, Is.Not.Null);
                    P0AppSession.Invoke(openConfiguration);
                    Thread.Sleep(700);
                    var configuration = P0AppSession.Ui(() => root.FindFirst(TreeScope.Descendants,
                        new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "Конфигурация"),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))));
                    Assert.That(configuration, Is.Not.Null);
                    P0AppSession.Invoke(configuration);
                    P0AppSession.Keys(handle, process.Id, 0x10, 0x79);
                    Thread.Sleep(400);
                    var moduleCommand = P0AppSession.Ui(() => P0AppSession.Handles(process.Id).Select(h =>
                        AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.NameProperty, "Открыть модуль приложения"))).FirstOrDefault(e => e != null));
                    Assert.That(moduleCommand, Is.Not.Null);
                    P0AppSession.Invoke(moduleCommand);
                    Thread.Sleep(500);
                    handle = NativeMethods.GetForegroundWindow();
                    NativeMethods.GetWindowThreadProcessId(handle, out var foregroundPid);
                    Assert.That(foregroundPid, Is.EqualTo(process.Id), "The opened 1C module lost the owned foreground");
                    new ScreenCapture().CaptureWindow(Path.Combine(_output, "onec-initial.png"), handle.ToInt32(), process.Id);
                    File.WriteAllText(Path.Combine(_output, "onec-windows.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
                        P0AppSession.Handles(process.Id).Select(h => new { handle = h.ToInt64(), title = P0AppSession.WindowTitle(h) })));
                    // 1C's full descendant enumeration can fail on a stale provider;
                    // the production capture deliberately reads the focused editor.
                    var module = P0AppSession.Ui(() => AutomationElement.FocusedElement);
                    Assert.That(P0AppSession.Ui(() => module.Current.ProcessId), Is.EqualTo(process.Id));
                    File.WriteAllText(Path.Combine(_output, "onec-focused.json"), P0AppSession.Ui(() =>
                        Newtonsoft.Json.JsonConvert.SerializeObject(new { module.Current.Name, module.Current.ClassName,
                            type = module.Current.ControlType.ProgrammaticName,
                            patterns = module.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray() })));
                    var before = P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));
                    Assert.That(before, Does.Contain("РассчитатьСуммуЗаказа"));
                    P0AppSession.Keys(handle, process.Id, 0x11, 0x41);
                    P0AppSession.Keys(handle, process.Id, 0x11, 0x10, 0x87);
                    var preview = app.Window("Предпросмотр Make it ZBS", 180000);
                    app.Element(preview, "cancelButton");
                    app.Snapshot(preview, "onec-preview.png");
                    Assert.That(app.Logged("Контекст: OneCCode"), Is.True);
                    Assert.That(app.Logged("OneCCode: выделенный фрагмент"), Is.True);
                    var result = P0AppSession.DocumentText(preview);
                    Assert.That(result, Does.Contain("РассчитатьСуммуЗаказа"));
                    File.WriteAllText(Path.Combine(_output, "onec-result.txt"), result);
                    P0AppSession.Invoke(app.Element(preview, "cancelButton"));
                    app.Wait(() => app.Logged("Preview закрыт пользователем: cancelled"), "1C Preview did not report Cancel");
                    Assert.That(P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1)),
                        Is.EqualTo(before), "Cancel changed the unsaved 1C module");
                    P0AppSession.Invoke(module);
                    P0AppSession.Keys(handle, process.Id, 0x11, 0x24);
                    P0AppSession.Keys(handle, process.Id, 0x11, 0x10, 0x87);
                    var confirmation = app.Window("Make it ZBS", requiredButton: "7");
                    app.Snapshot(confirmation, "onec-full-module-confirmation.png");
                    Assert.That(P0AppSession.Ui(() => confirmation.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                        .Cast<AutomationElement>().Any(e => e.Current.Name.Contains("Обработать весь модуль целиком"))), Is.True);
                    P0AppSession.Invoke(app.Element(confirmation, "7"));
                    app.Wait(() => app.Logged("обработка всего модуля не подтверждена"), "Full module rejection was ignored");
                    Assert.That(P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1)), Is.EqualTo(before));
                    FullOneCReplacement(app, module, handle, process.Id, before, false);
                    FullOneCReplacement(app, module, handle, process.Id, before, true);
                    app.Result(new JObject { ["status"] = "passed", ["context"] = "OneCCode", ["realConfigurator"] = true,
                        ["realCodex"] = true, ["cancelPreservesBuffer"] = true, ["fullModuleConfirmationRejected"] = true,
                        ["fullReplacementRejected"] = true, ["fullReplacementApplied"] = true, ["undo"] = true, ["encryptedBackupExact"] = true });
                    clipboard.Dispose();
                }
                finally
                {
                    if (!process.HasExited && (!process.CloseMainWindow() || !process.WaitForExit(5000))) { process.Kill(); process.WaitForExit(5000); }
                }
            }
        }

        private void FullOneCReplacement(P0AppSession app, AutomationElement module, IntPtr source, int pid, string original, bool replace)
        {
            var label = replace ? "applied" : "rejected";
            P0AppSession.Invoke(module);
            P0AppSession.Keys(source, pid, 0x11, 0x24);
            Thread.Sleep(200);
            Assert.That(P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern))
                .GetSelection().Sum(range => range.GetText(-1).Length)), Is.Zero, "This branch must process the whole module, not a selection");
            P0AppSession.Keys(source, pid, 0x11, 0x10, 0x87);
            var captureConfirmation = app.Window("Make it ZBS", requiredButton: "7");
            app.Snapshot(captureConfirmation, "onec-full-" + label + "-capture-confirmation.png");
            P0AppSession.Invoke(P0AppSession.DialogControl(captureConfirmation, "6", ControlType.Button), true);
            Thread.Sleep(300);
            var foreground = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
            File.WriteAllText(Path.Combine(_output, "onec-full-" + label + "-focus.json"), new JObject
            { ["source"] = source.ToInt64(), ["foreground"] = foreground.ToInt64(), ["foregroundPid"] = foregroundPid,
                ["title"] = foregroundPid == pid || foregroundPid == app.Process.Id ? P0AppSession.WindowTitle(foreground) : "other process" }.ToString());
            app.Wait(() =>
            {
                Assert.That(app.Logged("Исходное окно изменилось во время захвата"), Is.False,
                    "Full module capture failed after the real confirmation click; inspect the focus snapshot");
                Assert.That(app.Logged("Не удалось получить текст модуля из Конфигуратора"), Is.False,
                    "The focused module was unavailable after confirmation");
                return P0AppSession.Handles(app.Process.Id).Any(h => P0AppSession.WindowTitle(h) == "Предпросмотр Make it ZBS");
            }, "Full module Preview did not open", 180000);
            var preview = app.Window("Предпросмотр Make it ZBS", 180000);
            var apply = app.Element(preview, "applyButton");
            app.Wait(() => P0AppSession.Ui(() => apply.Current.IsEnabled), "The full module was not bound for replacement");
            Assert.That(app.Logged("OneCCode: весь модуль"), Is.True);
            Assert.That(P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1)), Is.EqualTo(original));
            app.Snapshot(preview, "onec-full-" + label + "-preview.png");
            P0AppSession.Invoke(apply);
            var confirmation = app.Window("Make it ZBS - замена всего модуля", requiredButton: "7");
            app.Snapshot(confirmation, "onec-full-" + label + "-confirmation.png");
            Assert.That(P0AppSession.Ui(() => AutomationElement.FocusedElement.Current.AutomationId), Is.EqualTo("7"),
                "The destructive confirmation must default to No");
            var backups = Directory.GetFiles(Path.Combine(_root, "logs/backups"));
            Assert.That(backups, Is.Not.Empty);
            var store = ReplacementBackupStore.Constructor();
            foreach (var backup in backups)
            {
                Assert.That(Path.GetExtension(backup), Is.EqualTo(".zbsbackup"), "No plaintext or unfinished backup may be present");
                Assert.That(store.Read(backup), Is.EqualTo(original), "DPAPI must preserve the exact full module");
            }
            P0AppSession.Invoke(P0AppSession.DialogControl(confirmation, replace ? "6" : "7", ControlType.Button), true);
            if (!replace)
            {
                app.Wait(() => app.Logged("Замена всего модуля отменена пользователем (после диалога)"), "Replacement rejection was ignored");
                Assert.That(P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1)), Is.EqualTo(original));
                return;
            }
            app.Wait(() => app.Logged("Replace-in-place выполнен (scope=fullModule)"), "Confirmed full-module replacement did not complete");
            var changed = P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));
            Assert.That(changed, Is.Not.EqualTo(original).And.Contains("РассчитатьСуммуЗаказа"));
            File.WriteAllText(Path.Combine(_output, "onec-applied-module.txt"), changed);
            P0AppSession.Keys(source, pid, 0x11, 0x5A);
            app.Wait(() => P0AppSession.Ui(() => ((TextPattern)module.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1) == original),
                "1C Undo did not restore the original full module");
        }

        private sealed class StartupSnapshot : IDisposable
        {
            private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            private readonly object _value;
            private readonly RegistryValueKind _kind;
            private readonly string _root;
            internal StartupSnapshot(string root)
            {
                _root = root;
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    _value = key?.GetValue("MakeItZBS", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (_value != null) _kind = key.GetValueKind("MakeItZBS");
                }
            }
            public void Dispose()
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                {
                    var current = key.GetValue("MakeItZBS", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (Equals(current, _value)) return;
                    Assert.That(current == null || (current is string value && value.Contains(_root.Replace('/', '\\'))), Is.True,
                        "Autostart changed externally; do not overwrite it");
                    if (_value == null) key.DeleteValue("MakeItZBS", false);
                    else key.SetValue("MakeItZBS", _value, _kind);
                }
            }
        }
    }
}
