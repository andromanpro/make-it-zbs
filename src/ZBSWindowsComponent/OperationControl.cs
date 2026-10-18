using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    internal sealed class WorkOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly List<Action> _cleanup = new List<Action>();
        private readonly object _lifecycle = new object();
        private bool _disposed;
        private int _cancelling;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        internal TimeSpan Elapsed => _clock.Elapsed;
        public CancellationToken Token { get; }
        public WorkOperation() { Token = _cancellation.Token; }
        public void Cancel()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _cancelling++;
            }
            try { _cancellation.Cancel(); }
            catch (AggregateException ex) { Trace.TraceWarning("Operation cancellation callback: " + ex.GetType().Name); }
            finally
            {
                lock (_lifecycle)
                {
                    _cancelling--;
                    if (_disposed && _cancelling == 0) _cancellation.Dispose();
                }
            }
        }
        public void OwnCleanup(Action cleanup)
        {
            lock (_lifecycle)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WorkOperation));
                _cleanup.Add(cleanup);
            }
        }
        public void Dispose()
        {
            Action[] pending;
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;
                pending = _cleanup.ToArray();
                _cleanup.Clear();
                if (_cancelling == 0) _cancellation.Dispose();
            }
            foreach (var cleanup in pending)
            {
                try { cleanup(); }
                catch (Exception ex) { Trace.TraceWarning("Operation cleanup: " + ex.GetType().Name); }
            }
            _clock.Stop();
        }
    }

    internal static class Operations
    {
        private static readonly object Sync = new object();
        private static WorkOperation _active;
        [ThreadStatic] private static WorkOperation _current;
        public static WorkOperation Current => _current;
        public static WorkOperation Active { get { lock (Sync) return _active; } }
        public static bool IsBusy { get { lock (Sync) return _active != null; } }
        public static bool IsCancelling { get { lock (Sync) return _active?.Token.IsCancellationRequested == true; } }

        public static bool Begin()
        {
            lock (Sync)
            {
                if (_active != null) return false;
                _current = _active = new WorkOperation();
                return true;
            }
        }

        public static void Cancel()
        {
            WorkOperation operation;
            lock (Sync) operation = _active;
            operation?.Cancel();
        }

        public static void Cancel(WorkOperation expected)
        {
            lock (Sync)
            {
                if (!ReferenceEquals(_active, expected)) return;
            }
            expected?.Cancel();
        }

        public static void End()
        {
            WorkOperation finished;
            lock (Sync)
            {
                if (_current == null) return;
                finished = _current;
                _current = null;
            }
            try { finished.Dispose(); }
            finally
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_active, finished)) _active = null;
                }
            }
        }

        public static void ThrowIfCancelled() => _current?.Token.ThrowIfCancellationRequested();
    }

    [ContextClass("УправлениеОперацией", "OperationControl")]
    public sealed class OperationControl : AutoContext<OperationControl>
    {
        [ScriptConstructor]
        public static OperationControl Constructor() => new OperationControl();
        [ContextMethod("Начать", "Begin")]
        public bool Begin() => Operations.Begin();
        [ContextMethod("Отменена", "IsCancelled")]
        public bool IsCancelled() => Operations.Current?.Token.IsCancellationRequested == true;
        [ContextMethod("Завершить", "End")]
        public void End() => Operations.End();
        [ContextMethod("Отменить", "Cancel")]
        public void Cancel() => Operations.Cancel();
        [ContextMethod("ПроверитьОтмену", "ThrowIfCancelled")]
        public void ThrowIfCancelled() => Operations.ThrowIfCancelled();
    }

    internal sealed class HotkeyAdmissionQueue
    {
        private readonly object _sync = new object();
        private string _pending;
        private string _source;
        private bool _processing;
        private readonly Func<string> _readSource;
        public HotkeyAdmissionQueue(Func<string> readSource = null) { _readSource = readSource; }
        public int Count { get { lock (_sync) return _pending == null ? 0 : 1; } }
        public bool Enqueue(string key, bool busy)
        {
            lock (_sync)
            {
                if (busy || _processing || _pending != null || string.IsNullOrEmpty(key)) return false;
                _pending = key;
                _source = _readSource?.Invoke();
                return true;
            }
        }
        public bool TryDequeue(out string key)
        {
            lock (_sync)
            {
                key = _pending;
                if (key == null) return false;
                _pending = null;
                if (_readSource != null && _source != _readSource()) { key = null; return false; }
                _processing = true;
                return true;
            }
        }
        public void Clear()
        {
            lock (_sync) { _pending = null; _processing = false; }
        }
    }

    internal interface IHotkeyEventSink
    {
        void Enqueue();
    }
}
