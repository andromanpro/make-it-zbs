using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class ConfigurationStoreTests
    {
        private string _directory;
        private static Dictionary<string, JObject> Generation(int value) => new Dictionary<string, JObject>
        {
            ["settings.json"] = new JObject { ["schemaVersion"] = 2, ["generation"] = value },
            ["models.json"] = new JObject { ["generation"] = value },
            ["prompts.json"] = new JObject { ["generation"] = value }
        };

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "zbs-config-" + Guid.NewGuid().ToString("N"));
            ConfigurationStore.Save(_directory, Generation(1), "fixture-1");
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, true);

        [TestCase("settings.json")]
        [TestCase("models.json")]
        [TestCase("prompts.json")]
        [TestCase(".env")]
        public void FailedWriteAtAnyStageRollsBackFilesAndCredential(string lockedFile)
        {
            using (File.Open(Path.Combine(_directory, lockedFile), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => ConfigurationStore.Save(_directory, Generation(2), "fixture-2"));
            }
            AssertGeneration(1);
            Assert.That(File.Exists(Path.Combine(_directory, ConfigurationStore.JournalName)), Is.False);
        }

        [Test]
        public void SnapshotRecoversInterruptedCommitBeforeReadingAnything()
        {
            var previous = new JObject();
            foreach (var name in new[] { "settings.json", "models.json", "prompts.json", ".env" })
                previous[name] = File.ReadAllText(Path.Combine(_directory, name));
            var journal = Path.Combine(_directory, ConfigurationStore.JournalName);
            File.WriteAllText(journal, new JObject { ["version"] = 1, ["files"] = previous }.ToString());
            File.WriteAllText(Path.Combine(_directory, "models.json"), Generation(9)["models.json"].ToString());
            File.WriteAllText(Path.Combine(_directory, ".env"), "OPENROUTER_API_KEY=fixture-9");

            AssertGeneration(1);
            Assert.That(File.Exists(journal), Is.False);
        }

        [Test]
        public void RecoveryRemovesFilesThatDidNotExistBeforeTransaction()
        {
            var previous = new JObject();
            foreach (var name in new[] { "settings.json", "models.json", "prompts.json", ".env" })
                previous[name] = JValue.CreateNull();
            File.WriteAllText(Path.Combine(_directory, ConfigurationStore.JournalName),
                new JObject { ["version"] = 1, ["files"] = previous }.ToString());
            ConfigurationStore.Recover(_directory);
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public void InvalidJournalRefusesMixedSnapshotAndDoesNotTraversePaths()
        {
            var journal = Path.Combine(_directory, ConfigurationStore.JournalName);
            File.WriteAllText(journal, "{\"version\":1,\"files\":{\"../outside.json\":null}}");
            Assert.Throws<InvalidDataException>(() => ConfigurationStore.ReadSnapshot(_directory));
            Assert.That(File.Exists(journal), Is.True);
            Assert.That(File.Exists(Path.Combine(_directory, "models.json")), Is.True);
        }

        [TestCase("../outside.json", "")]
        [TestCase("models.json", "invalid\nINJECTED=value")]
        public void InvalidUpdatesLeaveOldGenerationUntouched(string file, string token)
        {
            Assert.Throws<ArgumentException>(() => ConfigurationStore.Save(_directory,
                new Dictionary<string, JObject> { [file] = new JObject() }, token));
            AssertGeneration(1);
        }

        [Test]
        public void ConcurrentReadersNeverObserveMixedGenerations()
        {
            Parallel.For(2, 25, value =>
            {
                ConfigurationStore.Save(_directory, Generation(value), "fixture-" + value);
                var snapshot = ConfigurationStore.ReadSnapshot(_directory);
                var expected = (int)snapshot["settings"]["generation"];
                Assert.That((int)snapshot["models"]["generation"], Is.EqualTo(expected));
                Assert.That((int)snapshot["prompts"]["generation"], Is.EqualTo(expected));
                Assert.That((string)snapshot["apiKey"], Is.EqualTo("fixture-" + expected));
            });
        }

        [Test]
        public void EmptyTokenPreservesCredentialAndWrapperReturnsWholeSnapshot()
        {
            ConfigurationStore.Save(_directory, Generation(2), "");
            var result = JObject.Parse(ApplicationConfiguration.Constructor().ReadSnapshotJson(_directory));
            Assert.That((string)result["apiKey"], Is.EqualTo("fixture-1"));
            Assert.That((int)result["prompts"]["generation"], Is.EqualTo(2));
        }

        private void AssertGeneration(int expected)
        {
            var snapshot = ConfigurationStore.ReadSnapshot(_directory);
            foreach (var key in new[] { "settings", "models", "prompts" })
                Assert.That((int)snapshot[key]["generation"], Is.EqualTo(expected), key);
            Assert.That((string)snapshot["apiKey"], Is.EqualTo("fixture-" + expected));
        }
    }
}
