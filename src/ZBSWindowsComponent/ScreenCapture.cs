using System;
using System.Drawing;
using System.Threading;
using MakeItZBS.Capture;
using Newtonsoft.Json;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    [ContextClass("СнимокЭкрана", "ScreenCapture")]
    public class ScreenCapture : AutoContext<ScreenCapture>
    {
        internal CaptureMetadata LastCapture { get; private set; }

        [ScriptConstructor]
        public static ScreenCapture Constructor() => new ScreenCapture();

        [ContextMethod("СделатьСнимок", "CaptureWindow")]
        public string CaptureWindow(string outputPath = null, int targetHandle = 0, int targetProcessId = 0,
            string targetTitle = null)
        {
            LastCapture = null;
            using (new PhysicalPixelScope())
            {
                var handle = targetHandle == 0 ? NativeMethods.GetForegroundWindow() : new IntPtr(targetHandle);
                if (handle == IntPtr.Zero) throw new RuntimeException("Не найдено активное окно");
                var source = new CaptureSource(handle, targetProcessId, targetTitle);
                var original = ReadWindowBounds(handle);
                var bounds = Rectangle.Intersect(original, System.Windows.Forms.SystemInformation.VirtualScreen);
                if (bounds.IsEmpty) throw new RuntimeException("Исходное окно находится вне экрана");
                return Capture(new CaptureRequest
                {
                    Bounds = bounds, WindowHandle = handle.ToInt64(), ProcessId = source.ProcessId
                }, outputPath, () =>
                {
                    source.Validate();
                    if (ReadWindowBounds(handle) != original)
                        throw new RuntimeException("Исходное окно переместилось во время захвата. Повторите команду.");
                });
            }
        }

        [ContextMethod("СделатьСнимокОбласти", "CaptureRegion")]
        public string CaptureRegion(int x, int y, int width, int height, string outputPath = null)
        {
            LastCapture = null;
            if (width <= 0 || height <= 0) throw new RuntimeException("Некорректные размеры области");
            using (new PhysicalPixelScope())
                return Capture(new CaptureRequest { Bounds = new Rectangle(x, y, width, height) }, outputPath, null);
        }

        [ContextMethod("СделатьСнимокВсегоЭкрана", "CaptureFullScreen")]
        public string CaptureFullScreen(string outputPath = null)
        {
            using (new PhysicalPixelScope())
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                return CaptureRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height, outputPath);
            }
        }

        [ContextMethod("ПолучитьСведенияОСнимке", "GetLastCaptureMetadata")]
        public string GetLastCaptureMetadata() => LastCapture == null ? string.Empty : JsonConvert.SerializeObject(LastCapture);

        private string Capture(CaptureRequest request, string outputPath, Action validate)
        {
            outputPath = string.IsNullOrEmpty(outputPath) ? TemporaryFileStore.CreatePath("screenshot", "png") : outputPath;
            LastCapture = DesktopCaptureClient.Capture(request, outputPath,
                Operations.Current?.Token ?? CancellationToken.None, validate);
            return outputPath;
        }

        private static Rectangle ReadWindowBounds(IntPtr handle)
        {
            if (!NativeMethods.GetWindowRect(handle, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                throw new RuntimeException("Не удалось получить размеры исходного окна");
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        internal sealed class PhysicalPixelScope : IDisposable
        {
            private readonly IntPtr _previous = NativeMethods.SetThreadDpiAwarenessContext(
                NativeMethods.DpiAwarenessContextPerMonitorAwareV2);

            public void Dispose()
            {
                if (_previous != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(_previous);
            }
        }
    }
}
