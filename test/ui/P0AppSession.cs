using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
    internal sealed class P0AppSession : IDisposable
    {
        private readonly StreamWriter _log;
        private readonly StringBuilder _trace = new StringBuilder();
        private readonly object _sync = new object();
        internal Process Process { get; }
        internal string Root { get; }
        internal string Output { get; }

        internal P0AppSession(string root, string output, bool waitForHotkey = true)
        {
            try { using (Mutex.OpenExisting(@"Local\MakeItZBS-SingleInstance")) Assert.Fail("Stop the normal ZBS before P0 tests"); }
            catch (WaitHandleCannotBeOpenedException) { /* The isolated app can own this session. */ }
            Assert.That(KeyAvailable(0x87), Is.True, "Ctrl+Shift+F24 is occupied");
            Root = root; Output = output;
            Directory.CreateDirectory(output);
            _log = new StreamWriter(Path.Combine(output, "app.log")) { AutoFlush = true };
            var info = new ProcessStartInfo(Path.Combine(root, "runtime/bin/oscript.exe"),
                "-encoding=utf-8 " + OwnedProcessRunner.QuoteArgument(Path.Combine(root, "src/onescript/zbs-run.os")))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            info.EnvironmentVariables["WEBVIEW2_USER_DATA_FOLDER"] = Path.Combine(root, "webview-p0");
            Process = Process.Start(info);
            DataReceivedEventHandler write = (_, args) =>
            {
                if (args.Data == null) return;
                lock (_sync) { _log.WriteLine(args.Data); _trace.AppendLine(args.Data); }
            };
            Process.OutputDataReceived += write; Process.ErrorDataReceived += write;
            Process.BeginOutputReadLine(); Process.BeginErrorReadLine();
            try { if (waitForHotkey) Wait(() => !KeyAvailable(0x87), "P0 application did not register F24", 30000); }
            catch { Dispose(); throw; }
        }

        internal bool Logged(string value) { lock (_sync) return _trace.ToString().Contains(value); }
        internal static T Ui<T>(Func<T> read) => UiaDispatcher.Shared.Run(read);
        internal void Wait(Func<bool> predicate, string message, int timeout = 20000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeout)
            {
                Assert.That(Process.HasExited, Is.False, "The isolated app exited; inspect app.log");
                if (predicate()) return;
                Thread.Sleep(100);
            }
            Assert.Fail(message);
        }

        internal AutomationElement Window(string title, int timeout = 20000, string requiredButton = null)
        {
            AutomationElement result = null;
            Wait(() =>
            {
                var handle = Handles(Process.Id).FirstOrDefault(h => WindowTitle(h) == title
                    && (requiredButton == null || DialogControl(Ui(() => AutomationElement.FromHandle(h)), requiredButton, ControlType.Button) != null));
                if (handle == IntPtr.Zero) return false;
                result = Ui(() => AutomationElement.FromHandle(handle));
                return true;
            }, "Window not found: " + title, timeout);
            return result;
        }

        internal static IntPtr[] Handles(int pid, bool visibleOnly = true)
        {
            var handles = new List<IntPtr>();
            NativeMethods.EnumWindows((handle, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var actual);
                if (actual == pid && (!visibleOnly || NativeMethods.IsWindowVisible(handle))) handles.Add(handle);
                return true;
            }, IntPtr.Zero);
            return handles.ToArray();
        }

        internal static string WindowTitle(IntPtr handle)
        {
            var title = new StringBuilder(1024);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            return title.ToString();
        }

        internal AutomationElement Element(AutomationElement parent, string id, string name = null)
        {
            AutomationElement result = null;
            Wait(() => Ui(() => (result = parent.FindFirst(TreeScope.Descendants, new PropertyCondition(
                name == null ? AutomationElement.AutomationIdProperty : AutomationElement.NameProperty, name ?? id))) != null
                && !result.Current.IsOffscreen), "Control not found: " + (name ?? id));
            return result;
        }

        internal static AutomationElement DialogControl(AutomationElement parent, string id, ControlType type) =>
            Ui(() => parent.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, id),
                new PropertyCondition(AutomationElement.ControlTypeProperty, type))));

        internal static void Invoke(AutomationElement element, bool physicalClick = false) => Ui(() =>
        {
            if (!physicalClick && element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                try { ((InvokePattern)invoke).Invoke(); return true; }
                catch (NotImplementedException) { /* Some 1C menu providers advertise an unimplemented Invoke. */ }
            }
            Assert.That(element.Current.IsEnabled, Is.True, "Do not click a disabled control");
            if (!element.TryGetClickablePoint(out var point))
            {
                Assert.That(element.Current.ControlType, Is.EqualTo(ControlType.Text), "Only observed text bounds may replace a missing clickable point");
                var bounds = element.Current.BoundingRectangle;
                Assert.That(bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0, Is.False);
                point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            }
            Assert.That(SetPhysicalCursorPos((int)point.X, (int)point.Y), Is.True);
            RequirePhysicalTarget((int)point.X, (int)point.Y, element.Current.ProcessId);
            var mouse = new[] { new NativeMethods.Input { type = 0, u = new NativeMethods.InputUnion { mi = new NativeMethods.MouseInput { dwFlags = 0x0002 } } },
                new NativeMethods.Input { type = 0, u = new NativeMethods.InputUnion { mi = new NativeMethods.MouseInput { dwFlags = 0x0004 } } } };
            Assert.That(NativeMethods.SendInput(2, mouse, Marshal.SizeOf(typeof(NativeMethods.Input))), Is.EqualTo(2));
            return true;
        });

        internal void Snapshot(AutomationElement window, string file)
        {
            var handle = Ui(() => window.Current.NativeWindowHandle);
            NativeMethods.GetWindowThreadProcessId(new IntPtr(handle), out var pid);
            Assert.That(pid, Is.EqualTo(Process.Id), "Only the owned application's window may be activated");
            Assert.That(GetWindowLong(new IntPtr(handle), -20) & 0x08000000, Is.Zero,
                "Do not activate the non-activating HUD while waiting for a confirmation dialog");
            if (NativeMethods.GetForegroundWindow() != new IntPtr(handle)) new WindowInfo().ActivateWindow(handle);
            Wait(() => NativeMethods.GetForegroundWindow() == new IntPtr(handle), "Preview did not receive focus for its evidence snapshot");
            Thread.Sleep(250);
            var capture = new ScreenCapture();
            capture.CaptureWindow(Path.Combine(Output, file), handle, Process.Id);
            File.WriteAllText(Path.Combine(Output, file + ".capture.json"), capture.GetLastCaptureMetadata());
            TestContext.AddTestAttachment(Path.Combine(Output, file));
        }

        internal static string DocumentText(AutomationElement window) => Ui(() =>
        {
            var documents = window.FindAll(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))).Cast<AutomationElement>();
            foreach (var document in documents)
                if (document.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                    return ((TextPattern)pattern).DocumentRange.GetText(40000);
            return string.Join("\n", window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                .Where(e => !e.Current.IsOffscreen).Select(e => e.Current.Name));
        });

        internal AutomationElement OpenSettingsFromTray(bool physicalClick = false)
        {
            var menu = OpenTrayMenu(physicalClick);
            Invoke(Element(menu, null, English ? "Settings" : "Настройки"));
            var settings = Window(English ? "Make it ZBS Settings" : "Настройки Make it ZBS");
            Wait(() => Ui(() => settings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "hotkeyInput")) != null), "Settings payload did not load");
            Thread.Sleep(350); // The posted load payload replaces the previous DOM after Show.
            return settings;
        }

        internal void QuitFromTray()
        {
            try { Invoke(Element(OpenTrayMenu(), null, English ? "Exit" : "Выход")); }
            catch (InvalidOperationException error) when (error.InnerException is COMException && Process.WaitForExit(1000))
            { /* A successful Exit can close the UIA RPC connection before Invoke returns. */ }
            Assert.That(Process.WaitForExit(10000), Is.True, "Tray Exit did not finish the owned application");
            Assert.That(Process.ExitCode, Is.EqualTo(0));
        }

        private AutomationElement OpenTrayMenu(bool physicalClick = false)
        {
            // A native Toast does not temporarily expose a hidden tray icon as a
            // legacy balloon did. Open the overflow before querying its geometry.
            {
                var shell = Ui(() => AutomationElement.FromHandle(NativeMethods.FindWindow("Shell_TrayWnd", null)));
                Assert.That(shell, Is.Not.Null, "Windows taskbar is not available");
                var currentPanel = NativeMethods.FindWindow("NotifyIconOverflowWindow", null);
                if (currentPanel == IntPtr.Zero || !NativeMethods.IsWindowVisible(currentPanel))
                {
                    var overflow = Ui(() => shell.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "1502")));
                    if (overflow != null) { Invoke(overflow); Thread.Sleep(250); }
                }
            }
            var matches = new List<IconRect>();
            var discovery = new JArray();
            IconIdentifier ownedIcon = default(IconIdentifier);
            Wait(() =>
            {
                matches.Clear();
                discovery.Clear();
                foreach (var handle in Handles(Process.Id, false))
                {
                    var windowClass = new StringBuilder(256);
                    NativeMethods.GetClassName(handle, windowClass, windowClass.Capacity);
                    var codes = new JArray();
                    for (uint id = 0; id < 32; id++)
                    {
                        var identifier = new IconIdentifier { Size = Marshal.SizeOf(typeof(IconIdentifier)), Window = handle, Id = id };
                        var code = Shell_NotifyIconGetRect(ref identifier, out var rectangle);
                        codes.Add(new JObject { ["id"] = id, ["hr"] = code, ["rect"] = rectangle.Left + "," + rectangle.Top + "," + rectangle.Right + "," + rectangle.Bottom });
                        if (code == 0 && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top)
                        { matches.Add(rectangle); ownedIcon = identifier; }
                    }
                    discovery.Add(new JObject { ["handle"] = handle.ToInt64(), ["class"] = windowClass.ToString(), ["codes"] = codes });
                }
                File.WriteAllText(Path.Combine(Output, "tray-discovery.json"), discovery.ToString());
                Assert.That(matches.Count, Is.LessThanOrEqualTo(1), "The owned process has ambiguous tray icons");
                return matches.Count == 1;
            }, "The owned process did not expose its registered tray icon", 5000);
            Assert.That(matches.Count, Is.EqualTo(1), "The owned process must have exactly one registered notification icon");
            var rect = matches[0];
            File.WriteAllText(Path.Combine(Output, "tray-icon.json"), new JObject { ["pid"] = Process.Id,
                ["left"] = rect.Left, ["top"] = rect.Top, ["right"] = rect.Right, ["bottom"] = rect.Bottom }.ToString());
            if (physicalClick)
            {
                NativeMethods.GetWindowThreadProcessId(NativeMethods.FindWindow("Shell_TrayWnd", null), out var shellPid);
                // First-launch notifications can cover the overflow panel. Do not
                // hover them: hovering can keep the notification on screen.
                Wait(() => IsTrayHit((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, shellPid),
                    "The tray icon stayed covered by another window; no click was sent", 20000);
                Assert.That(SetPhysicalCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2), Is.True);
                new ScreenCapture().CaptureRegion(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    Path.Combine(Output, "tray-hit-target-" + DateTime.UtcNow.Ticks + ".png"));
                var hit = RequirePhysicalTarget((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, (int)shellPid);
                var rootClass = new StringBuilder(128);
                NativeMethods.GetClassName(GetAncestor(hit, 2), rootClass, rootClass.Capacity);
                Assert.That(rootClass.ToString(), Is.EqualTo("Shell_TrayWnd").Or.EqualTo("NotifyIconOverflowWindow"),
                    "The notification icon is not exposed on the taskbar; do not click another Explorer surface");
                var mouse = new[] { new NativeMethods.Input { type = 0, u = new NativeMethods.InputUnion { mi = new NativeMethods.MouseInput { dwFlags = 0x0008 } } },
                    new NativeMethods.Input { type = 0, u = new NativeMethods.InputUnion { mi = new NativeMethods.MouseInput { dwFlags = 0x0010 } } } };
                Assert.That(NativeMethods.SendInput(2, mouse, Marshal.SizeOf(typeof(NativeMethods.Input))), Is.EqualTo(2));
            }
            else
            {
                // .NET Framework NotifyIcon callback; this branch tests the app's
                // real menu, not Explorer hit testing or the already-tested first click.
                Assert.That(PostMessage(ownedIcon.Window, 0x800, new IntPtr(ownedIcon.Id), new IntPtr(0x0205)), Is.True);
            }
            AutomationElement menu = null;
            Wait(() => Ui(() => (menu = Handles(Process.Id).Select(AutomationElement.FromHandle).FirstOrDefault(e =>
                e.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, English ? "Settings" : "Настройки")) != null)) != null),
                "The registered icon did not open a menu belonging to the isolated PID", 5000);
            return menu;
        }

        private bool English => JObject.Parse(File.ReadAllText(Path.Combine(Root, "config/settings.json")))
            ["general"].Value<string>("language") == "en";

        [StructLayout(LayoutKind.Sequential)]
        private struct IconIdentifier { internal int Size; internal IntPtr Window; internal uint Id; internal Guid Guid; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IconRect { internal int Left, Top, Right, Bottom; }
        [DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out IconRect rectangle);
        [DllImport("user32.dll")]
        private static extern bool SetPhysicalCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPhysicalPoint(NativePoint point);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        private static IntPtr RequirePhysicalTarget(int x, int y, int expectedPid)
        {
            var hit = WindowFromPhysicalPoint(new NativePoint { X = x, Y = y });
            NativeMethods.GetWindowThreadProcessId(hit, out var actualPid);
            Assert.That(hit, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(actualPid, Is.EqualTo((uint)expectedPid),
                "Physical target is occluded by another process; no click was sent");
            return hit;
        }

        private static bool IsTrayHit(int x, int y, uint expectedPid)
        {
            var hit = WindowFromPhysicalPoint(new NativePoint { X = x, Y = y });
            NativeMethods.GetWindowThreadProcessId(hit, out var actualPid);
            if (hit == IntPtr.Zero || actualPid != expectedPid) return false;
            var name = new StringBuilder(128);
            NativeMethods.GetClassName(GetAncestor(hit, 2), name, name.Capacity);
            return name.ToString() == "Shell_TrayWnd" || name.ToString() == "NotifyIconOverflowWindow";
        }

        internal static bool KeyAvailable(ushort key)
        {
            if (!NativeMethods.RegisterHotKey(IntPtr.Zero, 903, 0x4006, key)) return false;
            NativeMethods.UnregisterHotKey(IntPtr.Zero, 903);
            return true;
        }

        internal static void Keys(IntPtr source, int pid, params ushort[] keys)
        {
            NativeMethods.GetWindowThreadProcessId(source, out var actual);
            Assert.That(actual, Is.EqualTo(pid));
            Assert.That(NativeMethods.GetForegroundWindow(), Is.EqualTo(source), "Input destination lost focus");
            NativeMethods.Input Input(ushort key, bool up) => new NativeMethods.Input { type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KeyboardInput { wVk = key, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } } };
            var release = keys.AsEnumerable().Reverse().Select(k => Input(k, true)).ToArray();
            try
            {
                var all = keys.Select(k => Input(k, false)).Concat(release).ToArray();
                Assert.That(NativeMethods.SendInput((uint)all.Length, all, Marshal.SizeOf(typeof(NativeMethods.Input))), Is.EqualTo(all.Length));
            }
            finally { NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(NativeMethods.Input))); }
        }

        internal void Result(JObject result) => File.WriteAllText(Path.Combine(Output, "result.json"), result.ToString());
        public void Dispose()
        {
            if (Process != null)
            {
                if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(10000); }
                Process.WaitForExit(); Process.Dispose();
            }
            _log?.Dispose();
        }
    }
}
