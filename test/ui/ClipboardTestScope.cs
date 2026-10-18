using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    internal sealed class ClipboardTestScope : IDisposable
    {
        private readonly NativeClipboardSnapshot _original;
        private readonly HashSet<uint> _owners = new HashSet<uint> { NativeMethods.GetCurrentProcessId() };
        private bool _disposed;
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();

        internal ClipboardTestScope()
        {
            _original = ClipboardDispatcher.Shared.Run(Capture);
        }

        internal void AllowOwner(int processId) => _owners.Add((uint)processId);

        internal static NativeClipboardSnapshot Capture()
        {
            var formats = Clipboard.GetDataObject()?.GetFormats(false) ?? Array.Empty<string>();
            return formats.Length == 0 ? new NativeClipboardSnapshot()
                : NativeClipboardSnapshot.Capture(Array.ConvertAll(formats, format => DataFormats.GetFormat(format).Id), NativeMethods.GetClipboardSequenceNumber());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                ClipboardDispatcher.Shared.Run(() =>
                {
                    NativeMethods.GetWindowThreadProcessId(GetClipboardOwner(), out var owner);
                    if (_owners.Contains(owner)) _original.Restore(NativeMethods.GetClipboardSequenceNumber());
                    return true;
                });
            }
            finally { _original.Dispose(); }
        }
    }
}
