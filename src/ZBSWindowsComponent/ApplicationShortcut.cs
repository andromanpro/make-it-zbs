using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ZBSWindowsComponent
{
    internal static class ApplicationShortcut
    {
        internal static void Ensure(string path, string launcher, bool onlyMatchingTarget = false)
        {
            if (onlyMatchingTarget && !File.Exists(path)) return;
            var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("00021401-0000-0000-C000-000000000046"), true));
            try
            {
                if (File.Exists(path))
                {
                    ((IPersistFile)link).Load(path, 2);
                    var target = new StringBuilder(32768);
                    link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                    if (onlyMatchingTarget && !string.Equals(target.ToString(), launcher, StringComparison.OrdinalIgnoreCase)) return;
                    if (string.Equals(target.ToString(), launcher, StringComparison.OrdinalIgnoreCase)
                        && ReadAppId((IPropertyStore)link) == WindowsAppIdentity.AppId) return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!onlyMatchingTarget)
                {
                    link.SetPath(launcher);
                    link.SetArguments(string.Empty);
                    link.SetWorkingDirectory(Path.GetDirectoryName(launcher));
                    link.SetDescription(WindowsAppIdentity.DisplayName);
                    link.SetIconLocation(launcher, 0);
                }
                var key = AppIdKey;
                var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(WindowsAppIdentity.AppId) };
                try { ((IPropertyStore)link).SetValue(ref key, ref value); ((IPropertyStore)link).Commit(); }
                finally { PropVariantClear(ref value); }
                ((IPersistFile)link).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        private static string ReadAppId(IPropertyStore store)
        {
            var key = AppIdKey;
            store.GetValue(ref key, out var value);
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : null; }
            finally { PropVariantClear(ref value); }
        }

        private static PropertyKey AppIdKey => new PropertyKey
        {
            Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            internal ushort Type, Reserved1, Reserved2, Reserved3;
            internal IntPtr Value, Extra;
        }
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int PropVariantClear(ref PropVariant variant);

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, IntPtr data, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int command);
            void SetShowCmd(int command);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
    }
}
