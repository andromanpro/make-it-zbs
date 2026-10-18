using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture, NonParallelizable]
    public class OperationControlTests
    {
        [TearDown]
        public void TearDown() => Operations.End();

        [Test]
        public void RapidPressesNeverCreateASecondPendingOrRunningRequest()
        {
            var queue = new HotkeyAdmissionQueue();
            Parallel.For(0, 1000, i => queue.Enqueue("Ctrl+F12", false));
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.TryDequeue(out var key), Is.True);
            Assert.That(key, Is.EqualTo("Ctrl+F12"));
            Assert.That(queue.Enqueue("Ctrl+F12", false), Is.False, "Reserved before operation start.");
            Assert.That(queue.TryDequeue(out _), Is.False);
            queue.Clear();
            Assert.That(queue.Enqueue("Ctrl+F12", true), Is.False, "Clearing stale events must not bypass busy state.");
            Assert.That(queue.Enqueue("", false), Is.False);
            Assert.That(queue.Enqueue("Ctrl+F12", false), Is.True);
        }

        [Test]
        public void CancellationIsScopedAndDoesNotPoisonTheNextTask()
        {
            var control = OperationControl.Constructor();
            Assert.That(control.Begin(), Is.True);
            Assert.That(Operations.IsBusy, Is.True);
            Assert.That(Operations.Begin(), Is.False);
            var operation = Operations.Current;
            Task.Run(Operations.Cancel).GetAwaiter().GetResult();
            Assert.That(control.IsCancelled(), Is.True);
            Assert.That(Operations.IsCancelling, Is.True);
            Assert.Throws<OperationCanceledException>(() => control.ThrowIfCancelled());
            control.End();
            Assert.That(Operations.IsBusy, Is.False);
            Assert.That(control.Begin(), Is.True);
            Assert.That(Operations.Current, Is.Not.SameAs(operation));
            Operations.Cancel(operation);
            Assert.That(control.IsCancelled(), Is.False);
            Assert.DoesNotThrow(() => control.ThrowIfCancelled());
        }

        [Test]
        public void DelayedPressCannotSwitchToAnotherWindowBeforeCapture()
        {
            var source = "window A";
            var queue = new HotkeyAdmissionQueue(() => source);
            queue.Enqueue("Ctrl+F12", false);
            source = "window B";
            Assert.That(queue.TryDequeue(out _), Is.False);
            Assert.That(queue.Count, Is.Zero);
            Assert.That(queue.Enqueue("Ctrl+F12", false), Is.True);
            Assert.That(queue.TryDequeue(out _), Is.True);
        }

        [Test]
        public void OperationOwnsOnlyItsTemporaryFilesAndCleanupContinuesAfterFailure()
        {
            var unrelated = TemporaryFileStore.CreatePath("fixture", "txt");
            File.WriteAllText(unrelated, "unrelated");
            try
            {
                Operations.Begin();
                var captured = TemporaryFileStore.CreatePath("fixture", "txt");
                File.WriteAllText(captured, "captured input");
                Operations.Current.OwnCleanup(() => throw new IOException("synthetic cleanup failure"));
                var last = TemporaryFileStore.CreatePath("fixture", "txt");
                File.WriteAllText(last, "last file");
                Operations.Cancel();
                Operations.End();
                Assert.That(File.Exists(captured), Is.False);
                Assert.That(File.Exists(last), Is.False);
                Assert.That(File.ReadAllText(unrelated), Is.EqualTo("unrelated"));
            }
            finally { File.Delete(unrelated); }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void LatePreviewCannotApplyAfterOperationWasCancelled()
        {
            Operations.Begin();
            using (var preview = new PreviewForm("before", "after", "Code", "fixture", 0, true))
            {
                preview.BindOperation(Operations.Current);
                Operations.Cancel();
                Assert.That(preview.TryApply(true), Is.False);
                Assert.That(preview.Outcome, Is.EqualTo("cancelled"));
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void CancelAlsoDisablesSavingOrCopyingAnImageResult()
        {
            Operations.Begin();
            var path = TemporaryFileStore.CreatePath("fixture", "png");
            using (var bitmap = new Bitmap(2, 2)) bitmap.Save(path, ImageFormat.Png);
            using (var preview = new ImagePreviewForm(path, "Image", "fixture", 0))
            {
                preview.BindOperation(Operations.Current);
                Assert.That(preview.CanUseResult, Is.True);
                Operations.Cancel();
                Assert.That(preview.CanUseResult, Is.False);
            }
        }

        [Test]
        public void CancellationCallbacksMayReadOperationStateFromAnotherThread()
        {
            Operations.Begin();
            var responsive = false;
            using (Operations.Current.Token.Register(() =>
                responsive = Task.Run(() => Operations.IsBusy).Wait(1000)))
                Operations.Cancel();
            Assert.That(responsive, Is.True);
        }

        [Test]
        public void BrokenCancellationCallbackDoesNotPreventOtherSubscribersOrCleanup()
        {
            Operations.Begin();
            var called = false;
            using (Operations.Current.Token.Register(() => { called = true; }))
            using (Operations.Current.Token.Register(() => throw new InvalidOperationException("fixture")))
                Assert.DoesNotThrow(Operations.Cancel);
            Assert.That(called, Is.True);
            Assert.That(Operations.IsCancelling, Is.True);
        }

        [Test]
        public void CleanupKeepsAdmissionClosedWithoutBlockingStatusReaders()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var worker = Task.Run(() =>
                {
                    Operations.Begin();
                    Operations.Current.OwnCleanup(() => { entered.Set(); release.Wait(3000); });
                    Operations.End();
                });
                try
                {
                    Assert.That(entered.Wait(1000), Is.True);
                    var status = Task.Run(() => Operations.IsBusy);
                    Assert.That(status.Wait(1000), Is.True);
                    Assert.That(status.Result, Is.True);
                    Assert.That(Operations.Begin(), Is.False);
                }
                finally { release.Set(); worker.GetAwaiter().GetResult(); }
            }
            Assert.That(Operations.IsBusy, Is.False);
        }
    }
}
