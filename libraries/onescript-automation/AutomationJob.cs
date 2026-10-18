using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ScriptEngine.Machine.Contexts;
using ZBSWindowsComponent;

namespace OneScript.Automation
{
    [ContextClass("ЗаданиеАвтоматизации", "AutomationJob")]
    public sealed class AutomationJob : AutoContext<AutomationJob>, IDisposable
    {
        private static int _admitted;
        private readonly object _lifecycle = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly Task<string> _task;
        private bool _disposed;
        private bool _cancelled;

        internal AutomationJob(Func<CancellationToken, string> execute)
        {
            if (Interlocked.CompareExchange(ref _admitted, 1, 0) != 0)
                throw new InvalidOperationException("An automation job is already running.");
            var token = _cancellation.Token;
            _task = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!Operations.Begin()) throw new InvalidOperationException("An operation is already running.");
                    try
                    {
                        using (token.Register(Operations.Current.Cancel))
                        {
                            token.ThrowIfCancellationRequested();
                            var result = execute(token);
                            token.ThrowIfCancellationRequested();
                            return result;
                        }
                    }
                    finally { Operations.End(); }
                }
                finally { Interlocked.Exchange(ref _admitted, 0); }
            });
            // Observe failures even when a caller disposes a job without reading its result.
            _task.ContinueWith(task => { var error = task.Exception; GC.KeepAlive(error); },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        [ContextProperty("Завершено", "IsCompleted")]
        public bool IsCompleted => _task.IsCompleted;
        [ContextProperty("Отменено", "IsCancelled")]
        public bool IsCancelled { get { lock (_lifecycle) return _cancelled; } }

        [ContextMethod("РезультатJSON", "ResultJson")]
        public string ResultJson()
        {
            lock (_lifecycle)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AutomationJob));
                _cancellation.Token.ThrowIfCancellationRequested();
                if (!_task.IsCompleted) throw new InvalidOperationException("The job has not completed.");
                return _task.GetAwaiter().GetResult();
            }
        }

        [ContextMethod("Отменить", "Cancel")]
        public void Cancel()
        {
            lock (_lifecycle)
            {
                if (!_disposed)
                {
                    _cancelled = true;
                    _cancellation.Cancel();
                }
            }
        }

        [ContextMethod("Закрыть", "Close")]
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;
                _cancelled = true;
                _cancellation.Cancel();
            }
            // Cancellation owns only this job; wait for its process cleanup, never a foreign client.
            try { _task.GetAwaiter().GetResult(); }
            catch (Exception ex) when (_task.IsCanceled || _task.IsFaulted)
            {
                Trace.TraceInformation("Automation job cleanup: " + ex.GetType().Name);
            }
            _cancellation.Dispose();
        }
    }
}
