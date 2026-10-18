using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class ReplacementBackupStoreTests
    {
        private string _root;
        private ReplacementBackupStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zbs-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = ReplacementBackupStore.Constructor();
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        [TestCase("")]
        [TestCase("private text\r\nwith tabs\tand trailing spaces  ")]
        [TestCase("Исходный модуль\r\nСтрока = \"данные\";\n\U0001F680")]
        public void RealCurrentUserDpapiRoundTripDoesNotPersistSource(string original)
        {
            var directory = Path.Combine(_root, "nested");
            var path = _store.Save(directory, original, "OneCCode");
            Assert.That(Path.GetExtension(path), Is.EqualTo(".zbsbackup"));
            Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { path }));
            Assert.That(_store.Read(path), Is.EqualTo(original));
            if (original.Length > 0)
            {
                var bytes = File.ReadAllBytes(path);
                Assert.That(Encoding.UTF8.GetString(bytes), Does.Not.Contain(original));
                Assert.That(Encoding.Unicode.GetString(bytes), Does.Not.Contain(original));
            }
            var exported = Path.Combine(_root, "explicit-recovery.txt");
            _store.RestoreToFile(path, exported);
            Assert.That(File.ReadAllBytes(exported), Is.EqualTo(new UTF8Encoding(false, true).GetBytes(original)));
        }

        [Test]
        public void SameInputCreatesIndependentRandomizedBackups()
        {
            var first = _store.Save(_root, "same source", "Word");
            var second = _store.Save(_root, "same source", "Word");
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(File.ReadAllBytes(second), Is.Not.EqualTo(File.ReadAllBytes(first)));
            Assert.That(_store.Read(first), Is.EqualTo(_store.Read(second)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("../escape")]
        [TestCase("Word\\other")]
        [TestCase("A................................")]
        public void UnsafeContextCannotCreateFiles(string context)
        {
            Assert.Throws<ArgumentException>(() => _store.Save(_root, "secret", context));
            Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void OversizeNullAndInvalidUnicodeAreRejectedBeforeDiskWrite()
        {
            Assert.Throws<ArgumentNullException>(() => _store.Save(_root, null, "Word"));
            Assert.Throws<ArgumentException>(() => _store.Save(_root, new string('x', ReplacementBackupStore.MaximumTextBytes + 1), "Word"));
            Assert.Throws<ArgumentException>(() => _store.Save(_root, new string('я', ReplacementBackupStore.MaximumTextBytes / 2 + 1), "Word"));
            Assert.Throws<EncoderFallbackException>(() => _store.Save(_root, "\uD800", "Word"));
            Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void DirectoryFailureDoesNotCreateBackupOrChangeExistingFile()
        {
            var blocker = Path.Combine(_root, "blocked");
            File.WriteAllText(blocker, "existing unrelated file");
            Assert.Throws<IOException>(() => _store.Save(Path.Combine(blocker, "backups"), "secret", "Word"));
            Assert.That(File.ReadAllText(blocker), Is.EqualTo("existing unrelated file"));
            Assert.That(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { blocker }));
        }

        [Test]
        public void RecoveryNeverOverwritesAnExistingDestinationOrBackup()
        {
            var path = _store.Save(_root, "original", "Word");
            var encrypted = File.ReadAllBytes(path);
            var destination = Path.Combine(_root, "existing.txt");
            File.WriteAllText(destination, "keep me");
            Assert.Throws<IOException>(() => _store.RestoreToFile(path, destination));
            Assert.Throws<IOException>(() => _store.RestoreToFile(path, path));
            Assert.That(File.ReadAllText(destination), Is.EqualTo("keep me"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(encrypted));
        }

        [TestCase(0)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(100)]
        public void ModifiedOrTruncatedBackupCannotBeRecovered(int alteration)
        {
            var path = _store.Save(_root, "confidential text", "Word");
            var bytes = File.ReadAllBytes(path);
            if (alteration == 100) bytes = bytes.Take(bytes.Length - 20).ToArray();
            else bytes[alteration] ^= 0x40;
            File.WriteAllBytes(path, bytes);
            var destination = Path.Combine(_root, "must-not-exist.txt");
            Assert.That(() => _store.RestoreToFile(path, destination), Throws.Exception);
            Assert.That(File.Exists(destination), Is.False);
        }

        [Test]
        public void PlaintextLegacyAndOversizeFilesAreNotTreatedAsProtectedBackups()
        {
            var path = Path.Combine(_root, "legacy.txt");
            File.WriteAllText(path, "original plaintext from a legacy backup");
            Assert.Throws<InvalidDataException>(() => _store.Read(path));
            using (var stream = File.OpenWrite(path)) stream.SetLength(ReplacementBackupStore.MaximumTextBytes + 8192L);
            Assert.Throws<InvalidDataException>(() => _store.Read(path));
        }
    }
}
