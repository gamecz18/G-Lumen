using System;

namespace G_Lumen.Services
{
    /// <summary>
    /// Reprezentuje jeden fyzický monitor, na který umíme posílat DDC/CI příkazy.
    /// Handle drží <see cref="DdcCiService"/> naživu po dobu běhu aplikace
    /// (uvolní se až v <see cref="DdcCiService.Dispose"/>).
    /// </summary>
    public sealed class MonitorInfo
    {
        /// <summary>Handle fyzického monitoru z GetPhysicalMonitorsFromHMONITOR.</summary>
        public IntPtr HPhysical { get; init; }

        /// <summary>
        /// Stabilní identifikátor pro persistenci (z EnumDisplayDevices DeviceID + index).
        /// Přežije restart appky a slouží jako klíč do settings.json.
        /// </summary>
        public string StableId { get; init; } = string.Empty;

        /// <summary>Lidsky čitelný popis (např. "Generic PnP Monitor").</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// GDI název displeje (\\.\DISPLAYx) z MONITORINFOEX — bridge na
        /// DisplayConfig API pro HDR / SDR white level.
        /// </summary>
        public string GdiDeviceName { get; init; } = string.Empty;
    }
}
