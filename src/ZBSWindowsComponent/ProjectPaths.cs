using System;
using System.IO;
using System.Reflection;

namespace ZBSWindowsComponent
{
    internal static class ProjectPaths
    {
        private const int MaxLevels = 6;

        public static string ConfigDir
        {
            get { return FindDirectoryUpwards("config"); }
        }

        public static string UiDir
        {
            get { return FindDirectoryUpwards("ui"); }
        }

        public static string WebViewUserDataDir
        {
            get
            {
                var localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                return ResolveWebViewUserDataDir(localAppData, Path.GetTempPath());
            }
        }

        internal static string ResolveWebViewUserDataDir(
            string localAppData, string fallbackRoot)
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var preferred = BuildWebViewUserDataDir(localAppData);
                if (TryCreateDirectory(preferred))
                {
                    return preferred;
                }
            }

            var fallback = BuildWebViewUserDataDir(fallbackRoot);
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        internal static string BuildWebViewUserDataDir(string localAppData)
        {
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException(
                    "Системный каталог LocalApplicationData недоступен");
            }

            return Path.Combine(localAppData, "MakeItZBS", "WebView2");
        }

        private static bool TryCreateDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static string LauncherPath
        {
            get
            {
                // Portable dist (раздаётся юзеру) содержит MakeItZBS.exe —
                // его и ищем ПЕРВЫМ, чтобы автозапуск в HKCU Run указывал на
                // .exe. Dev-окружение запускается через Make-it-ZBS.bat (exe
                // там нет рядом с config) — fallback. Раньше искали только
                // .bat → в portable LauncherPath=null → автозапуск молча
                // не записывался (Codex audit P1, 2026-06-09).
                var exe = FindFileUpwards("MakeItZBS.exe");
                if (!string.IsNullOrEmpty(exe))
                {
                    return exe;
                }

                return FindFileUpwards("Make-it-ZBS.bat");
            }
        }

        private static string FindDirectoryUpwards(string directoryName)
        {
            return FindUpwards(directoryName, Directory.Exists);
        }

        private static string FindFileUpwards(string fileName)
        {
            return FindUpwards(fileName, File.Exists);
        }

        private static string FindUpwards(string childName, Func<string, bool> exists)
        {
            var currentDir = GetAssemblyDirectory();

            for (var level = 0; level <= MaxLevels && !string.IsNullOrEmpty(currentDir); level++)
            {
                var candidate = Path.GetFullPath(Path.Combine(currentDir, childName));
                if (exists(candidate))
                {
                    return candidate;
                }

                var parent = Directory.GetParent(currentDir);
                currentDir = parent == null ? null : parent.FullName;
            }

            return null;
        }

        private static string GetAssemblyDirectory()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(location))
            {
                return Path.GetDirectoryName(Path.GetFullPath(location));
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
