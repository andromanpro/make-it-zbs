using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class UiaDispatcherTests
    {
        [Test]
        public void ReadsUseTheSamePersistentMtaWithoutReturningAutomationObjects()
        {
            using (var worker = new UiaDispatcher())
            {
                var id = worker.Run(() => Thread.CurrentThread.ManagedThreadId);
                Assert.That(worker.Run(() => Thread.CurrentThread.GetApartmentState()), Is.EqualTo(ApartmentState.MTA));
                Assert.That(worker.Run(() => Thread.CurrentThread.ManagedThreadId), Is.EqualTo(id));
                Assert.That(worker.Run(() => worker.Run(() => 42)), Is.EqualTo(42));
                Assert.Throws<InvalidOperationException>(() => worker.Run<int>(() => throw new InvalidOperationException("controlled")));
                Assert.That(worker.Run(() => 7), Is.EqualTo(7));
            }
        }

        [Test]
        public void TimedOutProviderDoesNotQueueMoreReadsOrPublishItsLateResult()
        {
            using (var worker = new UiaDispatcher())
            using (var release = new ManualResetEventSlim())
            using (var finished = new ManualResetEventSlim())
            {
                try
                {
                    Assert.Throws<TimeoutException>(() => worker.Run(() =>
                    {
                        release.Wait(); finished.Set(); return "late";
                    }, timeoutMilliseconds: 25));
                    Assert.Throws<InvalidOperationException>(() => worker.Run(() => "must-not-queue"));
                }
                finally { release.Set(); Assert.That(finished.Wait(1000), Is.True); }
            }
        }

        [Test]
        public void CancellationReturnsBeforeTheProviderAndARejectedRequestNeverRuns()
        {
            using (var worker = new UiaDispatcher())
            using (var release = new ManualResetEventSlim())
            using (var started = new ManualResetEventSlim())
            using (var cancellation = new CancellationTokenSource())
            {
                var result = Task.Run(() => worker.Run(() => { started.Set(); release.Wait(); return 1; }, cancellation.Token));
                try
                {
                    Assert.That(started.Wait(1000), Is.True);
                    cancellation.Cancel();
                    Assert.Throws<OperationCanceledException>(() => result.GetAwaiter().GetResult());
                    var calls = 0;
                    Assert.Throws<OperationCanceledException>(() => worker.Run(() => ++calls, cancellation.Token));
                    Assert.That(calls, Is.Zero);
                }
                finally { release.Set(); }
            }
        }

        [Test]
        public void InvalidAndDisposedRequestsAreRejected()
        {
            using (var worker = new UiaDispatcher())
            {
                Assert.Throws<ArgumentNullException>(() => worker.Run<int>(null));
                Assert.Throws<ArgumentOutOfRangeException>(() => worker.Run(() => 1, timeoutMilliseconds: 0));
                worker.Dispose();
                Assert.Throws<ObjectDisposedException>(() => worker.Run(() => 1));
            }
        }
    }
}
