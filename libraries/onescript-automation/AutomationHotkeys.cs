using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ScriptEngine.Machine.Contexts;
using ZBSWindowsComponent;
using ZBSWindowsComponent.Helpers;

namespace OneScript.Automation
{
    [ContextClass("КлавишиАвтоматизации", "AutomationHotkeys")]
    public sealed class AutomationHotkeys : AutoContext<AutomationHotkeys>, IDisposable
    {
        private readonly object _lifecycle = new object();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private readonly Thread _thread;
        private HiddenWindow _window;
        private Exception _startupError;
        private int _registeredId;
        private int _nextId;
        private string _key;
        private string _pending;
        private int _busy;
        private bool _disposed;
        private readonly Func<IntPtr, int, uint, uint, bool> _register;
        private readonly Func<IntPtr, int, bool> _unregister;

        [ScriptConstructor]
        public static AutomationHotkeys Constructor() => new AutomationHotkeys();

        public AutomationHotkeys() : this(NativeMethods.RegisterHotKey, NativeMethods.UnregisterHotKey) { }

        internal AutomationHotkeys(Func<IntPtr, int, uint, uint, bool> register, Func<IntPtr, int, bool> unregister)
        {
            _register = register ?? throw new ArgumentNullException(nameof(register));
            _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
            _thread = new Thread(() =>
            {
                try
                {
                    using (_window = new HiddenWindow(this))
                    {
                        var handle = _window.Handle;
                        GC.KeepAlive(handle);
                        _ready.Set();
                        Application.Run();
                    }
                }
                catch (Exception ex) { _startupError = ex; _ready.Set(); }
            }) { IsBackground = true, Name = "OneScript.Automation hotkeys" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(5000)) throw new TimeoutException("Hotkey window startup timed out.");
            if (_startupError != null) throw new InvalidOperationException("Hotkey window startup failed.", _startupError);
        }

        [ContextMethod("Слушать", "Listen")]
        public void Listen(string key)
        {
            var parsed = KeyConverter.Parse(key);
            lock (_lifecycle)
            {
                ThrowIfDisposed();
                if (string.Equals(_key, key, StringComparison.OrdinalIgnoreCase)) return;
                _window.Invoke(new Action(() =>
                {
                    var id = checked(++_nextId);
                    if (!_register(_window.Handle, id, parsed.modifiers, parsed.vkCode))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Hotkey is unavailable.");
                    if (_registeredId != 0) _unregister(_window.Handle, _registeredId);
                    _registeredId = id;
                    _key = key;
                    Interlocked.Exchange(ref _pending, null);
                }));
            }
        }

        [ContextMethod("ПолучитьНажатие", "TakePressed")]
        public string TakePressed()
        {
            lock (_lifecycle)
            {
                ThrowIfDisposed();
                return Interlocked.Exchange(ref _pending, null) ?? string.Empty;
            }
        }

        [ContextMethod("УстановитьЗанятость", "SetBusy")]
        public void SetBusy(bool busy)
        {
            lock (_lifecycle)
            {
                ThrowIfDisposed();
                _window.Invoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _busy, busy ? 1 : 0);
                    Interlocked.Exchange(ref _pending, null);
                }));
            }
        }

        internal void OnPressed(int id)
        {
            if (id == _registeredId && Volatile.Read(ref _busy) == 0)
                Interlocked.CompareExchange(ref _pending, _key, null);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AutomationHotkeys));
        }

        [ContextMethod("Закрыть", "Close")]
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;
                _window.Invoke(new Action(() =>
                {
                    if (_registeredId != 0) _unregister(_window.Handle, _registeredId);
                    _registeredId = 0;
                    Interlocked.Exchange(ref _pending, null);
                    Application.ExitThread();
                }));
            }
            if (!_thread.Join(5000)) throw new TimeoutException("Hotkey window shutdown timed out.");
            _ready.Dispose();
        }

        private sealed class HiddenWindow : Form
        {
            private readonly AutomationHotkeys _owner;
            internal HiddenWindow(AutomationHotkeys owner) { _owner = owner; ShowInTaskbar = false; }
            protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == NativeMethods.WM_HOTKEY) _owner.OnPressed(message.WParam.ToInt32());
                base.WndProc(ref message);
            }
        }
    }
}
