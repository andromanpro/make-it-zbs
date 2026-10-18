using System;
using System.Threading;
using System.Text;
using System.Diagnostics;
using ScriptEngine.Machine;

namespace ZBSWindowsComponent
{
    internal sealed class CaptureSource
    {
        public IntPtr Handle { get; }
        public uint ProcessId { get; }
        private readonly CancellationToken _cancellation;
        private readonly string _title;
        private readonly WorkOperation _operation;

        public CaptureSource(IntPtr handle, int expectedProcessId = 0, string expectedTitle = null)
        {
            Handle = handle;
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            ProcessId = expectedProcessId == 0 ? pid : (uint)expectedProcessId;
            _title = expectedTitle ?? ReadTitle(handle);
            _cancellation = Operations.Current?.Token ?? CancellationToken.None;
            _operation = Operations.Current;
            Validate();
        }

        public void Validate()
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_operation != null && !ReferenceEquals(Operations.Active, _operation))
                throw new OperationCanceledException();
            NativeMethods.GetWindowThreadProcessId(Handle, out var actualPid);
            var foreground = NativeMethods.GetForegroundWindow();
            var sameTitle = string.Equals(_title, ReadTitle(Handle), StringComparison.Ordinal);
            if (!Matches(Handle, ProcessId, foreground, actualPid) || !sameTitle)
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
                Trace.TraceWarning("zbs.source: expected=" + Handle.ToInt64() + ":" + ProcessId
                    + "; foreground=" + foreground.ToInt64() + ":" + foregroundPid + "; title-match=" + sameTitle);
                throw new RuntimeException("Исходное окно изменилось во время захвата. Повторите команду в нужном окне.");
            }
        }

        private static string ReadTitle(IntPtr handle)
        {
            var title = new StringBuilder(256);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            return title.ToString();
        }

        internal static bool Matches(IntPtr expected, uint expectedPid, IntPtr foreground, uint actualPid) =>
            expected != IntPtr.Zero && expectedPid != 0 && expected == foreground && expectedPid == actualPid;

        internal static bool ShouldRestoreClipboard(uint sentinelSequence, uint capturedSequence, uint currentSequence) =>
            sentinelSequence != 0 && (currentSequence == sentinelSequence
                || (capturedSequence != 0 && currentSequence == capturedSequence));
    }
}
