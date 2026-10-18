using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MakeItZBS.Capture;
using Newtonsoft.Json;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class DesktopCaptureTests
    {
        private static CaptureRequest Request() => new CaptureRequest { Bounds = new Rectangle(-100, 20, 4, 3) };

        [Test]
        public void AutomaticCapturePrefersDxgiWithoutRunningGdi()
        {
            var dxgi = new Backend(); var gdi = new Backend();
            using (new CaptureEngine(dxgi, gdi).Capture(Request(), CancellationToken.None, out var backend, out var fallback))
            {
                Assert.That(backend, Is.EqualTo("dxgi"));
                Assert.That(fallback, Is.Null);
                Assert.That(gdi.Calls, Is.Zero);
            }
        }

        [Test]
        public void FallbackKeepsTheExactRegionAndRecordsWhyDxgiFailed()
        {
            var request = Request();
            var gdi = new Backend();
            using (new CaptureEngine(new Backend { Failure = new CaptureUnavailableException("dxgi-frame-timeout") }, gdi)
                .Capture(request, CancellationToken.None, out var backend, out var fallback))
            {
                Assert.That(backend, Is.EqualTo("gdi"));
                Assert.That(fallback, Is.EqualTo("dxgi-frame-timeout"));
                Assert.That(gdi.Bounds, Is.EqualTo(request.Bounds));
            }
        }

        [TestCase(CaptureBackend.Dxgi)]
        [TestCase(CaptureBackend.Gdi)]
        public void ExplicitBackendNeverSilentlyUsesAnotherBackend(CaptureBackend selected)
        {
            var request = Request(); request.Backend = selected;
            var dxgi = new Backend { Failure = new CaptureUnavailableException("dxgi-unavailable") };
            var gdi = new Backend();
            var engine = new CaptureEngine(dxgi, gdi);
            if (selected == CaptureBackend.Dxgi)
            {
                Assert.Throws<CaptureUnavailableException>(() => engine.Capture(request, CancellationToken.None, out _, out _));
                Assert.That(gdi.Calls, Is.Zero);
            }
            else
            {
                using (engine.Capture(request, CancellationToken.None, out _, out _)) { }
                Assert.That(dxgi.Calls, Is.Zero);
            }
        }

        [TestCase(typeof(OperationCanceledException))]
        [TestCase(typeof(UnauthorizedAccessException))]
        [TestCase(typeof(InvalidOperationException))]
        public void CancellationAccessDenialAndProtectedContentDoNotTriggerFallback(Type failureType)
        {
            var gdi = new Backend();
            var engine = new CaptureEngine(new Backend { Failure = (Exception)Activator.CreateInstance(failureType) }, gdi);
            Assert.Throws(failureType, () => engine.Capture(Request(), CancellationToken.None, out _, out _));
            Assert.That(gdi.Calls, Is.Zero);
        }

        [Test]
        public void CancellationBetweenDxgiFailureAndFallbackDoesNotStartGdi()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var gdi = new Backend();
                var dxgi = new Backend { BeforeCapture = cancellation.Cancel, Failure = new CaptureUnavailableException("dxgi-timeout") };
                Assert.Throws<OperationCanceledException>(() => new CaptureEngine(dxgi, gdi)
                    .Capture(Request(), cancellation.Token, out _, out _));
                Assert.That(gdi.Calls, Is.Zero);
            }
        }

        [TestCase(0, 2)]
        [TestCase(2, 0)]
        [TestCase(-1, 2)]
        [TestCase(100000, 100000)]
        public void InvalidDimensionsFailBeforeAnyNativeCall(int width, int height)
        {
            var backend = new Backend();
            Assert.Throws<ArgumentException>(() => new CaptureEngine(backend, backend).Capture(
                new CaptureRequest { Bounds = new Rectangle(0, 0, width, height) }, CancellationToken.None, out _, out _));
            Assert.That(backend.Calls, Is.Zero);
        }

        [Test]
        public void GpuRowsRespectPitchAndIgnoreUndefinedAlpha()
        {
            var data = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 91, 92, 93, 94,
                    255, 0, 0, 0, 255, 255, 255, 0, 95, 96, 97, 98 }, 0, data, 24);
                using (var bitmap = DxgiCaptureBackend.CopyRows(data, 12, new Size(2, 2), CancellationToken.None))
                {
                    Assert.That(bitmap.GetPixel(0, 0).ToArgb(), Is.EqualTo(Color.Red.ToArgb()));
                    Assert.That(bitmap.GetPixel(1, 0).ToArgb(), Is.EqualTo(Color.Lime.ToArgb()));
                    Assert.That(bitmap.GetPixel(0, 1).ToArgb(), Is.EqualTo(Color.Blue.ToArgb()));
                    Assert.That(bitmap.GetPixel(1, 1).ToArgb(), Is.EqualTo(Color.White.ToArgb()));
                }
            }
            finally { Marshal.FreeHGlobal(data); }
        }

        [TestCase("timeout")]
        [TestCase("cancelled")]
        [TestCase("source-changed")]
        [TestCase("wrong-size")]
        [TestCase("wrong-source")]
        [TestCase("old-frame")]
        [TestCase("failure")]
        [TestCase("success")]
        public void ClientPublishesOnlyValidatedResultsAndAlwaysRemovesWorkerFiles(string scenario)
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-capture-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, "result.png");
            File.WriteAllText(destination, "original");
            string workerFile = null;
            var validations = 0;
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    CaptureMetadata Run() => DesktopCaptureClient.Capture(Request(), destination, cancellation.Token,
                        () => { if (++validations == 2 && scenario == "source-changed") throw new InvalidOperationException(); },
                        (process, token) =>
                        {
                            var request = JsonConvert.DeserializeObject<CaptureRequest>(process.Input);
                            workerFile = request.OutputPath;
                            using (var bitmap = new Bitmap(scenario == "wrong-size" ? 1 : 4, 3)) bitmap.Save(workerFile, ImageFormat.Png);
                            if (scenario == "cancelled") cancellation.Cancel();
                            return new ProcessResult
                            {
                                TimedOut = scenario == "timeout", ExitCode = scenario == "failure" ? 1 : 0,
                                Output = JsonConvert.SerializeObject(new CaptureMetadata
                                {
                                    Bounds = request.Bounds, WindowHandle = scenario == "wrong-source" ? 17 : 0,
                                    Backend = "dxgi", CapturedAtUtc = DateTime.UtcNow.AddMinutes(scenario == "old-frame" ? -5 : 0)
                                })
                            };
                        });
                    if (scenario == "success")
                    {
                        Assert.That(Run().Backend, Is.EqualTo("dxgi"));
                        using (var image = Image.FromFile(destination)) Assert.That(image.Size, Is.EqualTo(new Size(4, 3)));
                    }
                    else
                    {
                        Assert.That(() => Run(), Throws.Exception);
                        Assert.That(File.ReadAllText(destination), Is.EqualTo("original"));
                    }
                    Assert.That(workerFile, Is.Not.Null);
                    Assert.That(File.Exists(workerFile), Is.False);
                    Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
                }
                finally { Directory.Delete(directory, true); }
            }
        }

        [Test]
        public void TimedOutDxgiWorkerIsReplacedWithANewGdiWorkerForTheSameRegion()
        {
            var output = Path.Combine(Path.GetTempPath(), "zbs-recovery-" + Guid.NewGuid().ToString("N") + ".png");
            int calls = 0;
            try
            {
                var metadata = DesktopCaptureClient.Capture(Request(), output, CancellationToken.None, null, (process, token) =>
                {
                    var request = JsonConvert.DeserializeObject<CaptureRequest>(process.Input);
                    Assert.That(request.Bounds, Is.EqualTo(Request().Bounds));
                    if (++calls == 1)
                    {
                        Assert.That(request.Backend, Is.EqualTo(CaptureBackend.Auto));
                        return new ProcessResult { TimedOut = true };
                    }
                    Assert.That(request.Backend, Is.EqualTo(CaptureBackend.Gdi));
                    using (var bitmap = new Bitmap(4, 3)) bitmap.Save(request.OutputPath, ImageFormat.Png);
                    return new ProcessResult { Output = JsonConvert.SerializeObject(new CaptureMetadata
                    {
                        Bounds = request.Bounds, Backend = "gdi", CapturedAtUtc = DateTime.UtcNow
                    }) };
                });
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(metadata.FallbackReason, Is.EqualTo("dxgi-worker-timeout"));
            }
            finally { File.Delete(output); }
        }

        [Test]
        public void CancelledTimedOutWorkerCannotStartTheFallback()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                int calls = 0;
                Assert.Throws<OperationCanceledException>(() => DesktopCaptureClient.Capture(Request(), "unused.png", cancellation.Token,
                    null, (process, token) =>
                    {
                        calls++; cancellation.Cancel();
                        return new ProcessResult { TimedOut = true };
                    }));
                Assert.That(calls, Is.EqualTo(1));
            }
        }

        [TestCase("capture-source-focus-changed", "capture-source-focus-changed")]
        [TestCase("private document: C:/private/file.txt\nsecret", "capture-worker-error")]
        public void WorkerDiagnosticsUseOnlyAShortContentFreeClassification(string diagnostic, string expected)
        {
            var error = Assert.Throws<InvalidOperationException>(() => DesktopCaptureClient.Capture(
                Request(), "unused.png", CancellationToken.None, null,
                (process, token) => new ProcessResult { ExitCode = 1, Error = diagnostic }));
            Assert.That(error.Message, Is.EqualTo("Screen capture failed: " + expected));
        }

        [TestCase(0, 0, 640, 360, true)]
        [TestCase(20, 20, 600, 320, true)]
        [TestCase(20, 20, 640, 360, false)]
        [TestCase(-1, 0, 10, 10, false)]
        [TestCase(0, -1, 10, 10, false)]
        [TestCase(0, 0, 0, 10, false)]
        public void OutputModeChangesCannotCopyOutsideTheAcquiredTexture(int x, int y, int width, int height, bool expected)
        {
            Assert.That(DxgiCaptureBackend.FitsTexture(new Rectangle(x, y, width, height), 640, 360), Is.EqualTo(expected));
        }

        private sealed class Backend : ICaptureBackend
        {
            internal int Calls;
            internal Rectangle Bounds;
            internal Exception Failure;
            internal Action BeforeCapture;
            public Bitmap Capture(Rectangle bounds, int timeoutMilliseconds, CancellationToken cancellation)
            {
                Calls++; Bounds = bounds; BeforeCapture?.Invoke();
                if (Failure != null) throw Failure;
                return new Bitmap(bounds.Width, bounds.Height);
            }
        }
    }
}
