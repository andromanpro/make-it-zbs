using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace ZBSWindowsComponent
{
    internal sealed class ClipboardDispatcher : IDisposable
    {
        private static readonly Lazy<ClipboardDispatcher> Instance = new Lazy<ClipboardDispatcher>(() => new ClipboardDispatcher());
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private readonly Thread _thread;
        private readonly SemaphoreSlim _admission = new SemaphoreSlim(1, 1);
        private Control _control;
        private ApplicationContext _context;
        private int _disposed;

        internal static ClipboardDispatcher Shared => Instance.Value;
        internal static void ShutdownShared() { if (Instance.IsValueCreated) Instance.Value.Dispose(); }
        internal IntPtr WindowHandle => _control.Handle;

        internal ClipboardDispatcher()
        {
            _thread = new Thread(() =>
            {
                using (_control = new Control { Visible = false })
                using (_context = new ApplicationContext())
                {
                    var handle = _control.Handle;
                    GC.KeepAlive(handle);
                    _ready.Set();
                    Application.Run(_context);
                }
            }) { IsBackground = true, Name = "MakeItZBS.Clipboard" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(5000)) throw new TimeoutException("Clipboard apartment did not initialize.");
        }

        internal T Run<T>(Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ClipboardDispatcher));
            if (Thread.CurrentThread == _thread) return action();
            _admission.Wait();
            try
            {
                if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ClipboardDispatcher));
                try { return (T)_control.Invoke(action); }
                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
            }
            finally { _admission.Release(); }
        }

        internal static void PumpWait(int milliseconds)
        {
            // Clipboard owners receive synchronous OLE/window messages while
            // another application copies. Sleeping on the owner STA blocks it.
            var elapsed = Stopwatch.StartNew();
            do
            {
                Application.DoEvents();
                Thread.Sleep(1);
            } while (elapsed.ElapsedMilliseconds < milliseconds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _control.BeginInvoke(new Action(() => _context.ExitThread()));
            _thread.Join(1000);
            _ready.Dispose();
        }
    }
}
