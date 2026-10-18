using System;
using ScriptEngine.Machine.Contexts;
using ZBSWindowsComponent;

namespace OneScript.Windows
{
    [ContextClass("СнимокБуфераWindows", "WindowsClipboardSnapshot")]
    public sealed class WindowsClipboardSnapshot : AutoContext<WindowsClipboardSnapshot>, IDisposable
    {
        private readonly object _gate = new object();
        private NativeClipboardSnapshot _snapshot;

        internal WindowsClipboardSnapshot(NativeClipboardSnapshot snapshot, uint sequence)
        {
            _snapshot = snapshot;
            SequenceNumber = sequence;
        }

        [ContextProperty("Версия", "SequenceNumber")]
        public decimal SequenceNumber { get; }

        [ContextMethod("Восстановить", "Restore")]
        public bool Restore(decimal expectedSequence)
        {
            var sequence = ValidateSequence(expectedSequence);
            lock (_gate)
            {
                if (_snapshot == null) throw new ObjectDisposedException(nameof(WindowsClipboardSnapshot));
                return ClipboardDispatcher.Shared.Run(() =>
                {
                    if (NativeMethods.GetClipboardSequenceNumber() != sequence) return false;
                    _snapshot.Restore(sequence);
                    return true;
                });
            }
        }

        internal static uint ValidateSequence(decimal sequence)
        {
            if (sequence < 0 || sequence > uint.MaxValue || decimal.Truncate(sequence) != sequence)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            return (uint)sequence;
        }

        [ContextMethod("Закрыть", "Close")]
        public void Close() => Dispose();

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            lock (_gate)
            {
                _snapshot?.Dispose();
                _snapshot = null;
            }
        }

        ~WindowsClipboardSnapshot() { Release(); }
    }
}
