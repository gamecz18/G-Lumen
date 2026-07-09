using System;

namespace G_Lumen.Services
{
    /// <summary>
    /// Represents one physical monitor that we can send DDC/CI commands to.
    /// The handle is kept alive by <see cref="DdcCiService"/> for the app's lifetime
    /// (released only in <see cref="DdcCiService.Dispose"/>).
    /// </summary>
    public sealed class MonitorInfo
    {
        /// <summary>Physical monitor handle from GetPhysicalMonitorsFromHMONITOR.</summary>
        public IntPtr HPhysical { get; init; }

        /// <summary>
        /// Stable identifier for persistence (from EnumDisplayDevices DeviceID + index).
        /// Survives app restarts and is used as the key into settings.json.
        /// </summary>
        public string StableId { get; init; } = string.Empty;

        /// <summary>Human-readable description (e.g. "Generic PnP Monitor").</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// GDI display name (\\.\DISPLAYx) from MONITORINFOEX — bridges to the
        /// DisplayConfig API for HDR / SDR white level.
        /// </summary>
        public string GdiDeviceName { get; init; } = string.Empty;
    }
}
