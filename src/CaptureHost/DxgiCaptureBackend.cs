using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace MakeItZBS.Capture
{
    [SuppressMessage("Interop", "S6640", Justification = "Silk.NET's typed COM API requires native pointers. "
        + "Pointers stay inside this isolated worker; COM lifetimes use ComPtr, Map/Unmap use finally, "
        + "and checked row lengths/pitches are validated before copying into owned bitmap memory.")]
    internal sealed unsafe class DxgiCaptureBackend : ICaptureBackend
    {
        private const uint MaxAdapters = 64;
        private const uint MaxOutputs = 64;
        private const int NotFound = unchecked((int)0x887A0002);
        private const int WaitTimeout = unchecked((int)0x887A0027);

        public Bitmap Capture(Rectangle bounds, int timeoutMilliseconds, CancellationToken cancellation)
        {
            using (var dxgi = DXGI.GetApi(null))
            using (var d3d = D3D11.GetApi(null))
            {
                var factoryId = typeof(IDXGIFactory1).GUID;
                IDXGIFactory1* factoryPointer = null;
                Check(dxgi.CreateDXGIFactory1(&factoryId, (void**)&factoryPointer));
                using (var factory = new ComPtr<IDXGIFactory1>(factoryPointer))
                {
                    for (uint adapterIndex = 0; adapterIndex < MaxAdapters; adapterIndex++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        IDXGIAdapter1* adapterPointer = null;
                        var status = factory.Handle->EnumAdapters1(adapterIndex, &adapterPointer);
                        if (status == NotFound) break;
                        Check(status);
                        using (var adapter = new ComPtr<IDXGIAdapter1>(adapterPointer))
                        {
                            for (uint outputIndex = 0; outputIndex < MaxOutputs; outputIndex++)
                            {
                                IDXGIOutput* outputPointer = null;
                                status = adapter.Handle->EnumOutputs(outputIndex, &outputPointer);
                                if (status == NotFound) break;
                                Check(status);
                                using (var output = new ComPtr<IDXGIOutput>(outputPointer))
                                {
                                    OutputDesc description;
                                    Check(output.Handle->GetDesc(&description));
                                    var area = description.DesktopCoordinates;
                                    var monitor = Rectangle.FromLTRB(area.Min.X, area.Min.Y, area.Max.X, area.Max.Y);
                                    if (!description.AttachedToDesktop || !monitor.Contains(bounds)) continue;
                                    if (description.Rotation != ModeRotation.Identity)
                                        throw new CaptureUnavailableException("dxgi-rotated-output");
                                    return CaptureOutput(d3d, adapter.Handle, output.Handle, monitor,
                                        bounds, timeoutMilliseconds, cancellation);
                                }
                            }
                        }
                    }
                }
            }
            throw new CaptureUnavailableException("dxgi-region-spans-outputs-or-output-missing");
        }

        private static Bitmap CaptureOutput(D3D11 api, IDXGIAdapter1* adapter, IDXGIOutput* output,
            Rectangle monitor, Rectangle bounds, int timeoutMilliseconds, CancellationToken cancellation)
        {
            ID3D11Device* devicePointer = null;
            ID3D11DeviceContext* contextPointer = null;
            var result = api.CreateDevice((IDXGIAdapter*)adapter, D3DDriverType.Unknown, IntPtr.Zero,
                (uint)CreateDeviceFlag.BgraSupport, null, 0, D3D11.SdkVersion,
                &devicePointer, null, &contextPointer);
            // Own even partially returned COM objects before checking HRESULT.
            using (var device = new ComPtr<ID3D11Device>(devicePointer))
            using (var context = new ComPtr<ID3D11DeviceContext>(contextPointer))
            {
                Check(result);
                var outputId = typeof(IDXGIOutput1).GUID;
                IDXGIOutput1* output1Pointer = null;
                Check(output->QueryInterface(&outputId, (void**)&output1Pointer));
                using (var output1 = new ComPtr<IDXGIOutput1>(output1Pointer))
                {
                    IDXGIOutputDuplication* duplicationPointer = null;
                    Check(output1.Handle->DuplicateOutput((IUnknown*)device.Handle, &duplicationPointer));
                    using (var duplication = new ComPtr<IDXGIOutputDuplication>(duplicationPointer))
                        return Acquire(device.Handle, context.Handle, duplication.Handle, monitor,
                            bounds, timeoutMilliseconds, cancellation);
                }
            }
        }

        private static Bitmap Acquire(ID3D11Device* device, ID3D11DeviceContext* context,
            IDXGIOutputDuplication* duplication, Rectangle monitor, Rectangle bounds,
            int timeoutMilliseconds, CancellationToken cancellation)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeoutMilliseconds)
            {
                cancellation.ThrowIfCancellationRequested();
                OutduplFrameInfo info;
                IDXGIResource* resourcePointer = null;
                var status = duplication->AcquireNextFrame(50, &info, &resourcePointer);
                if (status == WaitTimeout) continue;
                Check(status);
                try
                {
                    using (var resource = new ComPtr<IDXGIResource>(resourcePointer))
                    {
                        if (info.ProtectedContentMaskedOut)
                            throw new InvalidOperationException("Protected desktop content cannot be captured.");
                        if (info.LastPresentTime == 0) continue;
                        var textureId = typeof(ID3D11Texture2D).GUID;
                        ID3D11Texture2D* texturePointer = null;
                        Check(resource.Handle->QueryInterface(&textureId, (void**)&texturePointer));
                        using (var texture = new ComPtr<ID3D11Texture2D>(texturePointer))
                            return ReadPixels(device, context, texture.Handle, monitor, bounds, cancellation);
                    }
                }
                // Disposal ends this one-shot session even if releasing the frame
                // fails. Never replace cancellation/access-denial with a fallback.
                finally { duplication->ReleaseFrame(); }
            }
            throw new CaptureUnavailableException("dxgi-frame-timeout");
        }

        private static Bitmap ReadPixels(ID3D11Device* device, ID3D11DeviceContext* context,
            ID3D11Texture2D* source, Rectangle monitor, Rectangle bounds, CancellationToken cancellation)
        {
            Texture2DDesc sourceDescription;
            source->GetDesc(&sourceDescription);
            if (sourceDescription.Format != Format.FormatB8G8R8A8Unorm)
                throw new CaptureUnavailableException("dxgi-unsupported-pixel-format");
            var sourceArea = new Rectangle(bounds.Left - monitor.Left, bounds.Top - monitor.Top, bounds.Width, bounds.Height);
            if (!FitsTexture(sourceArea, sourceDescription.Width, sourceDescription.Height))
                throw new CaptureUnavailableException("dxgi-output-resized");
            var description = new Texture2DDesc
            {
                Width = (uint)bounds.Width, Height = (uint)bounds.Height, MipLevels = 1, ArraySize = 1,
                Format = sourceDescription.Format, SampleDesc = new SampleDesc(1, 0),
                Usage = Usage.Staging, CPUAccessFlags = (uint)CpuAccessFlag.Read
            };
            ID3D11Texture2D* stagingPointer = null;
            Check(device->CreateTexture2D(&description, null, &stagingPointer));
            using (var staging = new ComPtr<ID3D11Texture2D>(stagingPointer))
            {
                var left = (uint)(bounds.Left - monitor.Left);
                var top = (uint)(bounds.Top - monitor.Top);
                var box = new Box(left, top, 0, left + (uint)bounds.Width, top + (uint)bounds.Height, 1);
                context->CopySubresourceRegion((ID3D11Resource*)staging.Handle, 0, 0, 0, 0,
                    (ID3D11Resource*)source, 0, &box);
                MappedSubresource mapped;
                Check(context->Map((ID3D11Resource*)staging.Handle, 0, Map.Read, 0, &mapped));
                try { return CopyRows((IntPtr)mapped.PData, checked((int)mapped.RowPitch), bounds.Size, cancellation); }
                finally { context->Unmap((ID3D11Resource*)staging.Handle, 0); }
            }
        }

        internal static Bitmap CopyRows(IntPtr data, int rowPitch, Size size, CancellationToken cancellation)
        {
            int rowLength = checked(size.Width * 4);
            if (data == IntPtr.Zero || rowPitch < rowLength || size.Width <= 0 || size.Height <= 0)
                throw new ArgumentException("Invalid GPU buffer.");
            // Format32bppRgb deliberately ignores DXGI's undefined desktop alpha.
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
            try
            {
                var pixels = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    var row = new byte[rowLength];
                    for (int y = 0; y < size.Height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Marshal.Copy(IntPtr.Add(data, checked(y * rowPitch)), row, 0, rowLength);
                        Marshal.Copy(row, 0, IntPtr.Add(pixels.Scan0, y * pixels.Stride), rowLength);
                    }
                }
                finally { bitmap.UnlockBits(pixels); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        internal static bool FitsTexture(Rectangle area, uint width, uint height) =>
            area.X >= 0 && area.Y >= 0 && area.Width > 0 && area.Height > 0
            && (long)area.X + area.Width <= width && (long)area.Y + area.Height <= height;

        private static void Check(int result)
        {
            if (result >= 0) return;
            if (result == unchecked((int)0x80070005))
                throw new UnauthorizedAccessException("Desktop capture is not permitted.");
            throw new CaptureUnavailableException("dxgi-hresult-" + unchecked((uint)result).ToString("X8"));
        }
    }
}
