using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class SettingsStoreConcurrencyTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zbs-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        [Test]
        public void ConcurrentJsonWritersPublishOnlyCompleteDocuments()
        {
            var path = Path.Combine(_root, "models.json");
            Parallel.For(0, 30, i => SettingsStore.SaveJsonAtomic(path,
                new JObject { ["id"] = i, ["value"] = new string((char)('a' + i % 26), 10000) }));

            var result = SettingsStore.LoadJObject(path);
            Assert.That(result.Value<string>("value"),
                Is.EqualTo(new string((char)('a' + result.Value<int>("id") % 26), 10000)));
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
        }

        [Test]
        public void ConcurrentEnvUpdatesDoNotLoseOtherKeys()
        {
            var path = Path.Combine(_root, ".env");
            Parallel.For(0, 20, i => SettingsStore.WriteEnvValue(path, "KEY_" + i, "value_" + i));

            foreach (var i in Enumerable.Range(0, 20))
            {
                Assert.That(SettingsStore.ReadEnvValue(path, "KEY_" + i), Is.EqualTo("value_" + i));
            }
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
        }

        [Test]
        public void SavingDoesNotOverwriteAnotherWritersStagingFile()
        {
            var path = Path.Combine(_root, "models.json");
            File.WriteAllText(path + ".tmp", "another writer");
            SettingsStore.SaveJsonAtomic(path, new JObject { ["value"] = 42 });

            Assert.That(File.ReadAllText(path + ".tmp"), Is.EqualTo("another writer"));
            Assert.That(SettingsStore.LoadJObject(path).Value<int>("value"), Is.EqualTo(42));
        }

        [Test]
        public void FailedReplacementLeavesTheOriginalAndRemovesOnlyItsOwnTemporaryFile()
        {
            var path = Path.Combine(_root, "models.json");
            File.WriteAllText(path, "{\"value\":42}");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => SettingsStore.SaveJsonAtomic(path, new JObject { ["value"] = 99 }));
            }

            Assert.That(SettingsStore.LoadJObject(path).Value<int>("value"), Is.EqualTo(42));
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
        }
    }
}
