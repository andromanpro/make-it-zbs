using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal static class UiText
    {
        internal const string Russian = "ru";
        internal const string English = "en";

        internal static string NormalizeLanguage(string value)
        {
            return string.Equals(value, English, StringComparison.OrdinalIgnoreCase)
                ? English
                : Russian;
        }

        internal static string CurrentLanguage()
        {
            try
            {
                var configDir = ProjectPaths.ConfigDir;
                if (string.IsNullOrEmpty(configDir))
                {
                    return Russian;
                }

                var path = Path.Combine(configDir, "settings.json");
                if (!File.Exists(path))
                {
                    return Russian;
                }

                var settings = SettingsStore.LoadJObject(path);
                var general = settings["general"] as JObject;
                return NormalizeLanguage(general?["language"]?.Value<string>());
            }
            catch
            {
                return Russian;
            }
        }

        internal static string Get(string language, string russian, string english)
        {
            return NormalizeLanguage(language) == English ? english : russian;
        }

        internal static string Current(string russian, string english)
        {
            return Get(CurrentLanguage(), russian, english);
        }
    }
}
