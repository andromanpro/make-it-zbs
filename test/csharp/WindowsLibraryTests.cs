using System;
using NUnit.Framework;
using OneScript.Windows;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class WindowsLibraryTests
    {
        [Test]
        public void InvalidTextAndVersionsAreRejectedBeforeClipboardAccess()
        {
            var clipboard = new WindowsClipboard();
            Assert.Throws<ArgumentNullException>(() => clipboard.WriteText(null));
            Assert.Throws<ArgumentException>(() => clipboard.WriteText("prefix\0suffix"));
            Assert.Throws<ArgumentException>(() => clipboard.WriteText(new string('x', 4 * 1024 * 1024 + 1)));
            foreach (var version in new[] { -2m, 0.5m, uint.MaxValue + 1m })
                Assert.Throws<ArgumentOutOfRangeException>(() => clipboard.WriteText("Must not write", version));
        }
    }
}
