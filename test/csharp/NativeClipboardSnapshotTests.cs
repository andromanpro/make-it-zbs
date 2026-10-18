using System;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class NativeClipboardSnapshotTests
    {
        [Test]
        public void InvalidFormatListsCannotOpenOrModifyTheClipboard()
        {
            Assert.Throws<ArgumentException>(() => NativeClipboardSnapshot.Capture(null, 0));
            Assert.Throws<ArgumentException>(() => NativeClipboardSnapshot.Capture(Array.Empty<int>(), 0));
            Assert.Throws<ArgumentException>(() => NativeClipboardSnapshot.Capture(new[] { 0 }, 0));
            Assert.Throws<ArgumentException>(() => NativeClipboardSnapshot.Capture(new[] { 65536 }, 0));
            Assert.Throws<ArgumentException>(() => NativeClipboardSnapshot.Capture(new int[257], 0));
        }

        [Test]
        public void ADisposedSnapshotCannotEmptyTheClipboard()
        {
            using (var snapshot = new NativeClipboardSnapshot())
            {
                snapshot.Dispose();
                Assert.Throws<ObjectDisposedException>(() => snapshot.Restore(0));
            }
        }
    }
}
