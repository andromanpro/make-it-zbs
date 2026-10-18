using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZBSWindowsComponent
{
    internal static class WindowsAppIdentity
    {
        internal const string AppId = "MakeItZBS.Desktop";
        internal const string DisplayName = "Make it ZBS";
        internal const string RegistryPath = @"Software\Classes\AppUserModelId\" + AppId;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        internal static void TryInitializeForCurrentProcess()
        {
            // This component is also loaded by testhost and other hosts: do not rename them.
            using (var process = Process.GetCurrentProcess())
            {
                if (!IsApplicationHost(process.ProcessName)) return;
            }
            TryApply(RegisterCurrentUser,
                () => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId)),
                LogWarning);
        }

        internal static void TryEnsureShortcut()
        {
            using (var process = Process.GetCurrentProcess())
            {
                if (!IsApplicationHost(process.ProcessName)) return;
            }
            var launcher = ProjectPaths.LauncherPath;
            if (!string.Equals(Path.GetExtension(launcher), ".exe", StringComparison.OrdinalIgnoreCase)) return;
            TryStep(() => ApplicationShortcut.Ensure(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Programs), DisplayName + ".lnk"), launcher), "Start menu shortcut",
                LogWarning);
            TryStep(() => ApplicationShortcut.Ensure(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory), DisplayName + ".lnk"), launcher,
                onlyMatchingTarget: true), "Desktop shortcut identity",
                LogWarning);
        }

        private static void LogWarning(string message) => Console.WriteLine("[zbs.identity] " + message);

        internal static bool IsApplicationHost(string processName) =>
            string.Equals(processName, "oscript", StringComparison.OrdinalIgnoreCase);

        internal static void TryApply(Action register, Action setProcessId, Action<string> warn)
        {
            TryStep(register, "Registration", warn);
            TryStep(setProcessId, "Process AppUserModelID", warn);
        }

        private static void TryStep(Action action, string step, Action<string> warn)
        {
            try { action(); }
            catch (Exception error)
            {
                warn(step + " failed: " + error.GetType().Name + " (0x" + error.HResult.ToString("X8") + ")");
            }
        }

        private static void RegisterCurrentUser()
        {
            string iconPath = null;
            TryStep(() => iconPath = EnsureIconFile(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData)), "Notification icon",
                LogWarning);
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                if (key == null) throw new InvalidOperationException("Application identity key is unavailable");
                WriteRegistration(key, iconPath);
            }
        }

        internal static void WriteRegistration(RegistryKey key, string iconPath)
        {
            SetValueIfChanged(key, "DisplayName", DisplayName);
            if (!string.IsNullOrEmpty(iconPath)) SetValueIfChanged(key, "IconUri", iconPath);
        }

        private static void SetValueIfChanged(RegistryKey key, string name, string value)
        {
            if (!string.Equals(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    value, StringComparison.Ordinal) || key.GetValueKind(name) != RegistryValueKind.ExpandString)
                key.SetValue(name, value, RegistryValueKind.ExpandString);
        }

        internal static string EnsureIconFile(string localAppData)
        {
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("LocalApplicationData is unavailable");
            var path = Path.Combine(localAppData, "MakeItZBS", "notification.ico");
            byte[] bytes;
            using (var resource = typeof(WindowsAppIdentity).Assembly.GetManifestResourceStream("ZBSWindowsComponent.app.ico"))
            using (var buffer = new MemoryStream())
            {
                if (resource == null) throw new InvalidOperationException("Application icon is unavailable");
                resource.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return path;
        }
    }
}
