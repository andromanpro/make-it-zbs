using System;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;
using OneScript.Windows;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    [TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
    public class WindowsLibraryUiTests
    {
        [OneTimeTearDown]
        public void StopLibraryApartment() => new WindowsClipboard().Shutdown();

        [Test]
        public void IndependentClipboardApiPreservesFormatsAndRejectsStaleAndForeignOwnership()
        {
            using (var original = new ClipboardTestScope())
            {
                var clipboard = new WindowsClipboard();
                Assert.That(clipboard.OwnedSequenceNumber, Is.EqualTo(-1));
                var first = clipboard.WriteText("Synthetic Unicode: Привет", clipboard.SequenceNumber);
                Assert.That(first, Is.EqualTo(clipboard.SequenceNumber));
                Assert.That(clipboard.ReadText(), Is.EqualTo("Synthetic Unicode: Привет"));
                using (var saved = clipboard.Save())
                {
                    var second = clipboard.WriteText("Synthetic newer copy", saved.SequenceNumber);
                    Assert.That(saved.Restore(first), Is.False);
                    Assert.That(clipboard.ReadText(), Is.EqualTo("Synthetic newer copy"));
                    Assert.Throws<InvalidOperationException>(() => clipboard.WriteText("Must not write", first));
                    Assert.That(saved.Restore(second), Is.True);
                    Assert.That(clipboard.ReadText(), Is.EqualTo("Synthetic Unicode: Привет"));
                    saved.Close();
                    Assert.Throws<ObjectDisposedException>(() => saved.Restore(clipboard.SequenceNumber));
                }
                var owned = clipboard.WriteText("Owned marker");
                Assert.That(clipboard.OwnedSequenceNumber, Is.EqualTo(owned));
                ClipboardDispatcher.Shared.Run(() => { Clipboard.SetText("Synthetic other clipboard owner"); return true; });
                Assert.That(clipboard.OwnedSequenceNumber, Is.EqualTo(-1));
                Assert.That(clipboard.ReadText(), Is.EqualTo("Synthetic other clipboard owner"));
                clipboard.WriteText("", clipboard.SequenceNumber);
                Assert.That(clipboard.ReadText(), Is.Empty);
            }
        }

        [Test]
        public void WindowIdentityIsReadOnlyAndRetainsFullHandleAndProcessId()
        {
            var before = NativeMethods.GetForegroundWindow();
            var window = new WindowsDesktop().ForegroundWindow();
            Assert.That(window.Handle, Is.EqualTo((decimal)before.ToInt64()));
            Assert.That(window.ProcessId, Is.GreaterThan(0));
            Assert.That(window.ClassName, Is.Not.Empty);
            Assert.That(window.Title, Is.Not.Null);
            Assert.That(window.IsForeground(), Is.True);
            Assert.That(NativeMethods.GetForegroundWindow(), Is.EqualTo(before));
        }
    }
}
