using System;

namespace ZBSWindowsComponent
{
    internal static class SettingsSaveWorkflow
    {
        internal static Exception Commit(Action save, Action startup, Action apply)
        {
            save();
            Exception warning = null;
            try { startup(); }
            catch (Exception error) { warning = error; }
            // Registry startup is optional; a committed configuration must still
            // reach the normal live-reload path when registry access is denied.
            apply();
            return warning;
        }
    }
}
