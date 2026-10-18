using System.Threading;

namespace ZBSWindowsComponent
{
    internal static class SettingsState
    {
        private static int _dirty;
        private static int _version;
        internal static int Version => Volatile.Read(ref _version);

        public static void MarkDirty()
        {
            Interlocked.Increment(ref _version);
            Interlocked.Exchange(ref _dirty, 1);
        }

        public static bool ConsumeDirty()
        {
            return Interlocked.Exchange(ref _dirty, 0) == 1;
        }
    }
}
