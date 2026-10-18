using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal static class SettingsSchema
    {
        internal const int CurrentVersion = 2;
        private const string ContinueTextKey = "continueText";
        private const string HotkeyKey = "hotkey";

        public static bool MigrateAndNormalize(JObject settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            var before = settings.ToString(Newtonsoft.Json.Formatting.None);
            var version = ReadVersion(settings);
            if (version > CurrentVersion)
            {
                throw new InvalidOperationException(
                    "Версия settings.json " + version + " новее поддерживаемой " + CurrentVersion + ".");
            }

            var general = GetOrCreateObject(settings, "general");
            var hotkey = AiClientProfiles.NormalizeText(
                AiClientProfiles.ReadString(general, HotkeyKey, AiClientProfiles.DefaultHotkey),
                AiClientProfiles.DefaultHotkey, 64);
            general[HotkeyKey] = hotkey;
            general["language"] = UiText.NormalizeLanguage(
                AiClientProfiles.ReadString(general, "language", UiText.Russian));
            general["codexOfferShown"] = AiClientProfiles.ReadBool(
                general, "codexOfferShown", false);
            if (general[OperationFeedbackOptions.Key] != null)
                general[OperationFeedbackOptions.Key] = OperationFeedbackOptions.Normalize(
                    AiClientProfiles.ReadString(general, OperationFeedbackOptions.Key, "both"));

            var aiChat = GetOrCreateObject(settings, "aiChat");
            var legacyMode = AiClientProfiles.NormalizeMode(
                AiClientProfiles.ReadString(aiChat, "mode", "continue"));
            var legacyText = AiClientProfiles.NormalizeText(
                AiClientProfiles.ReadString(aiChat, ContinueTextKey, AiClientProfiles.DefaultContinueText),
                AiClientProfiles.DefaultContinueText, 1000);

            var profiles = aiChat["profiles"] as JObject;
            if (profiles == null)
            {
                profiles = AiClientProfiles.CreateDefaults(legacyMode, legacyText, hotkey);
                aiChat["profiles"] = profiles;
            }

            NormalizeProfiles(profiles, legacyMode, legacyText, hotkey);
            aiChat.Remove("mode");
            aiChat.Remove(ContinueTextKey);
            settings["schemaVersion"] = CurrentVersion;

            var after = settings.ToString(Newtonsoft.Json.Formatting.None);
            return !string.Equals(before, after, StringComparison.Ordinal);
        }

        private static void NormalizeProfiles(
            JObject profiles, string fallbackMode, string fallbackText, string fallbackHotkey)
        {
            var defaults = AiClientProfiles.CreateDefaults(fallbackMode, fallbackText, fallbackHotkey);
            foreach (var defaultProperty in defaults.Properties())
            {
                if (!(profiles[defaultProperty.Name] is JObject))
                {
                    profiles[defaultProperty.Name] = defaultProperty.Value.DeepClone();
                }
            }

            foreach (var property in profiles.Properties())
            {
                var profile = property.Value as JObject;
                if (profile == null)
                {
                    continue;
                }

                var defaultProfile = defaults[property.Name] as JObject;
                profile["label"] = AiClientProfiles.NormalizeText(
                    AiClientProfiles.ReadString(profile, "label", property.Name), property.Name, 80);
                profile["enabled"] = AiClientProfiles.ReadBool(profile, "enabled", true);
                profile["processes"] = NormalizeProcesses(
                    profile["processes"] as JArray, defaultProfile?["processes"] as JArray);
                profile[HotkeyKey] = AiClientProfiles.NormalizeText(
                    AiClientProfiles.ReadString(profile, HotkeyKey, fallbackHotkey), fallbackHotkey, 64);
                profile["mode"] = AiClientProfiles.NormalizeMode(
                    AiClientProfiles.ReadString(profile, "mode", fallbackMode));
                profile[ContinueTextKey] = AiClientProfiles.NormalizeText(
                    AiClientProfiles.ReadString(profile, ContinueTextKey, fallbackText), fallbackText, 1000);
                profile["sendMode"] = NormalizeEnum(
                    AiClientProfiles.ReadString(profile, "sendMode", "submit"), "submit", "draft");
                profile["analysisSource"] = NormalizeEnum(
                    AiClientProfiles.ReadString(profile, "analysisSource", "localText"),
                    "localText", "screenshot");
            }
        }

        private static JArray NormalizeProcesses(JArray source, JArray fallback)
        {
            var result = new JArray();
            var input = source != null && source.Count > 0 ? source : fallback;
            if (input == null)
            {
                return result;
            }

            foreach (var value in input.Values<string>())
            {
                var process = (value ?? string.Empty).Trim().ToLowerInvariant();
                if (process.Length > 0 && process.Length <= 128 && !Contains(result, process))
                {
                    result.Add(process);
                }
            }

            return result;
        }

        private static bool Contains(JArray values, string expected)
        {
            return values.Values<string>().Any(
                value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeEnum(string value, string first, string second)
        {
            return string.Equals(value, second, StringComparison.Ordinal) ? second : first;
        }

        private static int ReadVersion(JObject settings)
        {
            var token = settings["schemaVersion"];
            if (token == null)
            {
                return 1;
            }

            if (token.Type != JTokenType.Integer || token.Value<int>() < 1)
            {
                throw new InvalidOperationException("Некорректный schemaVersion в settings.json.");
            }

            return token.Value<int>();
        }

        private static JObject GetOrCreateObject(JObject parent, string name)
        {
            var result = parent[name] as JObject;
            if (result != null)
            {
                return result;
            }

            result = new JObject();
            parent[name] = result;
            return result;
        }
    }
}
