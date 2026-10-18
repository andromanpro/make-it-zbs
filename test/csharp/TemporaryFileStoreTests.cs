using System;
using System.IO;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture, NonParallelizable]
    public class TemporaryFileStoreTests
    {
        private string _directory;

        [SetUp]
        public void SetUp() => _directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "zbs-temp-retention-" + Guid.NewGuid().ToString("N"))).FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, true);

        [Test]
        public void CleanupBoundsEachCaptureKindAndLeavesUnrelatedFilesAlone()
        {
            foreach (var prefix in new[] { "screenshot", "clipboard_image", "capture-worker" })
            {
                File.WriteAllText(Path.Combine(_directory, prefix + "_old.png"), "old");
                File.SetLastWriteTimeUtc(Path.Combine(_directory, prefix + "_old.png"), DateTime.UtcNow.AddDays(-1));
                File.WriteAllText(Path.Combine(_directory, prefix + "_new.png"), "new");
            }
            File.WriteAllText(Path.Combine(_directory, "personal.png"), "unrelated");
            Assert.That(TemporaryFileStore.Cleanup(_directory, 1), Is.EqualTo(3));
            Assert.That(Directory.GetFiles(_directory, "*_old.png"), Is.Empty);
            Assert.That(Directory.GetFiles(_directory, "*_new.png").Length, Is.EqualTo(3));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "personal.png")), Is.EqualTo("unrelated"));
        }

        [Test]
        public void ActiveLeaseSurvivesRetentionAndCrashOrphanIsRemovedAfterLeaseRelease()
        {
            var path = Path.Combine(_directory, "screenshot_active.png");
            File.WriteAllText(path, "synthetic pixels");
            using (new FileStream(path + ".lease", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(TemporaryFileStore.Cleanup(_directory, 0), Is.Zero);
                Assert.That(File.Exists(path), Is.True);
            }
            Assert.That(TemporaryFileStore.Cleanup(_directory, 0), Is.EqualTo(1));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public void CleanupRemovesLeaseLeftByCrashBeforeCaptureFileWasCreated()
        {
            File.WriteAllText(Path.Combine(_directory, "capture-worker_orphan.png.lease"), "");
            Assert.That(TemporaryFileStore.Cleanup(_directory, 0), Is.Zero);
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
            Assert.Throws<ArgumentOutOfRangeException>(() => TemporaryFileStore.Cleanup(_directory, -1));
        }

        [Test]
        public void LockedCaptureAndSubdirectoriesAreNotDeletedOrTraversed()
        {
            var path = Path.Combine(_directory, "clipboard_image_locked.png");
            var nested = Directory.CreateDirectory(Path.Combine(_directory, "nested")).FullName;
            var nestedFile = Path.Combine(nested, "screenshot_unrelated.png");
            File.WriteAllText(nestedFile, "unrelated nested data");
            using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Assert.That(TemporaryFileStore.Cleanup(_directory, 0), Is.Zero);
            Assert.That(TemporaryFileStore.Cleanup(_directory, 0), Is.EqualTo(1));
            Assert.That(File.ReadAllText(nestedFile), Is.EqualTo("unrelated nested data"));
            Assert.That(TemporaryFileStore.Cleanup(Path.Combine(_directory, "missing"), 0), Is.Zero);
        }
    }
}
