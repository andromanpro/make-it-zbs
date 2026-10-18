using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class AiClientProfilesTests
    {
        private static JObject CreateSettings()
        {
            var settings = new JObject
            {
                ["general"] = new JObject { ["hotkey"] = "Ctrl+F12" },
                ["aiChat"] = new JObject
                {
                    ["profiles"] = AiClientProfiles.CreateDefaults(
                        "continue", "Дальше", "Ctrl+F12")
                }
            };
            settings["aiChat"]["profiles"]["claude"]["hotkey"] = "Alt+F11";
            return settings;
        }

        [TestCase("codex.exe", "task", "codex")]
        [TestCase("chatgpt.exe", "task", "codex")]
        [TestCase("claude.exe", "task", "claude")]
        [TestCase("opencode.exe", "task", "opencode")]
        [TestCase("WindowsTerminal.exe", "Claude Code", "claude")]
        [TestCase("pwsh.exe", "opencode project", "opencode")]
        public void ResolvesProfileFromProcessOrTerminalTitle(string executable, string title, string expected)
        {
            var settings = CreateSettings();
            Assert.That(AiClientProfiles.ResolveProfileName(
                (JObject)settings["aiChat"], executable, title), Is.EqualTo(expected));
        }

        [Test]
        public void DisabledProfileIsNotResolved()
        {
            var settings = CreateSettings();
            settings["aiChat"]["profiles"]["claude"]["enabled"] = false;
            Assert.That(AiClientProfiles.ResolveProfileName(
                (JObject)settings["aiChat"], "claude.exe", "Claude"), Is.Empty);
        }

        [Test]
        public void ReturnsDistinctEnabledHotkeys()
        {
            var settings = CreateSettings();
            var hotkeys = AiClientProfiles.GetDistinctHotkeys(settings);
            Assert.That(hotkeys, Is.EqualTo(new[] { "Ctrl+F12", "Alt+F11" }));
        }

        [Test]
        public void NormalizeTextUsesFallbackAndMaximumLength()
        {
            Assert.That(AiClientProfiles.NormalizeText("  ", "fallback", 20), Is.EqualTo("fallback"));
            Assert.That(AiClientProfiles.NormalizeText("abcdef", "fallback", 3), Is.EqualTo("abc"));
        }

        [TestCase("wezterm-gui.exe")]
        [TestCase("alacritty.exe")]
        [TestCase("conhost.exe")]
        [TestCase("cmd.exe")]
        [TestCase("powershell.exe")]
        public void ResolvesEverySupportedTerminalByTitle(string executable)
        {
            var settings = CreateSettings();
            Assert.That(AiClientProfiles.ResolveProfileName(
                (JObject)settings["aiChat"], executable, "Codex task"), Is.EqualTo("codex"));
        }

        [Test]
        public void HandlesMissingInvalidAndCustomProfiles()
        {
            Assert.That(AiClientProfiles.ResolveProfileName(null, null, null), Is.Empty);

            var aiChat = JObject.Parse(@"{
                'profiles': {
                    'invalid': 'text',
                    'disabled': {'enabled': false, 'processes': ['disabled.exe']},
                    'custom': {'enabled': true, 'processes': [null, ' Custom.EXE ']}
                }
            }");
            Assert.That(AiClientProfiles.ResolveProfileName(
                aiChat, "custom.exe", null), Is.EqualTo("custom"));
            Assert.That(AiClientProfiles.ResolveProfileName(
                aiChat, null, "custom"), Is.Empty);
        }

        [Test]
        public void HotkeysSkipEmptyDisabledAndDuplicateValues()
        {
            var settings = JObject.Parse(@"{
                'general': {'hotkey': ''},
                'aiChat': {'profiles': {
                    'empty': {'enabled': true, 'hotkey': ''},
                    'duplicate': {'enabled': true, 'hotkey': 'ctrl+f12'},
                    'disabled': {'enabled': false, 'hotkey': 'Alt+F11'},
                    'invalid': 'text'
                }}
            }");
            Assert.That(AiClientProfiles.GetDistinctHotkeys(settings),
                Is.EqualTo(new[] { "ctrl+f12" }));
            Assert.That(AiClientProfiles.GetDistinctHotkeys(null),
                Is.EqualTo(new[] { AiClientProfiles.DefaultHotkey }));
            Assert.That(AiClientProfiles.NormalizeMode("nextStep"), Is.EqualTo("nextStep"));
            Assert.That(AiClientProfiles.NormalizeText(null, "fallback", 20), Is.EqualTo("fallback"));
            Assert.That(AiClientProfiles.ReadString(null, "value", "fallback"), Is.EqualTo("fallback"));
            Assert.That(AiClientProfiles.ReadBool(
                new JObject { ["value"] = "true" }, "value", false), Is.False);
        }
    }
}
