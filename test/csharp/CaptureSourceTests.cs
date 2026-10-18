using System;
using System.IO;
using NUnit.Framework;
using ScriptEngine.Machine;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class CaptureSourceTests
    {
        [Test]
        public void CaptureRejectsFocusChangeHandleReuseAndUnknownProcess()
        {
            var window = new IntPtr(42);
            Assert.That(CaptureSource.Matches(window, 7, window, 7), Is.True);
            Assert.That(CaptureSource.Matches(window, 7, new IntPtr(43), 7), Is.False);
            Assert.That(CaptureSource.Matches(window, 7, window, 8), Is.False);
            Assert.That(CaptureSource.Matches(IntPtr.Zero, 7, IntPtr.Zero, 7), Is.False);
            Assert.That(CaptureSource.Matches(window, 0, window, 0), Is.False);
        }

        [Test]
        public void ClipboardRestoreDoesNotOverwriteANewerUserCopy()
        {
            Assert.That(CaptureSource.ShouldRestoreClipboard(10, 0, 10), Is.True);
            Assert.That(CaptureSource.ShouldRestoreClipboard(10, 11, 11), Is.True);
            Assert.That(CaptureSource.ShouldRestoreClipboard(10, 11, 12), Is.False);
            Assert.That(CaptureSource.ShouldRestoreClipboard(10, 0, 11), Is.False);
            Assert.That(CaptureSource.ShouldRestoreClipboard(0, 0, 0), Is.False);
        }

        [Test]
        public void InvalidTargetIsRejectedBeforeCopyingTextOrSavingAScreenshot()
        {
            var path = Path.Combine(Path.GetTempPath(), "zbs-invalid-source-" + Guid.NewGuid().ToString("N") + ".png");
            Assert.That(() => new WindowInfo().GetSelectedText(1, int.MaxValue), Throws.TypeOf<RuntimeException>());
            Assert.That(() => new WindowInfo().GetOneCModuleText(1, int.MaxValue), Throws.TypeOf<RuntimeException>());
            Assert.That(() => new WindowInfo().GetOneCModuleFullText(1, int.MaxValue), Throws.TypeOf<RuntimeException>());
            Assert.That(() => new ScreenCapture().CaptureWindow(path, 1, int.MaxValue), Throws.TypeOf<RuntimeException>());
            Assert.That(File.Exists(path), Is.False);
        }
    }
}
