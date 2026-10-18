using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    internal static class ConfigurationStore
    {
        internal const string JournalName = ".settings-transaction.json";
        private static readonly string[] FileNames = { "settings.json", "models.json", "prompts.json", ".env" };

        internal static JObject ReadSnapshot(string directory)
        {
            lock (SettingsStore.SyncRoot)
            {
                Recover(directory);
                return new JObject
                {
                    ["settings"] = SettingsStore.LoadJObject(Path.Combine(directory, FileNames[0])),
                    ["models"] = SettingsStore.LoadJObject(Path.Combine(directory, FileNames[1])),
                    ["prompts"] = SettingsStore.LoadJObject(Path.Combine(directory, FileNames[2])),
                    ["apiKey"] = SettingsStore.ReadEnvValue(Path.Combine(directory, ".env"), "OPENROUTER_API_KEY")
                };
            }
        }

        internal static void Save(string directory, IDictionary<string, JObject> updates, string token = null)
        {
            lock (SettingsStore.SyncRoot)
            {
                Recover(directory);
                var contents = PrepareContents(directory, updates, token);
                var previous = new JObject();
                foreach (var name in FileNames)
                {
                    var path = Path.Combine(directory, name);
                    previous[name] = File.Exists(path) ? new JValue(File.ReadAllText(path)) : JValue.CreateNull();
                }

                // Until the journal is removed, readers must recover the previous complete generation.
                var journal = Path.Combine(directory, JournalName);
                SettingsStore.WriteTextAtomic(journal, new JObject { ["version"] = 1, ["files"] = previous }.ToString());
                try
                {
                    foreach (var entry in contents)
                    {
                        SettingsStore.WriteTextAtomic(Path.Combine(directory, entry.Key), entry.Value);
                    }
                    File.Delete(journal);
                    SettingsState.MarkDirty();
                }
                catch
                {
                    Recover(directory);
                    throw;
                }
            }
        }

        private static Dictionary<string, string> PrepareContents(string directory, IDictionary<string, JObject> updates, string token)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in updates)
            {
                if (!FileNames.Take(3).Contains(entry.Key) || entry.Value == null)
                {
                    throw new ArgumentException("Invalid configuration file.", nameof(updates));
                }
                result.Add(entry.Key, entry.Value + Environment.NewLine);
            }
            if (!string.IsNullOrEmpty(token))
            {
                if (token.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                {
                    throw new ArgumentException("Invalid API key.", nameof(token));
                }
                var path = Path.Combine(directory, ".env");
                var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
                lines.RemoveAll(line => line.Split('=')[0].Trim() == "OPENROUTER_API_KEY");
                lines.Add("OPENROUTER_API_KEY=" + token);
                result.Add(".env", string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
            return result;
        }

        internal static void Recover(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            lock (SettingsStore.SyncRoot)
            {
                var journal = Path.Combine(directory, JournalName);
                if (!File.Exists(journal)) return;
                var data = JObject.Parse(File.ReadAllText(journal));
                var files = data["files"] as JObject;
                if ((int?)data["version"] != 1 || files == null || files.Count != FileNames.Length
                    || FileNames.Any(name => files[name] == null
                        || (files[name].Type != JTokenType.String && files[name].Type != JTokenType.Null)))
                {
                    throw new InvalidDataException("Invalid configuration recovery journal.");
                }
                foreach (var name in FileNames)
                {
                    RestoreFile(Path.Combine(directory, name), (string)files[name]);
                }
                File.Delete(journal);
            }
        }

        private static void RestoreFile(string path, string original)
        {
            if (original == null)
            {
                File.Delete(path);
            }
            else if (!File.Exists(path) || File.ReadAllText(path) != original)
            {
                SettingsStore.WriteTextAtomic(path, original);
            }
        }
    }

    [ContextClass("СнимокНастроекПриложения", "ApplicationConfiguration")]
    public sealed class ApplicationConfiguration : AutoContext<ApplicationConfiguration>
    {
        [ScriptConstructor]
        public static ApplicationConfiguration Constructor() => new ApplicationConfiguration();

        [ContextMethod("ПрочитатьСнимокJSON", "ReadSnapshotJson")]
        public string ReadSnapshotJson(string directory)
        {
            if (typeof(ScriptEngine.HostedScript.Library.StructureImpl).Assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name == "DotNetZip"))
                throw new InvalidOperationException("Unsupported legacy ZIP runtime. Start Make it ZBS using its bundled runtime.");
            return ConfigurationStore.ReadSnapshot(directory).ToString();
        }
    }
}
