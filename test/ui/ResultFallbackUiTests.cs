using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Linq;
using NUnit.Framework;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    [TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
    public class ResultFallbackUiTests
    {
        [Test]
        public void NativeResultIsVisibleAndCopyableWithoutWebView()
        {
            Assert.That(Environment.UserInteractive, Is.True);
            const string text = "Synthetic paid result\r\nLine 2: проверка результата\rMixed newlines\nTrailing spaces  ";
            using (var clipboard = new ClipboardTestScope())
            using (var form = new ResultFallbackForm(text, "Preview unavailable. The result is available to copy.", null))
            {
                form.Show();
                Application.DoEvents();
                Assert.That(form.Visible, Is.True);
                Assert.That(form.CopyResult(), Is.True);
                Assert.That(Clipboard.GetText(), Is.EqualTo(text));
                var output = Environment.GetEnvironmentVariable("ZBS_UI_ARTIFACT_DIR");
                Assert.That(output, Is.Not.Null.And.Not.Empty);
                form.Activate();
                Assert.That(SpinWait.SpinUntil(() =>
                {
                    Application.DoEvents();
                    NativeMethods.SetForegroundWindow(form.Handle);
                    return NativeMethods.GetForegroundWindow() == form.Handle;
                }, 3000), Is.True, "The owned result window must be foreground before capture.");
                new ScreenCapture().CaptureWindow(Path.Combine(output, "native-result-fallback.png"),
                    form.Handle.ToInt32(), Process.GetCurrentProcess().Id);
                form.Close();
            }
        }

        [Test]
        public void CancellationClosesNativeResultAndPreventsCopy()
        {
            using (var operation = new WorkOperation())
            using (var form = new ResultFallbackForm("synthetic cancelled result", "", operation))
            {
                form.Show();
                Application.DoEvents();
                operation.Cancel();
                Application.DoEvents();
                Assert.That(form.Visible, Is.False);
                Assert.That(form.CanCopy, Is.False);
                Assert.That(form.CopyResult(), Is.False);
            }
        }

        [Test]
        public void PollerFallbackUsesItsStaLoopAndExposesCopyWithoutApply()
        {
            using (var clipboard = new ClipboardTestScope())
            using (var poller = new HotkeyPoller(new HotkeyManager(() => null)))
            {
                const string text = "Synthetic result through the production poller";
                var shown = Task.Run(() => poller.ShowResultFallback(text, "No automatic Apply."));
                AutomationElement window = null;
                try
                {
                    Assert.That(SpinWait.SpinUntil(() =>
                    {
                        window = P0AppSession.Handles(Process.GetCurrentProcess().Id)
                            .Select(AutomationElement.FromHandle).FirstOrDefault(candidate =>
                                candidate.Current.Name == "Результат Make it ZBS");
                        return window != null;
                    }, 5000), Is.True, "The production STA did not show the native result.");
                    var copy = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "copyResult"));
                    Assert.That(copy, Is.Not.Null);
                    P0AppSession.Invoke(copy);
                    Assert.That(ClipboardDispatcher.Shared.Run(Clipboard.GetText), Is.EqualTo(text));
                    Assert.That(window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "Применить")), Is.Null);
                }
                finally
                {
                    if (window != null) P0AppSession.Invoke(window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "closeResult")));
                    Assert.That(shown.Wait(5000), Is.True, "The native result did not return to OneScript.");
                }
            }
        }
    }
}
