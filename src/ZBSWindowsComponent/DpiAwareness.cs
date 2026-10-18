using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZBSWindowsComponent
{
    internal static class DpiAwareness
    {
        private const int LogicalDpi = 96;

        internal static bool TryEnablePerMonitorV2ForCurrentThread()
        {
            return NativeMethods.SetThreadDpiAwarenessContext(
                NativeMethods.DpiAwarenessContextPerMonitorAwareV2) != IntPtr.Zero;
        }

        internal static void ConfigureForm(Form form)
        {
            // All dimensions in source are logical pixels at 96 DPI. WinForms
            // computes the actual scale from the monitor that owns the HWND.
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }

        internal static Size ScaleLogicalSize(Size logicalSize, int dpi)
        {
            dpi = dpi > 0 ? dpi : LogicalDpi;

            return new Size(
                ScaleLogicalValue(logicalSize.Width, dpi),
                ScaleLogicalValue(logicalSize.Height, dpi));
        }

        internal static void ApplyInitialFormSize(
            Form form, Size logicalSize, Size logicalMinimumSize)
        {
            var dpi = GetWindowDpi(form);
            form.MinimumSize = ScaleLogicalSize(logicalMinimumSize, dpi);
            form.Size = ScaleLogicalSize(logicalSize, dpi);
        }

        internal static int GetWindowDpi(Form form)
        {
            var dpi = form.IsHandleCreated
                ? NativeMethods.GetDpiForWindow(form.Handle)
                : (uint)form.DeviceDpi;
            return dpi > 0 ? (int)dpi : LogicalDpi;
        }

        private static int ScaleLogicalValue(int value, int dpi)
        {
            return (int)Math.Round(
                value * (double)dpi / LogicalDpi,
                MidpointRounding.AwayFromZero);
        }
    }
}
