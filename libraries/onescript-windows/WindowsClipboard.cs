using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScriptEngine.Machine.Contexts;
using ZBSWindowsComponent;

namespace OneScript.Windows
{
    [ContextClass("БуферWindows", "WindowsClipboard")]
    public sealed class WindowsClipboard : AutoContext<WindowsClipboard>
    {
        private static IntPtr _lastWriteMemory;
        [ScriptConstructor]
        public static WindowsClipboard Constructor() => new WindowsClipboard();

        [ContextProperty("Версия", "SequenceNumber")]
        public decimal SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

        [ContextProperty("ВерсияСобственнойЗаписи", "OwnedSequenceNumber")]
        public decimal OwnedSequenceNumber => ClipboardDispatcher.Shared.Run(CurrentOwnedSequence);

        [ContextMethod("ПрочитатьТекст", "ReadText")]
        public string ReadText() => ClipboardDispatcher.Shared.Run(() => Clipboard.GetText(TextDataFormat.UnicodeText));

        [ContextMethod("ЗавершитьСлужбы", "Shutdown")]
        public void Shutdown() => ClipboardDispatcher.ShutdownShared();

        [ContextMethod("Сохранить", "Save")]
        public WindowsClipboardSnapshot Save() => ClipboardDispatcher.Shared.Run(() =>
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            var data = Clipboard.GetDataObject();
            var formats = data?.GetFormats(false).Select(name => DataFormats.GetFormat(name).Id).Distinct().ToArray() ?? Array.Empty<int>();
            if (NativeMethods.GetClipboardSequenceNumber() != sequence)
                throw new InvalidOperationException("Clipboard changed while enumerating formats.");
            var snapshot = formats.Length == 0 ? new NativeClipboardSnapshot() : NativeClipboardSnapshot.Capture(formats, sequence);
            return new WindowsClipboardSnapshot(snapshot, sequence);
        });

        [ContextMethod("ЗаписатьТекст", "WriteText")]
        public decimal WriteText(string text, decimal expectedSequence = -1)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length > 4 * 1024 * 1024 || text.IndexOf('\0') >= 0)
                throw new ArgumentException("Text exceeds the limit or contains a NUL character.", nameof(text));
            var expected = expectedSequence == -1 ? (uint?)null : WindowsClipboardSnapshot.ValidateSequence(expectedSequence);
            return ClipboardDispatcher.Shared.Run(() => WriteNativeText(text, expected));
        }

        // CloseClipboard can finalize formats and advance the sequence. Reopen
        // read-only and verify both owner HWND and HGLOBAL before sampling it.
        private static uint WriteNativeText(string text, uint? expectedSequence)
        {
            var memory = AllocateText(text);
            try
            {
                OpenClipboard();
                try
                {
                    if (expectedSequence.HasValue && NativeMethods.GetClipboardSequenceNumber() != expectedSequence.Value)
                        throw new InvalidOperationException("Clipboard changed before write.");
                    if (!NativeMethods.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                    _lastWriteMemory = IntPtr.Zero;
                    if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, memory) == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    _lastWriteMemory = memory;
                    memory = IntPtr.Zero;
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
            var sequence = CurrentOwnedSequence();
            if (sequence < 0) throw new InvalidOperationException("Clipboard was replaced after our write.");
            return (uint)sequence;
        }

        private static IntPtr AllocateText(string text)
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var memory = GlobalAlloc(2, new UIntPtr((uint)bytes.Length));
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(memory); }
                return memory;
            }
            catch { GlobalFree(memory); throw; }
        }

        private static decimal CurrentOwnedSequence()
        {
            if (_lastWriteMemory == IntPtr.Zero) return -1;
            OpenClipboard();
            try
            {
                if (GetClipboardOwner() != ClipboardDispatcher.Shared.WindowHandle
                    || NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT) != _lastWriteMemory) return -1;
                return NativeMethods.GetClipboardSequenceNumber();
            }
            finally { NativeMethods.CloseClipboard(); }
        }

        private static void OpenClipboard()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (NativeMethods.OpenClipboard(ClipboardDispatcher.Shared.WindowHandle)) return;
                ClipboardDispatcher.PumpWait(25);
            }
            throw new InvalidOperationException("Clipboard is busy.");
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    }
}
