using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class ClipboardDispatcherTests
    {
        [Test]
        public void OleCallsKeepTheirStaOwnerAndSupportNestedCalls()
        {
            using (var dispatcher = new ClipboardDispatcher())
            {
                var id = dispatcher.Run(() => Thread.CurrentThread.ManagedThreadId);
                Assert.That(dispatcher.Run(() => Thread.CurrentThread.GetApartmentState()), Is.EqualTo(ApartmentState.STA));
                Assert.That(dispatcher.Run(() => Thread.CurrentThread.ManagedThreadId), Is.EqualTo(id));
                Assert.That(dispatcher.Run(() => dispatcher.Run(() => 42)), Is.EqualTo(42));
                Assert.Throws<InvalidOperationException>(() => dispatcher.Run<int>(() => throw new InvalidOperationException("controlled")));
                Assert.That(dispatcher.Run(() => 7), Is.EqualTo(7));
            }
        }

        [Test]
        public void InvalidAndDisposedCallsAreRejectedWithoutPostingMessages()
        {
            using (var dispatcher = new ClipboardDispatcher())
            {
                Assert.Throws<ArgumentNullException>(() => dispatcher.Run<int>(null));
                dispatcher.Dispose();
                Assert.Throws<ObjectDisposedException>(() => dispatcher.Run(() => 1));
            }
        }

        [Test]
        public void ClipboardWaitPumpsMessagesWithoutInterleavingAnotherCaller()
        {
            using (var dispatcher = new ClipboardDispatcher())
            using (var started = new ManualResetEventSlim())
            {
                var active = false;
                var timerFired = false;
                var first = Task.Run(() => dispatcher.Run(() =>
                {
                    active = true;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 10 })
                    {
                        timer.Tick += (sender, args) => timerFired = true;
                        timer.Start();
                        started.Set();
                        ClipboardDispatcher.PumpWait(150);
                    }
                    active = false;
                    return timerFired;
                }));
                Assert.That(started.Wait(5000), Is.True);
                var second = Task.Run(() => dispatcher.Run(() => active));
                Assert.That(first.GetAwaiter().GetResult(), Is.True, "STA did not dispatch timer messages");
                Assert.That(second.GetAwaiter().GetResult(), Is.False, "Pumping must not admit another clipboard operation");
            }
        }
    }
}
