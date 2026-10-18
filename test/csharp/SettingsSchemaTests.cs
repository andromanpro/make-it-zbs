using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class SettingsSchemaTests
    {
        [Test]
        public void MigratesLegacyAiChatIntoAllProfiles()
        {
            var settings = JObject.Parse(@"{
                'general': {'hotkey': 'Alt+F10'},
                'aiChat': {'mode': 'nextStep', 'continueText': 'Продолжай'}
            }");

            Assert.That(SettingsSchema.MigrateAndNormalize(settings), Is.True);
            Assert.That(settings.Value<int>("schemaVersion"), Is.EqualTo(2));
            Assert.That(settings["aiChat"]["mode"], Is.Null);
            Assert.That(settings["aiChat"]["profiles"]["codex"]["mode"].Value<string>(),
                Is.EqualTo("nextStep"));
            Assert.That(settings["aiChat"]["profiles"]["claude"]["continueText"].Value<string>(),
                Is.EqualTo("Продолжай"));
            Assert.That(settings["aiChat"]["profiles"]["opencode"]["hotkey"].Value<string>(),
                Is.EqualTo("Alt+F10"));
        }

        [Test]
        public void NormalizesInvalidProfileValues()
        {
            var settings = JObject.Parse(@"{
                'schemaVersion': 2,
                'general': {'hotkey': ''},
                'aiChat': {'profiles': {'codex': {
                    'enabled': 'yes', 'processes': ['CODEX.EXE', 'codex.exe', ''],
                    'mode': 'invalid', 'continueText': '', 'sendMode': 'invalid',
                    'analysisSource': 'invalid'
                }}}
            }");

            SettingsSchema.MigrateAndNormalize(settings);
            var profile = (JObject)settings["aiChat"]["profiles"]["codex"];
            Assert.Multiple(() =>
            {
                Assert.That(settings["general"]["hotkey"].Value<string>(), Is.EqualTo("Ctrl+F12"));
                Assert.That(profile.Value<bool>("enabled"), Is.True);
                Assert.That(profile["processes"], Has.Count.EqualTo(1));
                Assert.That(profile["processes"][0].Value<string>(), Is.EqualTo("codex.exe"));
                Assert.That(profile.Value<string>("mode"), Is.EqualTo("continue"));
                Assert.That(profile.Value<string>("continueText"), Is.EqualTo("Давай дальше."));
                Assert.That(profile.Value<string>("sendMode"), Is.EqualTo("submit"));
                Assert.That(profile.Value<string>("analysisSource"), Is.EqualTo("localText"));
            });
        }

        [Test]
        public void RejectsFutureSchemaVersion()
        {
            var settings = JObject.Parse("{'schemaVersion': 99}");
            Assert.That(() => SettingsSchema.MigrateAndNormalize(settings),
                Throws.TypeOf<InvalidOperationException>());
        }

        [TestCase("'2'")]
        [TestCase("0")]
        public void RejectsMalformedSchemaVersion(string version)
        {
            var settings = JObject.Parse("{'schemaVersion': " + version + "}");
            Assert.That(() => SettingsSchema.MigrateAndNormalize(settings),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void RejectsNullSettingsAndIsIdempotentAfterMigration()
        {
            Assert.That(() => SettingsSchema.MigrateAndNormalize(null),
                Throws.TypeOf<ArgumentNullException>());

            var settings = new JObject();
            Assert.That(SettingsSchema.MigrateAndNormalize(settings), Is.True);
            Assert.That(SettingsSchema.MigrateAndNormalize(settings), Is.False);
        }

        [Test]
        public void RecreatesObjectsAndNormalizesCustomProfileEdges()
        {
            var settings = JObject.Parse(@"{
                'schemaVersion': 2,
                'general': 'invalid',
                'aiChat': {'profiles': {
                    'codex': 'invalid',
                    'custom': {
                        'label': null,
                        'enabled': null,
                        'processes': [],
                        'hotkey': null,
                        'mode': 'nextStep',
                        'continueText': null,
                        'sendMode': 'draft',
                        'analysisSource': 'screenshot'
                    },
                    'filtered': {
                        'processes': [null, '', 'ok.exe', 'OK.EXE', '" + new string('x', 129) + @"']
                    }
                }}
            }");

            SettingsSchema.MigrateAndNormalize(settings);

            Assert.Multiple(() =>
            {
                Assert.That(settings["general"], Is.TypeOf<JObject>());
                Assert.That(settings["aiChat"]["profiles"]["codex"], Is.TypeOf<JObject>());
                Assert.That(settings["aiChat"]["profiles"]["custom"]["processes"], Is.Empty);
                Assert.That(settings["aiChat"]["profiles"]["custom"]["sendMode"].Value<string>(),
                    Is.EqualTo("draft"));
                Assert.That(settings["aiChat"]["profiles"]["custom"]["analysisSource"].Value<string>(),
                    Is.EqualTo("screenshot"));
                Assert.That(settings["aiChat"]["profiles"]["filtered"]["processes"],
                    Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void SettingsStoreMigratesAtomicallyAndKeepsOneBackup()
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-settings-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            try
            {
                File.WriteAllText(path, "{\"general\":{\"hotkey\":\"Ctrl+F12\"},\"aiChat\":{\"mode\":\"continue\"}}");
                var loaded = SettingsStore.LoadJObject(path);
                Assert.That(loaded.Value<int>("schemaVersion"), Is.EqualTo(2));
                Assert.That(File.Exists(path + ".v1.bak"), Is.True);

                var backup = File.ReadAllText(path + ".v1.bak");
                SettingsStore.LoadJObject(path);
                Assert.That(File.ReadAllText(path + ".v1.bak"), Is.EqualTo(backup));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
