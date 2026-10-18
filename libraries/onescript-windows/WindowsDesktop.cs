using System;
using System.Text;
using ScriptEngine.Machine.Contexts;
using ZBSWindowsComponent;

namespace OneScript.Windows
{
    [ContextClass("РабочийСтолWindows", "WindowsDesktop")]
    public sealed class WindowsDesktop : AutoContext<WindowsDesktop>
    {
        [ScriptConstructor]
        public static WindowsDesktop Constructor() => new WindowsDesktop();

        [ContextMethod("АктивноеОкно", "ForegroundWindow")]
        public WindowsWindow ForegroundWindow()
        {
            var handle = NativeMethods.GetForegroundWindow();
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
                throw new InvalidOperationException("No foreground window.");
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            var title = new StringBuilder(1024);
            var className = new StringBuilder(256);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            NativeMethods.GetClassName(handle, className, className.Capacity);
            return new WindowsWindow(handle, processId, title.ToString(), className.ToString());
        }
    }

    [ContextClass("ОкноWindows", "WindowsWindow")]
    public sealed class WindowsWindow : AutoContext<WindowsWindow>
    {
        private readonly IntPtr _handle;
        private readonly uint _processId;

        internal WindowsWindow(IntPtr handle, uint processId, string title, string className)
        {
            _handle = handle;
            _processId = processId;
            Title = title;
            ClassName = className;
        }

        [ContextProperty("Дескриптор", "Handle")]
        public decimal Handle => _handle.ToInt64();
        [ContextProperty("ИдентификаторПроцесса", "ProcessId")]
        public decimal ProcessId => _processId;
        [ContextProperty("Заголовок", "Title")]
        public string Title { get; }
        [ContextProperty("Класс", "ClassName")]
        public string ClassName { get; }

        [ContextMethod("Активно", "IsForeground")]
        public bool IsForeground()
        {
            NativeMethods.GetWindowThreadProcessId(_handle, out var processId);
            return NativeMethods.IsWindow(_handle) && processId == _processId && NativeMethods.GetForegroundWindow() == _handle;
        }
    }
}
