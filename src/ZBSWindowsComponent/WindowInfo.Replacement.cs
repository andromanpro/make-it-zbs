using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    public partial class WindowInfo
    {
        private const int ReplacementTextLimit = 1000000;
        private const string ReplaceMode = "Replace";
        private readonly ReplacementLease _replacement = new ReplacementLease();

        [ContextMethod("ЗакрепитьИсходникЗамены", "CaptureReplacement")]
        public string CaptureReplacement(int handle, int processId, string mode, string original)
        {
            ReleaseReplacement();
            var cancellation = Operations.Current?.Token ?? CancellationToken.None;
            var captured = RunInSta("Replacement snapshot", () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var window = new IntPtr(handle);
                var result = new PasteExecutionResult();
                if (processId <= 0 || !ValidatePasteTarget(window, processId, ReplaceMode, result)
                    || !FocusPasteTarget(window, ReplaceMode, result)) return null;
                using (var process = Process.GetProcessById(processId))
                {
                    if (string.Equals(process.ProcessName, "Code", StringComparison.OrdinalIgnoreCase))
                    {
                        var editor = VsCodeEditorBridge.Capture(handle, processId, mode, cancellation, out var available);
                        if (available) return NativeMethods.GetForegroundWindow() == window ? editor : null;
                    }
                }
                return ReadReplacementSnapshot(handle, processId, mode, cancellation);
            });
            var token = _replacement.Capture(captured, original);
            if (string.IsNullOrEmpty(token))
            {
                _lastDiagnostic = "Replace: источник нельзя надёжно проверить; доступно копирование результата";
            }
            return token;
        }

        [ContextMethod("ОсвободитьПривязкуЗамены", "ReleaseReplacement")]
        public void ReleaseReplacement() => _replacement.Release();

        [ContextMethod("ЗаменитьЗакрепленныйИсходник", "ReplaceCapturedText")]
        public IValue ReplaceCapturedText(string token, string text)
        {
            var captured = _replacement.Take(token);
            if (captured == null)
            {
                return CreatePasteResult(new PasteExecutionResult().Fail("Replace: привязка исходника устарела"));
            }
            if (captured.Editor != null)
            {
                var cancellation = Operations.Current?.Token ?? CancellationToken.None;
                return CreatePasteResult(RunInSta("VS Code replacement", () =>
                {
                    var result = new PasteExecutionResult();
                    var window = new IntPtr(captured.Handle);
                    if (!ValidatePasteTarget(window, captured.ProcessId, ReplaceMode, result)
                        || !FocusPasteTarget(window, ReplaceMode, result)) return result;
                    cancellation.ThrowIfCancellationRequested();
                    if (!VsCodeEditorBridge.Apply(captured, text, cancellation))
                        return result.Fail("Replace: документ или выделение изменились, результат не применён");
                    result.Success = true;
                    return result;
                }));
            }
            var replacementCancellation = Operations.Current?.Token ?? CancellationToken.None;
            return PasteTextIntoWindow(captured.Handle, captured.ProcessId, text, captured.Mode, false,
                () => captured.Matches(ReadReplacementSnapshot(captured.Handle, captured.ProcessId, captured.Mode, replacementCancellation)));
        }

        private ReplacementSnapshot ReadReplacementSnapshot(int handle, int processId, string mode, CancellationToken cancellation = default(CancellationToken))
        {
            return RunInUia("Replacement snapshot", () => ReadReplacementSnapshotCore(handle, processId, mode), cancellation);
        }

        private static ReplacementSnapshot ReadReplacementSnapshotCore(int handle, int processId, string mode)
        {
            try
            {
                if (NativeMethods.GetForegroundWindow() != new IntPtr(handle)) return null;
                NativeMethods.GetWindowThreadProcessId(new IntPtr(handle), out var actualPid);
                var element = AutomationElement.FocusedElement;
                if (actualPid != processId || element == null || element.Current.ProcessId != processId
                    || !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
                var text = (TextPattern)pattern;
                var document = text.DocumentRange.GetText(ReplacementTextLimit + 1);
                if (document.Length > ReplacementTextLimit) return null;
                var selections = text.GetSelection();
                if (mode != "fullModule" && (selections == null || selections.Length != 1)) return null;
                var selected = string.Empty;
                var start = 0;
                if (mode != "fullModule")
                {
                    selected = selections[0].GetText(ReplacementTextLimit + 1);
                    if (selected.Length == 0 || selected.Length > ReplacementTextLimit) return null;
                    var prefix = text.DocumentRange.Clone();
                    prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
                    start = prefix.GetText(ReplacementTextLimit + 1).Length;
                }
                var title = new StringBuilder(2048);
                NativeMethods.GetWindowText(new IntPtr(handle), title, title.Capacity);
                using (var process = Process.GetProcessById(processId))
                {
                    return new ReplacementSnapshot
                    {
                        Handle = handle, ProcessId = processId, Mode = mode,
                        Identity = process.StartTime.ToUniversalTime().Ticks + ":" + title + ":"
                            + element.Current.Name + ":" + string.Join(",", element.GetRuntimeId()),
                        Document = ReplacementSnapshot.Fingerprint(document),
                        Selection = ReplacementSnapshot.Fingerprint(selected), SelectionStart = start
                    };
                }
            }
            catch (Exception)
            {
                // An unavailable or replaced UIA provider cannot authorize an edit.
                return null;
            }
        }
    }
}
