using System;
using System.Runtime.InteropServices;

namespace G_Lumen.Services
{
    /// <summary>
    /// Maps a GDI display name (\\.\DISPLAYx) to its position in the virtual
    /// screen, so the popup can be placed on the monitor chosen in Settings.
    /// </summary>
    internal static class ScreenLocator
    {
        /// <summary>
        /// Finds the pixel center of the given display in virtual-screen
        /// coordinates. Returns false if the display is not connected.
        /// </summary>
        public static bool TryGetCenter(string gdiDeviceName, out int x, out int y)
        {
            int cx = 0, cy = 0;
            bool found = false;

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
                {
                    var mi = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                    };

                    if (NativeMethods.GetMonitorInfo(hMonitor, ref mi) &&
                        string.Equals(mi.szDevice, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        cx = (mi.rcMonitor.left + mi.rcMonitor.right) / 2;
                        cy = (mi.rcMonitor.top + mi.rcMonitor.bottom) / 2;
                        found = true;
                        return false; // stop enumeration
                    }

                    return true;
                }, IntPtr.Zero);

            x = cx;
            y = cy;
            return found;
        }
    }
}
