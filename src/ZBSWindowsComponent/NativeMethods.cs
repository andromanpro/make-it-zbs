using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ZBSWindowsComponent
{
    /// <summary>
    /// P/Invoke декларации для Win32 API
    /// </summary>
    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        #region DPI

        // Win10 1703+: lets the WinForms STA follow the monitor where a window
        // is displayed even when the embedding oscript.exe has older DPI metadata.
        internal static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 =
            new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetThreadDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        #endregion

        #region Hotkeys

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Модификаторы для RegisterHotKey
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        // Message для hotkey
        public const int WM_HOTKEY = 0x0312;

        #endregion

        #region Console

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int SW_HIDE = 0;

        #endregion

        #region DWM

        // DWM dark title bar: Win10 1809..1909 uses attribute 19, Win10 2004+ / Win11 uses 20.
        // Try both for compatibility; old systems fail open.
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public static void TryEnableDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)); }
            catch
            {
                // The attribute is optional and absent on older Windows builds.
            }
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int)); }
            catch
            {
                // Dark title bars are cosmetic; failure must not block the window.
            }
        }

        #endregion

        #region Elevation

        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenElevation = 20;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            out TokenElevationInfo tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct TokenElevationInfo
        {
            public int TokenIsElevated;
        }

        #endregion

        #region Window Info

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int count);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        #endregion

        #region Clipboard

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int CountClipboardFormats();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint EnumClipboardFormats(uint format);

        // Форматы буфера обмена
        public const uint CF_TEXT = 1;
        public const uint CF_BITMAP = 2;
        public const uint CF_METAFILEPICT = 3;
        public const uint CF_SYLK = 4;
        public const uint CF_DIF = 5;
        public const uint CF_TIFF = 6;
        public const uint CF_OEMTEXT = 7;
        public const uint CF_DIB = 8;
        public const uint CF_PALETTE = 9;
        public const uint CF_PENDATA = 10;
        public const uint CF_RIFF = 11;
        public const uint CF_WAVE = 12;
        public const uint CF_UNICODETEXT = 13;
        public const uint CF_ENHMETAFILE = 14;
        public const uint CF_HDROP = 15;
        public const uint CF_LOCALE = 16;
        public const uint CF_DIBV5 = 17;

        #endregion

        #region SendInput (для эмуляции клавиш)

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct Input
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput mi;
            [FieldOffset(0)]
            public KeyboardInput ki;
            [FieldOffset(0)]
            public HardwareInput hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardInput
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HardwareInput
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        public const int INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYDOWN = 0x0000;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        // Virtual key codes
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_A = 0x41;
        public const ushort VK_C = 0x43;
        public const ushort VK_V = 0x56;

        #endregion
    }
}
