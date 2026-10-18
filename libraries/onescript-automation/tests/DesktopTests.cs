using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OneScript.Automation;
using ZBSWindowsComponent;

namespace OneScript.Automation.Tests
{
    [TestFixture, Category("Desktop"), Apartment(ApartmentState.STA), NonParallelizable]
    public sealed class DesktopTests
    {
        private static Form ShowFixture()
        {
            var window = new Form
            {
                Text = "OneScript.Automation isolated fixture", BackColor = Color.Magenta,
                StartPosition = FormStartPosition.Manual, Location = new Point(120, 120),
                ClientSize = new Size(400, 260)
            };
            window.Show();
            window.Activate();
            Application.DoEvents();
            Assert.That(NativeMethods.GetForegroundWindow(), Is.EqualTo(window.Handle));
            return window;
        }

        [TestCase("dxgi"), TestCase("gdi"), TestCase("auto")]
        public void OwnedWorkerCapturesTheExpectedPixelsAndReportsItsBackend(string backend)
        {
            var output = Path.Combine(Path.GetTempPath(), "automation-desktop-" + Guid.NewGuid().ToString("N") + ".png");
            var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
            try
            {
                using (var window = ShowFixture())
                using (var job = new AutomationClients().CaptureWindow(output, backend))
                {
                    Assert.That(SpinWait.SpinUntil(() => job.IsCompleted, 15000), Is.True);
                    var metadata = JObject.Parse(job.ResultJson());
                    Assert.That(metadata.Value<long>("WindowHandle"), Is.EqualTo(window.Handle.ToInt64()));
                    Assert.That(metadata.Value<string>("Backend"), backend == "auto"
                        ? Is.AnyOf("gdi", "dxgi") : Is.EqualTo(backend));
                    using (var image = new Bitmap(output))
                    {
                        var center = image.GetPixel(image.Width / 2, image.Height / 2);
                        Assert.That(center.R, Is.GreaterThan(240));
                        Assert.That(center.B, Is.GreaterThan(240));
                        Assert.That(center.G, Is.LessThan(20));
                    }
                }
            }
            finally
            {
                File.Delete(output);
                if (previous != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previous);
            }
        }

        [Test]
        public void ActualGlobalHotkeyIsDeliveredOnceAndSuppressedWhileBusy()
        {
            using (var window = ShowFixture())
            using (var keys = new AutomationHotkeys())
            {
                keys.Listen("Ctrl+Shift+F23");
                SendCombination();
                string pressed = "";
                Assert.That(SpinWait.SpinUntil(() => (pressed = keys.TakePressed()).Length > 0, 3000), Is.True);
                Assert.That(pressed, Is.EqualTo("Ctrl+Shift+F23"));
                Assert.That(keys.TakePressed(), Is.Empty);
                keys.SetBusy(true);
                SendCombination();
                Thread.Sleep(100);
                Assert.That(keys.TakePressed(), Is.Empty);
                Assert.That(NativeMethods.GetForegroundWindow(), Is.EqualTo(window.Handle));
            }
        }

        private static void SendCombination()
        {
            ushort[] keys = { 0x11, 0x10, 0x86 };
            var input = keys.Select(key => Key(key, false)).Concat(Enumerable.Reverse(keys).Select(key => Key(key, true))).ToArray();
            try
            {
                Assert.That(NativeMethods.SendInput((uint)input.Length, input, Marshal.SizeOf<NativeMethods.Input>()), Is.EqualTo(input.Length));
            }
            finally
            {
                var release = Enumerable.Reverse(keys).Select(key => Key(key, true)).ToArray();
                NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf<NativeMethods.Input>());
            }
        }

        private static NativeMethods.Input Key(ushort key, bool up) => new NativeMethods.Input
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KeyboardInput { wVk = key, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 }
            }
        };
    }
}
