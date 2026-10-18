using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    public static class SettingsStore
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "MakeItZBS";
        private static readonly object WriteLock = new object();
        internal static object SyncRoot => WriteLock;

        public static JObject LoadJObject(string path)
        {
            lock (WriteLock)
            {
                ConfigurationStore.Recover(Path.GetDirectoryName(path));
                return LoadJObjectCore(path);
            }
        }

        private static JObject LoadJObjectCore(string path)
        {
            JObject result;
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
            using (var jsonReader = new JsonTextReader(reader))
            {
                // DateParseHandling.None: иначе Newtonsoft превратит любую
                // date-подобную строку в DateTime и переформатирует при save.
                // FloatParseHandling.Decimal: согласовано с NumericUpDown.
                jsonReader.DateParseHandling = DateParseHandling.None;
                jsonReader.FloatParseHandling = FloatParseHandling.Decimal;
                result = JObject.Load(jsonReader);
            }

            if (string.Equals(Path.GetFileName(path), "settings.json", StringComparison.OrdinalIgnoreCase)
                && SettingsSchema.MigrateAndNormalize(result))
            {
                var backupPath = path + ".v1.bak";
                if (!File.Exists(backupPath))
                {
                    File.Copy(path, backupPath);
                }

                SaveJsonAtomic(path, result);
            }

            return result;
        }

        public static void SaveJsonAtomic(string targetPath, JObject json)
        {
            var content = json.ToString(Formatting.Indented) + Environment.NewLine;
            lock (WriteLock)
            {
                ConfigurationStore.Recover(Path.GetDirectoryName(targetPath));
                WriteAtomic(targetPath, tempPath =>
                {
                    File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                    using (var verifyReader = new StreamReader(tempPath, Encoding.UTF8))
                    using (var verifyJson = new JsonTextReader(verifyReader))
                    {
                        verifyJson.DateParseHandling = DateParseHandling.None;
                        JObject.Load(verifyJson);
                    }
                });
            }
        }

        internal static void WriteTextAtomic(string path, string content)
        {
            WriteAtomic(path, temporary =>
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            });
        }

        private static void WriteAtomic(string targetPath, Action<string> write)
        {
            EnsureParentDirectory(targetPath);
            var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                write(tempPath);
                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Replace(tempPath, targetPath, null);
                    }
                    else
                    {
                        File.Move(tempPath, targetPath);
                    }
                }
                catch (FileNotFoundException) when (File.Exists(tempPath) && !File.Exists(targetPath))
                {
                    // The destination was removed after the existence check.
                    File.Move(tempPath, targetPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        public static bool IsAutostartEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }

        public static void SetAutostart(bool enabled, string launcherPath)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("не удалось открыть HKCU Run");
                }

                if (enabled)
                {
                    key.SetValue(RunValueName, "\"" + launcherPath + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
        }

        public static string ReadEnvValue(string envPath, string key)
        {
            lock (WriteLock)
            {
                ConfigurationStore.Recover(Path.GetDirectoryName(envPath));
                return ReadEnvValueCore(envPath, key);
            }
        }

        private static string ReadEnvValueCore(string envPath, string key)
        {
            if (string.IsNullOrEmpty(envPath) || string.IsNullOrEmpty(key) || !File.Exists(envPath))
            {
                return string.Empty;
            }

            foreach (var rawLine in File.ReadAllLines(envPath, Encoding.UTF8))
            {
                var line = rawLine == null ? string.Empty : rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var name = line.Substring(0, separatorIndex).Trim();
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    return line.Substring(separatorIndex + 1).Trim();
                }
            }

            return string.Empty;
        }

        public static void WriteEnvValue(string envPath, string key, string value)
        {
            ValidateEnvArguments(envPath, key);

            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (WriteLock)
            {
                ConfigurationStore.Recover(Path.GetDirectoryName(envPath));
                var lines = ReadEnvLines(envPath);
                UpsertEnvLine(lines, key, value);
                WriteAtomic(envPath, tempPath =>
                    File.WriteAllLines(tempPath, lines.ToArray(), new UTF8Encoding(false)));
            }
        }

        private static void ValidateEnvArguments(string envPath, string key)
        {
            if (string.IsNullOrEmpty(envPath))
            {
                throw new ArgumentException("envPath не может быть пустым", "envPath");
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("key не может быть пустым", "key");
            }
        }

        private static List<string> ReadEnvLines(string envPath)
        {
            return File.Exists(envPath)
                ? new List<string>(File.ReadAllLines(envPath, Encoding.UTF8))
                : new List<string>();
        }

        private static void UpsertEnvLine(List<string> lines, string key, string value)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var name = trimmed.Substring(0, separatorIndex).Trim();
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    lines[i] = key + "=" + value;
                    return;
                }
            }

            lines.Add(key + "=" + value);
        }

        private static void EnsureParentDirectory(string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

    }
}
