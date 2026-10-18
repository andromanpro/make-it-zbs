using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ZBSWindowsComponent
{
    internal sealed class UiaDispatcher : IDisposable
    {
        private static readonly Lazy<UiaDispatcher> Instance = new Lazy<UiaDispatcher>(() => new UiaDispatcher());
        private readonly BlockingCollection<Action> _requests = new BlockingCollection<Action>();
        private readonly SemaphoreSlim _admission = new SemaphoreSlim(1, 1);
        private readonly Thread _thread;
        private int _disposed;

        internal static UiaDispatcher Shared => Instance.Value;
        internal static void ShutdownShared() { if (Instance.IsValueCreated) Instance.Value.Dispose(); }

        internal UiaDispatcher()
        {
            _thread = new Thread(() =>
            {
                foreach (var request in _requests.GetConsumingEnumerable()) request();
            }) { IsBackground = true, Name = "MakeItZBS.UIAutomation" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        internal T Run<T>(Func<T> read, CancellationToken cancellation = default(CancellationToken), int timeoutMilliseconds = 5000)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UiaDispatcher));
            cancellation.ThrowIfCancellationRequested();
            if (Thread.CurrentThread == _thread) return read();
            if (!_admission.Wait(0)) throw new InvalidOperationException("UI Automation is still processing a previous read.");
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            result.Task.ContinueWith(failed => GC.KeepAlive(failed.Exception), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                _requests.Add(() =>
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        result.SetResult(read());
                    }
                    catch (Exception error) { result.SetException(error); }
                    finally { _admission.Release(); }
                });
            }
            catch
            {
                _admission.Release();
                throw;
            }
            // UIA providers cannot be forcibly interrupted. Keep their apartment
            // alive, reject new reads while busy, and never publish a late result.
            try
            {
                if (!result.Task.Wait(timeoutMilliseconds, cancellation)) throw new TimeoutException("UI Automation read timed out.");
            }
            catch (AggregateException) { return result.Task.GetAwaiter().GetResult(); }
            cancellation.ThrowIfCancellationRequested();
            return result.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _requests.CompleteAdding();
            if (!_thread.Join(1000)) return;
            _requests.Dispose();
            _admission.Dispose();
        }
    }
}
