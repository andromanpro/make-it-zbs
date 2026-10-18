using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal static class AiClientProfiles
    {
        internal const string DefaultContinueText = "Давай дальше.";
        internal const string DefaultHotkey = "Ctrl+F12";

        private static readonly string[] ProfileOrder =
        {
            "codex",
            "claude",
            "opencode"
        };

        public static JObject CreateDefaults(string mode, string continueText, string hotkey)
        {
            return new JObject
            {
                ["codex"] = CreateProfile(
                    "Codex / ChatGPT", new[] { "codex.exe", "chatgpt.exe" },
                    mode, continueText, hotkey),
                ["claude"] = CreateProfile(
                    "Claude", new[] { "claude.exe" }, mode, continueText, hotkey),
                ["opencode"] = CreateProfile(
                    "OpenCode", new[] { "opencode.exe" }, mode, continueText, hotkey)
            };
        }

        public static string ResolveProfileName(JObject aiChat, string executable, string title)
        {
            var profiles = aiChat?["profiles"] as JObject;
            if (profiles == null)
            {
                return string.Empty;
            }

            var normalizedExecutable = (executable ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();

            foreach (var profileName in EnumerateProfileNames(profiles))
            {
                var profile = profiles[profileName] as JObject;
                if (profile == null || !ReadBool(profile, "enabled", true))
                {
                    continue;
                }

                if (MatchesProcess(profile["processes"] as JArray, normalizedExecutable))
                {
                    return profileName;
                }

                if (IsTerminal(normalizedExecutable)
                    && normalizedTitle.IndexOf(profileName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return profileName;
                }
            }

            return string.Empty;
        }

        public static string[] GetDistinctHotkeys(JObject settings)
        {
            var result = new List<string>();
            AddDistinct(result, ReadString(settings?["general"] as JObject, "hotkey", DefaultHotkey));

            var profiles = settings?["aiChat"]?["profiles"] as JObject;
            if (profiles != null)
            {
                foreach (var property in profiles.Properties())
                {
                    var profile = property.Value as JObject;
                    if (profile != null && ReadBool(profile, "enabled", true))
                    {
                        AddDistinct(result, ReadString(profile, "hotkey", DefaultHotkey));
                    }
                }
            }

            return result.ToArray();
        }

        private static JObject CreateProfile(
            string label, IEnumerable<string> processes, string mode, string continueText, string hotkey)
        {
            return new JObject
            {
                ["label"] = label,
                ["enabled"] = true,
                ["processes"] = new JArray(processes),
                ["hotkey"] = NormalizeText(hotkey, DefaultHotkey, 64),
                ["mode"] = NormalizeMode(mode),
                ["continueText"] = NormalizeText(continueText, DefaultContinueText, 1000),
                ["sendMode"] = "submit",
                ["analysisSource"] = "localText"
            };
        }

        private static IEnumerable<string> EnumerateProfileNames(JObject profiles)
        {
            var configuredDefaults = ProfileOrder.Where(name => profiles[name] != null);
            var customProfiles = profiles.Properties()
                .Select(property => property.Name)
                .Where(propertyName => !ProfileOrder.Any(
                    name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase)));
            return configuredDefaults.Concat(customProfiles);
        }

        private static bool MatchesProcess(JArray processes, string executable)
        {
            if (processes == null || string.IsNullOrEmpty(executable))
            {
                return false;
            }

            return processes.Values<string>().Any(token => string.Equals(
                (token ?? string.Empty).Trim(), executable, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTerminal(string executable)
        {
            switch (executable)
            {
                case "windowsterminal.exe":
                case "wezterm-gui.exe":
                case "alacritty.exe":
                case "conhost.exe":
                case "cmd.exe":
                case "powershell.exe":
                case "pwsh.exe":
                    return true;
                default:
                    return false;
            }
        }

        private static void AddDistinct(ICollection<string> values, string value)
        {
            var normalized = NormalizeText(value, string.Empty, 64);
            if (normalized.Length == 0)
            {
                return;
            }

            if (values.Any(existing => string.Equals(
                existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            values.Add(normalized);
        }

        internal static string NormalizeMode(string value)
        {
            return string.Equals(value, "nextStep", StringComparison.Ordinal)
                ? "nextStep"
                : "continue";
        }

        internal static string NormalizeText(string value, string fallback, int maxLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                normalized = fallback;
            }

            return normalized.Length <= maxLength
                ? normalized
                : normalized.Substring(0, maxLength);
        }

        internal static string ReadString(JObject source, string name, string fallback)
        {
            return source?[name]?.Type == JTokenType.String
                ? source.Value<string>(name)
                : fallback;
        }

        internal static bool ReadBool(JObject source, string name, bool fallback)
        {
            return source?[name]?.Type == JTokenType.Boolean
                ? source.Value<bool>(name)
                : fallback;
        }
    }
}
