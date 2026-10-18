using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using OneScript.Automation;

namespace OneScript.Automation.Tests
{
    [TestFixture, NonParallelizable]
    public sealed class HotkeyTests
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        private sealed class Registration : IDisposable
        {
            internal readonly AutomationHotkeys Keys;
            internal readonly List<int> Removed = new List<int>();
            internal IntPtr Window;
            internal int Id;
            internal int Count;
            internal bool Reject;
            internal ApartmentState Apartment;
            internal Registration()
            {
                Keys = new AutomationHotkeys((window, id, modifiers, key) =>
                {
                    Apartment = Thread.CurrentThread.GetApartmentState();
                    Window = window;
                    Count++;
                    if (Reject) return false;
                    Id = id;
                    return true;
                }, (window, id) => { Removed.Add(id); return true; });
            }
            internal void Press(int? id = null) => SendMessage(Window, 0x0312, new IntPtr(id ?? Id), IntPtr.Zero);
            public void Dispose() => Keys.Dispose();
        }

        [Test]
        public void RegistrationUsesAReadyStaWindowWithoutInstallingAnActualGlobalKey()
        {
            using (var registration = new Registration())
            {
                registration.Keys.Listen("Ctrl+Shift+F23");
                Assert.That(registration.Window, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(registration.Apartment, Is.EqualTo(ApartmentState.STA));
                registration.Keys.Listen("ctrl+shift+f23");
                Assert.That(registration.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void PendingPressesAreCoalescedAndBusyTransitionsDiscardThem()
        {
            using (var registration = new Registration())
            {
                registration.Keys.Listen("Ctrl+Shift+F23");
                registration.Press();
                registration.Press();
                Assert.That(registration.Keys.TakePressed(), Is.EqualTo("Ctrl+Shift+F23"));
                Assert.That(registration.Keys.TakePressed(), Is.Empty);
                registration.Press();
                registration.Keys.SetBusy(true);
                registration.Press();
                Assert.That(registration.Keys.TakePressed(), Is.Empty);
                registration.Keys.SetBusy(false);
                Assert.That(registration.Keys.TakePressed(), Is.Empty);
                registration.Press();
                Assert.That(registration.Keys.TakePressed(), Is.Not.Empty);
            }
        }

        [Test]
        public void FailedReplacementKeepsTheOldRegistrationAndSuccessfulReplacementDropsStaleEvents()
        {
            using (var registration = new Registration())
            {
                registration.Keys.Listen("Ctrl+Shift+F23");
                var previous = registration.Id;
                registration.Reject = true;
                Assert.That(() => registration.Keys.Listen("Ctrl+Shift+F24"), Throws.Exception);
                Assert.That(registration.Removed, Is.Empty);
                registration.Press(previous);
                Assert.That(registration.Keys.TakePressed(), Is.EqualTo("Ctrl+Shift+F23"));
                registration.Reject = false;
                registration.Keys.Listen("Ctrl+Shift+F24");
                Assert.That(registration.Removed, Is.EquivalentTo(new[] { previous }));
                registration.Press(previous);
                Assert.That(registration.Keys.TakePressed(), Is.Empty);
                registration.Press();
                Assert.That(registration.Keys.TakePressed(), Is.EqualTo("Ctrl+Shift+F24"));
            }
        }

        [Test]
        public void CloseUnregistersExactlyOnceAndRejectsFurtherUse()
        {
            var registration = new Registration();
            registration.Keys.Listen("Ctrl+Shift+F23");
            registration.Dispose();
            registration.Dispose();
            Assert.That(registration.Removed.Count, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => registration.Keys.Listen("Ctrl+Shift+F23"));
            Assert.Throws<ObjectDisposedException>(() => registration.Keys.TakePressed());
            Assert.Throws<ObjectDisposedException>(() => registration.Keys.SetBusy(true));
        }
    }
}
