using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    public partial class P0PipelineTests
    {
        private string _notificationAnnouncement;
        private string _notificationSender;

        [Test, Category("OwnerSettings")]
        public void OwnerOnboardingNotificationOpensSettingsAndDoesNotRepeat()
        {
            var path = Path.Combine(_root, "config/settings.json");
            var settings = JObject.Parse(File.ReadAllText(path));
            settings["general"]["codexOfferShown"] = false;
            File.WriteAllText(path, settings.ToString());
            var modelsPath = Path.Combine(_root, "config/models.json");
            var modelsBefore = File.ReadAllBytes(modelsPath);
            using (var first = new P0AppSession(_root, Path.Combine(_output, "first-start"), false))
            {
                AutomationElement notification = null;
                first.Wait(() => (notification = FindCodexNotification(Path.Combine(_output, "notification-discovery.json"))) != null,
                    "The real Windows Codex notification did not appear", 30000);
                var evidence = P0AppSession.Ui(() => new JObject
                {
                    ["name"] = notification.Current.Name, ["pid"] = notification.Current.ProcessId,
                    ["bounds"] = notification.Current.BoundingRectangle.ToString(),
                    ["shellAnnouncement"] = _notificationAnnouncement, ["sender"] = _notificationSender
                });
                File.WriteAllText(Path.Combine(_output, "notification.json"), evidence.ToString());
                Assert.That(_notificationSender, Is.EqualTo("Make it ZBS"),
                    "Windows must identify the sender, not just the notification title");
                var previousBounds = P0AppSession.Ui(() => notification.Current.BoundingRectangle);
                var stableSince = Stopwatch.StartNew();
                first.Wait(() =>
                {
                    var bounds = P0AppSession.Ui(() => notification.Current.BoundingRectangle);
                    if (bounds != previousBounds) { previousBounds = bounds; stableSince.Restart(); }
                    return stableSince.ElapsedMilliseconds >= 300;
                }, "Notification animation did not settle", 5000);
                P0AppSession.Invoke(notification, true);
                first.Wait(() => first.Logged("[zbs.notification] Activated"), "Windows did not deliver the actual toast click", 5000);
                var window = first.Window("Настройки Make it ZBS");
                P0AppSession.Invoke(first.Element(window, null, "Провайдеры ИИ"), true);
                first.Element(window, null, "Подписка ChatGPT подключена");
                first.Snapshot(window, "notification-opened-providers.png");
                Assert.That(File.ReadAllBytes(modelsPath), Is.EqualTo(modelsBefore), "Clicking the offer must not silently change providers");
                first.Wait(() => !P0AppSession.KeyAvailable(0x87), "The first app did not finish registering its normal shortcut", 30000);
                P0AppSession.Invoke(first.Element(window, null, "Отмена"));
                first.QuitFromTray();
            }
            var afterFirst = File.ReadAllBytes(path);
            Assert.That(JObject.Parse(File.ReadAllText(path))["general"].Value<bool>("codexOfferShown"), Is.True);
            using (var second = new P0AppSession(_root, Path.Combine(_output, "second-start")))
            {
                Thread.Sleep(5000);
                Assert.That(FindCodexNotification(), Is.Null, "The first-launch notification repeated");
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(afterFirst));
                Assert.That(File.ReadAllBytes(modelsPath), Is.EqualTo(modelsBefore));
                second.QuitFromTray();
                second.Result(new JObject { ["status"] = "passed", ["physicalNotificationClick"] = true,
                    ["providersOpened"] = true, ["subscriptionStatus"] = true, ["oneTimeOffer"] = true,
                    ["providerUnchanged"] = true, ["sender"] = "Make it ZBS" });
            }
        }

        private AutomationElement FindCodexNotification(string diagnostic = null) => P0AppSession.Ui(() =>
        {
            var windows = new JArray();
            _notificationAnnouncement = null;
            var expectedMessage = TrayApplicationContext.BuildCodexOnboardingMessage(
                new CodexCliStatus { Installed = true, Authenticated = true, UsesChatGptSubscription = true }, "ru");
            SHQueryUserNotificationState(out var notificationState);
            var shown = JObject.Parse(File.ReadAllText(Path.Combine(_root, "config/settings.json")))["general"].Value<bool>("codexOfferShown");
            var capturePath = Path.Combine(_output, "notification-area.png");
            if (diagnostic != null && shown && !File.Exists(capturePath))
            {
                using (new ScreenCapture.PhysicalPixelScope())
                {
                    var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    var capture = new ScreenCapture();
                    capture.CaptureRegion(area.Right - 720, area.Bottom - 400, 720, 400, capturePath);
                    File.WriteAllText(capturePath + ".capture.json", capture.GetLastCaptureMetadata());
                }
            }
            windows.Add(new JObject { ["notificationState"] = notificationState,
                ["offerShown"] = shown });
            foreach (var process in Process.GetProcessesByName("ShellExperienceHost").Concat(Process.GetProcessesByName("explorer")))
                using (process)
                    foreach (var root in NotificationRoots(process.Id))
                    {
                        var matches = root.FindAll(TreeScope.Subtree, Condition.TrueCondition)
                            .Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen
                                && e.Current.ControlType == ControlType.Text && e.Current.Name == expectedMessage).ToArray();
                        windows.Add(new JObject { ["handle"] = root.Current.NativeWindowHandle, ["title"] = root.Current.Name,
                            ["matches"] = new JArray(matches.Select(e => new JObject { ["name"] = e.Current.Name,
                                ["type"] = e.Current.ControlType.ProgrammaticName, ["bounds"] = e.Current.BoundingRectangle.ToString() })) });
                        if (diagnostic != null) File.WriteAllText(diagnostic, windows.ToString());
                        if (matches.Length != 0)
                        {
                            _notificationAnnouncement = root.Current.Name;
                            var texts = root.FindAll(TreeScope.Subtree, new PropertyCondition(
                                AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                                .Select(element => element.Current.Name).ToArray();
                            _notificationSender = texts.FirstOrDefault();
                            if (diagnostic != null) File.WriteAllText(diagnostic + ".texts.json", new JArray(texts).ToString());
                            return matches[0];
                        }
                    }
            if (diagnostic != null) File.WriteAllText(diagnostic, windows.ToString());
            return null;
        });

        private static AutomationElement[] NotificationRoots(int pid)
        {
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>().ToList();
            using (new ScreenCapture.PhysicalPixelScope())
            {
                var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                var hit = WindowFromPhysicalPoint(new NotificationPoint { X = area.Right - 270, Y = area.Bottom - 130 });
                NativeMethods.GetWindowThreadProcessId(hit, out var actual);
                if (actual == pid)
                {
                    var handle = GetAncestor(hit, 2);
                    if (handle != IntPtr.Zero && roots.All(root => root.Current.NativeWindowHandle != handle.ToInt64()))
                        roots.Add(AutomationElement.FromHandle(handle));
                }
            }
            return roots.ToArray();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NotificationPoint { internal int X, Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPhysicalPoint(NotificationPoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

        [Test, Category("OwnerSettings")]
        public void OwnerLanguageProfileHotkeysAndExternalContactsPersist()
        {
            var path = Path.Combine(_root, "config/settings.json");
            var settings = JObject.Parse(File.ReadAllText(path));
            var names = new[] { "codex", "claude", "opencode" };
            for (var i = 0; i < names.Length; i++)
            {
                var profile = settings["aiChat"]["profiles"][names[i]];
                profile["enabled"] = true;
                profile["hotkey"] = "Ctrl+Shift+F" + (13 + i);
                Assert.That(P0AppSession.KeyAvailable((ushort)(0x7C + i)), Is.True, "Initial profile shortcut is occupied");
                Assert.That(P0AppSession.KeyAvailable((ushort)(0x83 + i)), Is.True, "New profile shortcut is occupied");
            }
            File.WriteAllText(path, settings.ToString());
            using (var startup = new StartupSnapshot(_root))
            using (var app = new P0AppSession(_root, _output))
            {
                app.Wait(() => names.Select((name, i) => !P0AppSession.KeyAvailable((ushort)(0x7C + i))).All(x => x),
                    "Initial profile shortcuts were not registered");
                var window = app.OpenSettingsFromTray();
                SelectLanguage(app, window, true);
                app.Snapshot(window, "settings-en.png");
                P0AppSession.Invoke(app.Element(window, null, "Save"));
                app.Wait(() => JObject.Parse(File.ReadAllText(path))["general"].Value<string>("language") == "en",
                    "English was not saved");
                window = app.OpenSettingsFromTray();
                Assert.That(P0AppSession.DocumentText(window), Does.Contain("General").And.Contain("AI providers").And.Contain("About"));
                P0AppSession.Invoke(app.Element(window, null, "AI chats"));
                Thread.Sleep(350); // ReloadAndShow queues the WebView2 load payload after Show.
                File.WriteAllText(Path.Combine(_output, "profile-accessibility.json"), P0AppSession.Ui(() =>
                {
                    var nodes = new JArray();
                    foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                        try { nodes.Add(new JObject { ["name"] = element.Current.Name, ["id"] = element.Current.AutomationId,
                            ["type"] = element.Current.ControlType.ProgrammaticName, ["offscreen"] = element.Current.IsOffscreen,
                            ["patterns"] = new JArray(element.GetSupportedPatterns().Select(p => p.ProgrammaticName)) }); }
                        catch (ElementNotAvailableException) { nodes.Add(new JObject { ["unavailable"] = true }); }
                    return nodes.ToString();
                }));
                for (var i = 0; i < names.Length; i++) CaptureProfileHotkey(app, window, names[i], (ushort)(0x83 + i), 20 + i);
                app.Snapshot(window, "profiles-en-edited.png");
                P0AppSession.Invoke(app.Element(window, null, "Save"));
                app.Wait(() => names.Select((name, i) => P0AppSession.KeyAvailable((ushort)(0x7C + i))
                    && !P0AppSession.KeyAvailable((ushort)(0x83 + i))).All(x => x),
                    "Profile shortcuts did not reload without restarting the app");
                window = app.OpenSettingsFromTray();
                P0AppSession.Invoke(app.Element(window, null, "AI chats"));
                for (var i = 0; i < names.Length; i++)
                {
                    var input = OpenProfileInput(window, names[i]);
                    var value = P0AppSession.Ui(() => ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                    Assert.That(value, Is.EqualTo("Ctrl+Shift+F" + (20 + i)));
                }
                P0AppSession.Invoke(app.Element(window, null, "General"));
                SelectLanguage(app, window, false);
                P0AppSession.Invoke(app.Element(window, null, "Сохранить"));
                app.Wait(() => JObject.Parse(File.ReadAllText(path))["general"].Value<string>("language") == "ru",
                    "Russian was not saved");
                window = app.OpenSettingsFromTray();
                P0AppSession.Invoke(app.Element(window, null, "Об авторе"));
                var contacts = new JArray();
                foreach (var target in new[] { new[] { "authorWebsiteButton", "androman.pro" }, new[] { "authorTelegramButton", "t.me/andromanpro" } })
                {
                    var button = app.Element(window, target[0]);
                    var before = P0AppSession.Ui(() => button.Current.BoundingRectangle);
                    Hover(button);
                    Thread.Sleep(500);
                    var after = P0AppSession.Ui(() => button.Current.BoundingRectangle);
                    Assert.That(after, Is.EqualTo(before), "The contact hover moved its hit target");
                    app.Snapshot(window, target[0] + "-hover.png");
                    P0AppSession.Invoke(button, true);
                    contacts.Add(WaitForExternalBrowser(app, target[1]));
                    Assert.That(new WindowInfo().ActivateWindow(P0AppSession.Ui(() => window.Current.NativeWindowHandle)), Is.True);
                }
                P0AppSession.Invoke(app.Element(window, null, "Отмена"));
                var saved = JObject.Parse(File.ReadAllText(path));
                Assert.That(saved["general"].Value<string>("language"), Is.EqualTo("ru"));
                for (var i = 0; i < names.Length; i++) Assert.That(saved["aiChat"]["profiles"][names[i]].Value<string>("hotkey"), Is.EqualTo("Ctrl+Shift+F" + (20 + i)));
                app.Result(new JObject { ["status"] = "passed", ["ruEnRuPersisted"] = true,
                    ["profilesCapturedAndPersisted"] = new JArray(names), ["oldProfileKeysReleased"] = true,
                    ["newProfileKeysRegisteredLive"] = true, ["contacts"] = contacts });
                app.QuitFromTray();
            }
        }

        private static void SelectLanguage(P0AppSession app, AutomationElement window, bool english)
        {
            var select = app.Element(window, "languageSelect");
            P0AppSession.Invoke(select, true);
            var handle = new IntPtr(P0AppSession.Ui(() => window.Current.NativeWindowHandle));
            P0AppSession.Keys(handle, app.Process.Id, english ? (ushort)0x23 : (ushort)0x24);
            P0AppSession.Keys(handle, app.Process.Id, 0x0D);
            app.Element(window, null, english ? "Save" : "Сохранить");
        }

        private static void CaptureProfileHotkey(P0AppSession app, AutomationElement window, string name, ushort key, int number)
        {
            var input = OpenProfileInput(window, name);
            P0AppSession.Ui(() => { input.SetFocus(); return true; });
            P0AppSession.Invoke(input, true);
            app.Wait(() => P0AppSession.Ui(() => input.Current.HasKeyboardFocus), "Profile hotkey input has no keyboard focus");
            P0AppSession.Keys(new IntPtr(P0AppSession.Ui(() => window.Current.NativeWindowHandle)), app.Process.Id, 0x11, 0x10, key);
            app.Wait(() => P0AppSession.Ui(() => ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "Ctrl+Shift+F" + number),
                "Profile shortcut was not captured: " + name);
        }

        private static AutomationElement OpenProfileInput(AutomationElement window, string name)
        {
            var label = name == "codex" ? "Codex / ChatGPT" : name == "claude" ? "Claude" : "OpenCode";
            var header = P0AppSession.Ui(() => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .Single(e => e.Current.Name.EndsWith(label + " Enabled", StringComparison.Ordinal)));
            P0AppSession.Ui(() =>
            {
                ((ScrollItemPattern)header.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
                var expand = (ExpandCollapsePattern)header.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                if (expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) expand.Expand();
                return true;
            });
            var input = P0AppSession.Ui(() => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "profileHotkey-" + name)));
            P0AppSession.Ui(() => { ((ScrollItemPattern)input.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView(); return true; });
            return input;
        }

        private static void Hover(AutomationElement element)
        {
            var point = P0AppSession.Ui(() => element.GetClickablePoint());
            Assert.That(SetPhysicalCursorPos((int)point.X, (int)point.Y), Is.True);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetPhysicalCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        private static JObject WaitForExternalBrowser(P0AppSession app, string expected)
        {
            JObject result = null;
            app.Wait(() =>
            {
                var handle = NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(handle, out var pid);
                var observation = new JObject { ["foreground"] = handle.ToInt64(), ["pid"] = pid };
                var diagnostic = Path.Combine(app.Output, expected.Replace('/', '-') + "-browser.json");
                File.WriteAllText(diagnostic, observation.ToString());
                if (pid == 0 || pid == app.Process.Id) return false;
                using (var browser = Process.GetProcessById((int)pid))
                {
                    observation["process"] = browser.ProcessName;
                    File.WriteAllText(diagnostic, observation.ToString());
                    if (!new[] { "firefox", "chrome", "msedge", "browser" }.Contains(browser.ProcessName.ToLowerInvariant())) return false;
                    var controls = P0AppSession.Ui(() => new JArray(AutomationElement.FromHandle(handle).FindAll(TreeScope.Descendants,
                        new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))).Cast<AutomationElement>()
                        .Where(e => !e.Current.IsOffscreen && !e.Current.IsPassword).Select(e =>
                        {
                            var value = e.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                                ? ((ValuePattern)pattern).Current.Value : string.Empty;
                            return new JObject { ["id"] = e.Current.AutomationId, ["name"] = e.Current.Name,
                                ["type"] = e.Current.ControlType.ProgrammaticName,
                                ["patterns"] = new JArray(e.GetSupportedPatterns().Select(p => p.ProgrammaticName)),
                                ["value"] = value.Contains("androman") ? value : "other-value" };
                        })));
                    observation["controls"] = controls;
                    File.WriteAllText(diagnostic, observation.ToString());
                    var address = controls.Select(c => c.Value<string>("value")).FirstOrDefault(value =>
                        value == "https://" + expected || value == "https://" + expected + "/" || value == expected || value == expected + "/");
                    if (address == null) return false;
                    result = new JObject { ["url"] = address, ["process"] = browser.ProcessName, ["pid"] = pid, ["window"] = handle.ToInt64() };
                    return true;
                }
            }, "The contact did not open the expected URL in an external browser: " + expected, 20000);
            return result;
        }
    }
}
