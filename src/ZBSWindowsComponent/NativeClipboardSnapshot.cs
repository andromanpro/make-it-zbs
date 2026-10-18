using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace ZBSWindowsComponent
{
    internal sealed class NativeClipboardSnapshot : IDisposable
    {
        private const int MaximumBytes = 64 * 1024 * 1024;
        private readonly List<Entry> _entries = new List<Entry>();
        private bool _disposed;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();

        internal static NativeClipboardSnapshot Capture(int[] formats, uint expectedSequence)
        {
            if (formats == null || formats.Length == 0 || formats.Length > 256 || formats.Any(format => format < 1 || format > ushort.MaxValue))
                throw new ArgumentException("Invalid native clipboard formats.", nameof(formats));
            var snapshot = new NativeClipboardSnapshot();
            Open(IntPtr.Zero);
            try
            {
                if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) throw new InvalidOperationException("Clipboard changed before capture.");
                var total = 0;
                foreach (var format in formats.Distinct())
                {
                    var entry = Entry.Read((uint)format);
                    snapshot._entries.Add(entry);
                    total = checked(total + entry.MemoryBytes);
                    if (total > MaximumBytes) throw new InvalidOperationException("Clipboard snapshot is too large.");
                }
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
            finally { CloseClipboard(); }
        }

        internal void Restore(uint expectedSequence)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeClipboardSnapshot));
            Open(ClipboardDispatcher.Shared.WindowHandle);
            try
            {
                if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) throw new InvalidOperationException("Clipboard changed before restore.");
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                foreach (var entry in _entries) entry.Write();
            }
            finally { CloseClipboard(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries) entry.Dispose();
            _entries.Clear();
        }

        private static void Open(IntPtr owner)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (OpenClipboard(owner)) return;
                ClipboardDispatcher.PumpWait(25);
            }
            NativeMethods.GetWindowThreadProcessId(GetOpenClipboardWindow(), out var processId);
            throw new InvalidOperationException("Clipboard is busy; lock owner PID=" + processId);
        }

        private sealed class Entry : IDisposable
        {
            [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
            [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
            [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
            [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
            [DllImport("gdi32.dll")] private static extern uint GetEnhMetaFileBits(IntPtr metafile, uint size, [Out] byte[] data);
            [DllImport("gdi32.dll")] private static extern IntPtr SetEnhMetaFileBits(uint size, byte[] data);
            [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr metafile);
            [DllImport("gdi32.dll")] private static extern uint GetMetaFileBitsEx(IntPtr metafile, uint size, [Out] byte[] data);
            [DllImport("gdi32.dll")] private static extern IntPtr SetMetaFileBitsEx(uint size, byte[] data);
            [DllImport("gdi32.dll")] private static extern bool DeleteMetaFile(IntPtr metafile);
            [DllImport("user32.dll")] private static extern IntPtr CopyImage(IntPtr image, uint type, int width, int height, uint flags);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr image);
            [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr image, int size, out NativeBitmap bitmap);
            [StructLayout(LayoutKind.Sequential)] private struct MetafilePicture { public int Mode, X, Y; public IntPtr Metafile; }
            [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, Stride; public ushort Planes, BitsPerPixel; public IntPtr Bits; }

            internal uint Format;
            internal byte[] Bytes;
            private IntPtr _bitmap;
            private MetafilePicture _picture;
            private int _bitmapBytes;
            internal int MemoryBytes => Bytes?.Length ?? _bitmapBytes;

            internal static Entry Read(uint format)
            {
                var source = GetClipboardData(format);
                if (source == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var entry = new Entry { Format = format };
                if (format == 14 || format == 0x8E) entry.Bytes = ReadMetafile(source, true);
                else if (format == 2 || format == 0x82) entry.ReadBitmap(source);
                else if (format == 3 || format == 0x83) entry.ReadPicture(source);
                else entry.Bytes = ReadMemory(source, format);
                return entry;
            }

            private void ReadBitmap(IntPtr source)
            {
                if (GetObject(source, Marshal.SizeOf<NativeBitmap>(), out var bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height == 0
                    || (ulong)bitmap.Width * (ulong)Math.Abs((long)bitmap.Height) > MaximumBytes / 4)
                    throw new InvalidOperationException("Clipboard bitmap is invalid or too large.");
                _bitmapBytes = checked(bitmap.Width * (int)Math.Abs((long)bitmap.Height) * 4);
                _bitmap = CopyImage(source, 0, 0, 0, 0x2000);
                if (_bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            private void ReadPicture(IntPtr source)
            {
                if (GlobalSize(source).ToUInt64() < (ulong)Marshal.SizeOf<MetafilePicture>())
                    throw new InvalidOperationException("Invalid clipboard metafile picture size.");
                var pointer = GlobalLock(source);
                if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { _picture = Marshal.PtrToStructure<MetafilePicture>(pointer); }
                finally { GlobalUnlock(source); }
                Bytes = ReadMetafile(_picture.Metafile, false);
            }

            private static byte[] ReadMemory(IntPtr source, uint format)
            {
                if (format == 9 || (format >= 0x200 && format <= 0x3FF))
                    throw new InvalidOperationException("Unsupported native clipboard handle format: " + format);
                var size = GlobalSize(source).ToUInt64();
                if (size == 0 || size > MaximumBytes) throw new InvalidOperationException("Invalid clipboard memory size.");
                var pointer = GlobalLock(source);
                if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally { GlobalUnlock(source); }
            }

            private static byte[] ReadMetafile(IntPtr metafile, bool enhanced)
            {
                var size = enhanced ? GetEnhMetaFileBits(metafile, 0, null) : GetMetaFileBitsEx(metafile, 0, null);
                if (size == 0 || size > MaximumBytes) throw new InvalidOperationException("Invalid clipboard metafile size.");
                var bytes = new byte[(int)size];
                var copied = enhanced ? GetEnhMetaFileBits(metafile, size, bytes) : GetMetaFileBitsEx(metafile, size, bytes);
                if (copied != size) throw new InvalidOperationException("Clipboard metafile copy was incomplete.");
                return bytes;
            }

            internal void Write()
            {
                IntPtr handle = IntPtr.Zero, metafile = IntPtr.Zero;
                try
                {
                    handle = CreateHandle(out metafile);
                    if (handle == IntPtr.Zero || SetClipboardData(Format, handle) == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    handle = IntPtr.Zero;
                    metafile = IntPtr.Zero; // Clipboard owns both handles after SetClipboardData.
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                    {
                        if (Format == 14 || Format == 0x8E) DeleteEnhMetaFile(handle);
                        else if (Format == 2 || Format == 0x82) DeleteObject(handle);
                        else GlobalFree(handle);
                    }
                    if (metafile != IntPtr.Zero) DeleteMetaFile(metafile);
                }
            }

            private IntPtr CreateHandle(out IntPtr metafile)
            {
                metafile = IntPtr.Zero;
                if (Format == 14 || Format == 0x8E) return SetEnhMetaFileBits((uint)Bytes.Length, Bytes);
                if (Format == 2 || Format == 0x82) return CopyImage(_bitmap, 0, 0, 0, 0x2000);
                if (Format != 3 && Format != 0x83) return Allocate(Bytes);
                metafile = SetMetaFileBitsEx((uint)Bytes.Length, Bytes);
                if (metafile == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var handle = Allocate(new byte[Marshal.SizeOf<MetafilePicture>()]);
                try
                {
                    var pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        var picture = _picture;
                        picture.Metafile = metafile;
                        Marshal.StructureToPtr(picture, pointer, false);
                    }
                    finally { GlobalUnlock(handle); }
                    return handle;
                }
                catch { GlobalFree(handle); throw; }
            }

            private static IntPtr Allocate(byte[] bytes)
            {
                var handle = GlobalAlloc(2 /* GMEM_MOVEABLE */, new UIntPtr((uint)bytes.Length));
                if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) { GlobalFree(handle); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(handle); }
                return handle;
            }

            public void Dispose()
            {
                if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
            }
        }
    }
}
