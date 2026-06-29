using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using static G_Lumen.Services.DisplayConfigNative;

namespace G_Lumen.Services
{
    /// <summary>
    /// Ovládání jasu na HDR monitorech přes "SDR white level" (DisplayConfig API).
    /// Když je monitor v HDR, DDC 0x10 typicky neovládá reálný jas — místo toho
    /// se mění SDR white level (to, co dělá ve Windows posuvník "Jas obsahu SDR").
    ///
    /// Narozdíl od DDC je ČTENÍ přes DisplayConfig dokumentované a funguje,
    /// takže slider umí v HDR režimu ukázat skutečnou hodnotu.
    /// </summary>
    public sealed class HdrService
    {
        /// <summary>Rozsah jasu v nitech namapovaný na slider 0–100 %.</summary>
        public const double MinNits = 80.0;
        public const double MaxNits = 480.0;

        private readonly ILogger _log;

        // GDI device name (\\.\DISPLAYx, upper) → cíl pro DisplayConfig dotazy.
        private readonly Dictionary<string, (LUID adapterId, uint targetId)> _targets =
            new(StringComparer.OrdinalIgnoreCase);

        public HdrService(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>Znovu načte aktivní DisplayConfig cesty a namapuje GDI názvy na cíle.</summary>
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
                    _log.LogWarning("QueryDisplayConfig failed rc={Rc}", qrc);
                    return;
                }

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
                _log.LogWarning(ex, "RefreshPaths failed");
            }
        }

        /// <summary>
        /// Je to reálný displej s DisplayConfig cestou (a tedy kandidát na HDR/SDR
        /// white level)? Nezávisí na čtení HDR stavu, které na některých ovladačích
        /// (AMD + levný adaptér) selhává stejně jako DDC read-back.
        /// </summary>
        public bool HasTarget(string gdiDeviceName) => _targets.ContainsKey(gdiDeviceName);

        /// <summary>Podporuje monitor pokročilé barvy (HDR), ať už je teď zapnuté nebo ne?</summary>
        public bool IsHdrAvailable(string gdiDeviceName)
            => TryGetAdvancedColor(gdiDeviceName, out var info) && info.AdvancedColorSupported;

        /// <summary>Je na monitoru právě teď zapnuté HDR?</summary>
        public bool IsHdrActive(string gdiDeviceName)
            => TryGetAdvancedColor(gdiDeviceName, out var info) && info.AdvancedColorEnabled;

        /// <summary>Přečte aktuální SDR white level v nitech.</summary>
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
                if (DisplayConfigGetDeviceInfo(ref packet) != ERROR_SUCCESS)
                    return false;

                nits = packet.SDRWhiteLevel * 80.0 / 1000.0;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TryGetSdrNits failed");
                return false;
            }
        }

        /// <summary>Nastaví SDR white level v nitech (nedokumentované API).</summary>
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
                if (rc != ERROR_SUCCESS)
                    _log.LogWarning("DisplayConfigSetDeviceInfo rc={Rc} ({Gdi})", rc, gdiDeviceName);
                else
                    _log.LogDebug("SDR white level {Nits} nits -> {Gdi}", nits, gdiDeviceName);
                return rc == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SetSdrNits failed");
                return false;
            }
        }

        /// <summary>Převede procenta slideru (0–100) na nity v rozsahu Min–Max.</summary>
        public static double PercentToNits(int percent)
            => MinNits + Math.Clamp(percent, 0, 100) / 100.0 * (MaxNits - MinNits);

        /// <summary>Převede nity zpět na procenta slideru (0–100).</summary>
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
                    _log.LogDebug("HDR {Gdi}: supported={Sup} enabled={En}",
                        gdiDeviceName, info.AdvancedColorSupported, info.AdvancedColorEnabled);
                return rc == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "TryGetAdvancedColor failed");
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
