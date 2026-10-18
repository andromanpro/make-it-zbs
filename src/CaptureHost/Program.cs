using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace MakeItZBS.Capture
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

        [STAThread]
        private static int Main()
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                var input = Console.In.ReadLine();
                if (input == null || input.Length > 8192) throw new ArgumentException("Invalid input.");
                var request = JsonConvert.DeserializeObject<CaptureRequest>(input);
                CaptureEngine.Validate(request);
                if (!Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(request.Bounds))
                    || !SystemInformation.VirtualScreen.Contains(request.Bounds))
                    throw new ArgumentException("Capture region is outside the desktop.");
                ValidateWindow(request);
                var timer = Stopwatch.StartNew();
                var engine = new CaptureEngine(new DxgiCaptureBackend(), new GdiCaptureBackend());
                using (var bitmap = engine.Capture(request, CancellationToken.None, out var backend, out var fallback))
                {
                    ValidateWindow(request);
                    var metadata = new CaptureMetadata
                    {
                        Backend = backend, FallbackReason = fallback, Bounds = request.Bounds,
                        WindowHandle = request.WindowHandle, ProcessId = request.ProcessId,
                        Dpi = request.WindowHandle == 0 ? 0 : GetDpiForWindow(new IntPtr(request.WindowHandle)),
                        CapturedAtUtc = DateTime.UtcNow, ElapsedMilliseconds = timer.ElapsedMilliseconds
                    };
                    bitmap.Save(request.OutputPath, ImageFormat.Png);
                    Console.WriteLine(JsonConvert.SerializeObject(metadata));
                }
                return 0;
            }
            catch (Exception ex)
            {
                // Never include a window title, captured text, or caller path in diagnostics.
                Console.Error.WriteLine(ex is CaptureUnavailableException || ex is CaptureSourceException ? ex.Message
                    : ex.GetType().Name + ":" + ex.HResult.ToString("X8"));
                return 1;
            }
        }

        private static void ValidateWindow(CaptureRequest request)
        {
            if (request.WindowHandle == 0) return;
            var handle = new IntPtr(request.WindowHandle);
            GetWindowThreadProcessId(handle, out var pid);
            if (pid != request.ProcessId) throw new CaptureSourceException("capture-source-process-changed");
            if (GetForegroundWindow() != handle) throw new CaptureSourceException("capture-source-focus-changed");
            if (IsIconic(handle)) throw new CaptureSourceException("capture-source-minimized");
            if (!GetWindowRect(handle, out var rect)
                || !Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom).Contains(request.Bounds))
                throw new CaptureSourceException("capture-source-bounds-changed");
        }

        private sealed class CaptureSourceException : InvalidOperationException
        {
            internal CaptureSourceException(string reason) : base(reason) { }
        }
    }
}
