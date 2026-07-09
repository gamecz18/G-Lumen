using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using static G_Lumen.Services.DisplayConfigNative;

namespace G_Lumen.Services
{
    /// <summary>
    /// Controls brightness on HDR monitors via "SDR white level" (DisplayConfig API).
    /// When a monitor is in HDR, DDC 0x10 typically doesn't control real brightness —
    /// instead the SDR white level changes (what Windows' "SDR content brightness"
    /// slider does).
    ///
    /// Unlike DDC, READING via DisplayConfig is documented and works, so the slider
    /// can show the actual value in HDR mode.
    /// </summary>
    public sealed class HdrService
    {
        /// <summary>Brightness range in nits, mapped to the 0–100% slider.</summary>
        public const double MinNits = 80.0;
        public const double MaxNits = 480.0;

        private readonly ILogger _log;
        private readonly TrafficLog _traffic;

        // GDI device name (\\.\DISPLAYx, upper) → target for DisplayConfig queries.
        private readonly Dictionary<string, (LUID adapterId, uint targetId)> _targets =
            new(StringComparer.OrdinalIgnoreCase);

        public HdrService(ILogger logger, TrafficLog traffic)
        {
            _log = logger;
            _traffic = traffic;
        }

        /// <summary>Reloads active DisplayConfig paths and maps GDI names to targets.</summary>
        public void RefreshPaths()
        {
            _targets.Clear();
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                    return;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                int qrc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (qrc != ERROR_SUCCESS)
                {
                    _traffic.In("DispCfg", "QueryDisplayConfig failed", false, qrc);
                    _log.LogWarning("QueryDisplayConfig failed rc={Rc}", qrc);
                    return;
                }

                _traffic.In("DispCfg", $"QueryDisplayConfig → {pathCount} active path(s)", true);

                for (int i = 0; i < pathCount; i++)
                {
                    var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                            size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                            adapterId = paths[i].sourceInfo.adapterId,
                            id = paths[i].sourceInfo.id,
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref src) != ERROR_SUCCESS)
                        continue;
                    if (string.IsNullOrWhiteSpace(src.viewGdiDeviceName))
                        continue;

                    _targets[src.viewGdiDeviceName] =
                        (paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
                    _log.LogDebug("DisplayConfig target {Gdi} (targetId={Tid})",
                        src.viewGdiDeviceName, paths[i].targetInfo.id);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Loading DisplayConfig paths failed");
            }
        }

        /// <summary>
        /// Is this a real display with a DisplayConfig path (and therefore a candidate
        /// for HDR/SDR white level)? Doesn't depend on reading the HDR state, which
        /// fails on some drivers (AMD + cheap adapter) just like the DDC read-back.
        /// </summary>
        public bool HasTarget(string gdiDeviceName) => _targets.ContainsKey(gdiDeviceName);

        /// <summary>Does the monitor support advanced color (HDR), whether it's currently on or not?</summary>
        public bool IsHdrAvailable(string gdiDeviceName)
            => TryGetAdvancedColor(gdiDeviceName, out var info) && info.AdvancedColorSupported;

        /// <summary>Is HDR currently enabled on the monitor?</summary>
        public bool IsHdrActive(string gdiDeviceName)
            => TryGetAdvancedColor(gdiDeviceName, out var info) && info.AdvancedColorEnabled;

        /// <summary>Reads the current SDR white level in nits.</summary>
        public bool TryGetSdrNits(string gdiDeviceName, out double nits)
        {
            nits = MinNits;
            if (!_targets.TryGetValue(gdiDeviceName, out var t))
                return false;

            try
            {
                var packet = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = MakeHeader(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(), t)
                };
                int rc = DisplayConfigGetDeviceInfo(ref packet);
                if (rc != ERROR_SUCCESS)
                {
                    _traffic.In("DispCfg", $"GetSdrWhiteLevel · {gdiDeviceName}", false, rc);
                    return false;
                }

                nits = packet.SDRWhiteLevel * 80.0 / 1000.0;
                _traffic.In("DispCfg",
                    $"GetSdrWhiteLevel = {nits:0} nits (raw {packet.SDRWhiteLevel}) · {gdiDeviceName}", true);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Reading SDR white level failed");
                return false;
            }
        }

        /// <summary>Sets the SDR white level in nits (undocumented API).</summary>
        public bool SetSdrNits(string gdiDeviceName, double nits)
        {
            if (!_targets.TryGetValue(gdiDeviceName, out var t))
                return false;

            try
            {
                nits = Math.Clamp(nits, MinNits, MaxNits);
                var packet = new DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
                {
                    header = MakeHeader(DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SET_SDR_WHITE_LEVEL>(), t),
                    SDRWhiteLevel = (uint)Math.Round(nits * 1000.0 / 80.0),
                    finalValue = 1,
                };
                int rc = DisplayConfigSetDeviceInfo(ref packet);

                _traffic.Out("DispCfg",
                    $"SetSdrWhiteLevel = {nits:0} nits (raw {packet.SDRWhiteLevel}) · {gdiDeviceName}",
                    rc == ERROR_SUCCESS, rc == ERROR_SUCCESS ? null : rc);

                if (rc != ERROR_SUCCESS)
                    _log.LogWarning("SDR white level write failed rc={Rc} ({Gdi})", rc, gdiDeviceName);
                else
                    _log.LogDebug("SDR white level {Nits} nits -> {Gdi}", nits, gdiDeviceName);
                return rc == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SDR white level write failed (exception)");
                return false;
            }
        }

        /// <summary>Converts a slider percentage (0–100) to nits within the Min–Max range.</summary>
        public static double PercentToNits(int percent)
            => MinNits + Math.Clamp(percent, 0, 100) / 100.0 * (MaxNits - MinNits);

        /// <summary>Converts nits back to a slider percentage (0–100).</summary>
        public static int NitsToPercent(double nits)
            => (int)Math.Round(Math.Clamp((nits - MinNits) / (MaxNits - MinNits), 0.0, 1.0) * 100);

        private bool TryGetAdvancedColor(string gdiDeviceName, out DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info)
        {
            info = default;
            if (!_targets.TryGetValue(gdiDeviceName, out var t))
                return false;

            try
            {
                info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                {
                    header = MakeHeader(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(), t)
                };
                int rc = DisplayConfigGetDeviceInfo(ref info);
                if (rc == ERROR_SUCCESS)
                {
                    _traffic.In("DispCfg",
                        $"GetAdvancedColorInfo · {gdiDeviceName}: HDR supported={(info.AdvancedColorSupported ? "yes" : "no")}, active={(info.AdvancedColorEnabled ? "yes" : "no")}",
                        true);
                    _log.LogDebug("HDR {Gdi}: supported={Sup} enabled={En}",
                        gdiDeviceName, info.AdvancedColorSupported, info.AdvancedColorEnabled);
                }
                else
                {
                    _traffic.In("DispCfg", $"GetAdvancedColorInfo · {gdiDeviceName}", false, rc);
                }
                return rc == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Reading HDR state failed (exception)");
                return false;
            }
        }

        private static DISPLAYCONFIG_DEVICE_INFO_HEADER MakeHeader(uint type, uint size, (LUID adapterId, uint targetId) t)
            => new()
            {
                type = type,
                size = size,
                adapterId = t.adapterId,
                id = t.targetId,
            };
    }
}
