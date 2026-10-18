using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("ZBSWindowsComponent.Tests")]
[assembly: InternalsVisibleTo("MakeItZBS.UiTests")]
[assembly: InternalsVisibleTo("ZBSWindowsComponent")]

namespace MakeItZBS.Capture
{
    public enum CaptureBackend { Auto, Dxgi, Gdi }

    public sealed class CaptureRequest
    {
        public int Version { get; set; } = 1;
        public Rectangle Bounds { get; set; }
        public CaptureBackend Backend { get; set; }
        public string OutputPath { get; set; }
        public long WindowHandle { get; set; }
        public uint ProcessId { get; set; }
        public int FrameTimeoutMilliseconds { get; set; } = 1000;
    }

    public sealed class CaptureMetadata
    {
        public int Version { get; set; } = 1;
        public string Backend { get; set; }
        public string FallbackReason { get; set; }
        public Rectangle Bounds { get; set; }
        public long WindowHandle { get; set; }
        public uint ProcessId { get; set; }
        public uint Dpi { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class CaptureUnavailableException : Exception
    {
        public CaptureUnavailableException(string reason) : base(reason) { }
    }

    internal interface ICaptureBackend
    {
        Bitmap Capture(Rectangle bounds, int timeoutMilliseconds, CancellationToken cancellation);
    }

    internal sealed class CaptureEngine
    {
        private readonly ICaptureBackend _dxgi;
        private readonly ICaptureBackend _gdi;

        internal CaptureEngine(ICaptureBackend dxgi, ICaptureBackend gdi)
        {
            _dxgi = dxgi;
            _gdi = gdi;
        }

        internal Bitmap Capture(CaptureRequest request, CancellationToken cancellation,
            out string backend, out string fallbackReason)
        {
            Validate(request);
            cancellation.ThrowIfCancellationRequested();
            fallbackReason = null;
            backend = "dxgi";
            if (request.Backend != CaptureBackend.Gdi)
            {
                try { return _dxgi.Capture(request.Bounds, request.FrameTimeoutMilliseconds, cancellation); }
                catch (CaptureUnavailableException ex) when (request.Backend == CaptureBackend.Auto)
                {
                    fallbackReason = ex.Message;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            backend = "gdi";
            return _gdi.Capture(request.Bounds, request.FrameTimeoutMilliseconds, cancellation);
        }

        internal static void Validate(CaptureRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var bounds = request.Bounds;
            if (request.Version != 1 || !Enum.IsDefined(typeof(CaptureBackend), request.Backend)
                || bounds.Width <= 0 || bounds.Height <= 0
                || (long)bounds.Width * bounds.Height > 32000000
                || (long)bounds.X + bounds.Width > int.MaxValue
                || (long)bounds.Y + bounds.Height > int.MaxValue
                || request.FrameTimeoutMilliseconds < 1 || request.FrameTimeoutMilliseconds > 3000)
                throw new ArgumentException("Invalid capture request.");
        }
    }

    internal sealed class GdiCaptureBackend : ICaptureBackend
    {
        public Bitmap Capture(Rectangle bounds, int timeoutMilliseconds, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                        CopyPixelOperation.SourceCopy);
                cancellation.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
    }
}
