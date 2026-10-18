using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using NUnit.Framework;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    [TestFixture, NonParallelizable]
    public class PaintUiTests
    {
        [Test]
        public void BundledSketchCanBeCapturedInAnIndependentlyStartedPaint()
        {
            var output = Environment.GetEnvironmentVariable("ZBS_UI_ARTIFACT_DIR");
            var samples = Environment.GetEnvironmentVariable("ZBS_SMOKE_SAMPLES");
            Assert.That(output, Is.Not.Null.And.Not.Empty);
            var source = Path.Combine(samples, "paint-cabin-sketch.png");
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe");
            Assert.That(File.Exists(executable), Is.True, "Microsoft Paint is not installed");
            using (var owned = Process.Start(new ProcessStartInfo(executable, OwnedProcessRunner.QuoteArgument(source)) { UseShellExecute = false }))
            {
                try
                {
                    var timer = Stopwatch.StartNew();
                    IntPtr handle = IntPtr.Zero;
                    while (timer.ElapsedMilliseconds < 15000)
                    {
                        owned.Refresh();
                        handle = owned.MainWindowHandle;
                        if (handle != IntPtr.Zero) break;
                        Thread.Sleep(100);
                    }
                    Assert.That(handle, Is.Not.EqualTo(IntPtr.Zero), "Paint did not show its own window");
                    NativeMethods.GetWindowThreadProcessId(handle, out var pid);
                    Assert.That(pid, Is.EqualTo(owned.Id));
                    Assert.That(new WindowInfo().ActivateWindow(handle.ToInt32()), Is.True);
                    Thread.Sleep(1500);
                    Assert.That(NativeMethods.GetForegroundWindow(), Is.EqualTo(handle));
                    var path = Path.Combine(output, "paint-visible-window.png");
                    var capture = new ScreenCapture();
                    capture.CaptureWindow(path, handle.ToInt32(), owned.Id);
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), capture.GetLastCaptureMetadata());
                    using (var image = new Bitmap(path))
                    {
                        Assert.That(image.Width, Is.GreaterThan(200));
                        Assert.That(image.Height, Is.GreaterThan(200));
                    }
                    TestContext.AddTestAttachment(path);
                }
                finally
                {
                    // No editing commands are sent; only this created process is closed.
                    if (!owned.HasExited && (!owned.CloseMainWindow() || !owned.WaitForExit(5000)))
                    { owned.Kill(); owned.WaitForExit(5000); }
                }
            }
        }
    }
}
