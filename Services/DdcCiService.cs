using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace G_Lumen.Services
{
    /// <summary>
    /// DDC/CI service layer (Windows-only, dxva2.dll).
    /// Enumerates physical monitors and sends VCP commands.
    ///
    /// Reality on this hardware: WRITING (SetVCPFeature) works, READING
    /// (GetVCPFeatureAndVCPFeatureReply) fails with error 31 (ERROR_GEN_FAILURE),
    /// because the cheap active adapter / AMD driver can't do the I2C read-back.
    /// Reading is therefore treated as "best effort" and the app works without it.
    /// </summary>
    public sealed class DdcCiService : IDisposable
    {
        private const byte VcpBrightness = 0x10;

        private readonly ILogger _log;
        private readonly TrafficLog _traffic;

        // Physical monitor handles are kept alive for the whole app lifetime;
        // released in bulk in Dispose via DestroyPhysicalMonitors.
        private readonly List<NativeMethods.PHYSICAL_MONITOR[]> _ownedHandles = new();
        private bool _disposed;

        public DdcCiService(ILogger logger, TrafficLog traffic)
        {
            _log = logger;
            _traffic = traffic;
        }

        /// <summary>
        /// Walks all logical monitors, resolves their physical handles,
        /// and returns a list of <see cref="MonitorInfo"/>. Releases previous handles.
        /// </summary>
        public IReadOnlyList<MonitorInfo> Enumerate()
        {
            ReleaseHandles();

            var result = new List<MonitorInfo>();
            var hMonitors = new List<IntPtr>();

            NativeMethods.MonitorEnumProc cb =
                (IntPtr h, IntPtr hdc, ref NativeMethods.RECT r, IntPtr d) =>
                {
                    hMonitors.Add(h);
                    return true;
                };
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

            foreach (var h in hMonitors)
            {
                var mi = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (!NativeMethods.GetMonitorInfo(h, ref mi))
                    continue;

                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(h, out uint count) || count == 0)
                    continue;

                var pms = new NativeMethods.PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(h, count, pms))
                {
                    _traffic.In("GDI", $"GetPhysicalMonitors failed ({mi.szDevice})",
                        false, Marshal.GetLastWin32Error());
                    continue;
                }

                _ownedHandles.Add(pms);

                // Monitor DeviceID under this adapter (\\.\DISPLAYx) — used as a stable key.
                string deviceId = TryGetMonitorDeviceId(mi.szDevice);

                for (int pi = 0; pi < (int)count; pi++)
                {
                    var pm = pms[pi];
                    string desc = string.IsNullOrWhiteSpace(pm.szPhysicalMonitorDescription)
                        ? mi.szDevice
                        : pm.szPhysicalMonitorDescription;

                    result.Add(new MonitorInfo
                    {
                        HPhysical = pm.hPhysicalMonitor,
                        StableId = $"{deviceId}#{pi}",
                        Description = desc,
                        GdiDeviceName = mi.szDevice,
                    });

                    _traffic.In("GDI",
                        $"found {desc} · {mi.szDevice} · handle 0x{pm.hPhysicalMonitor.ToInt64():X}", true);
                }
            }

            _traffic.In("GDI", $"EnumDisplayMonitors → {result.Count} physical monitor(s)", true);
            return result;
        }

        /// <summary>
        /// Tries to read the current brightness (0–100). On this hardware it typically
        /// returns false (error 31) — the caller then falls back to the locally saved value.
        /// </summary>
        public bool TryGetBrightness(MonitorInfo monitor, out uint current, out uint max)
        {
            current = 0;
            max = 100;
            if (monitor.HPhysical == IntPtr.Zero)
                return false;

            try
            {
                // 1) High-level API (what Monitorian tries first).
                if (NativeMethods.GetMonitorBrightness(monitor.HPhysical, out uint _, out uint cur, out uint mx))
                {
                    current = cur;
                    max = mx == 0 ? 100 : mx;
                    _traffic.In("DDC/CI",
                        $"GetMonitorBrightness → brightness {cur}/{max} · {Tag(monitor)}", true);
                    return true;
                }

                // 2) Low-level VCP fallback.
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                        monitor.HPhysical, VcpBrightness, IntPtr.Zero, out uint vcur, out uint vmax))
                {
                    current = vcur;
                    max = vmax == 0 ? 100 : vmax;
                    _traffic.In("DDC/CI",
                        $"GetVCPFeature(0x10 brightness) → {vcur}/{max} · {Tag(monitor)}", true);
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                _traffic.In("DDC/CI",
                    $"GetVCPFeature(0x10 brightness) — monitor did not respond · {Tag(monitor)}", false, err);
                _log.LogDebug("Brightness read failed err={Err} ({Gdi} {Monitor})",
                    err, monitor.GdiDeviceName, monitor.Description);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Exception while reading brightness ({Monitor})", monitor.Description);
                return false;
            }
        }

        /// <summary>
        /// Sets brightness (0–100) via SetVCPFeature(0x10). Returns true on success.
        /// </summary>
        public bool SetBrightness(MonitorInfo monitor, uint value)
        {
            if (monitor.HPhysical == IntPtr.Zero)
                return false;

            try
            {
                bool ok = NativeMethods.SetVCPFeature(monitor.HPhysical, VcpBrightness, value);
                int err = ok ? 0 : Marshal.GetLastWin32Error();

                _traffic.Out("DDC/CI",
                    $"SetVCPFeature(0x10 brightness, {value}) · {Tag(monitor)}", ok, ok ? null : err);

                if (!ok)
                    _log.LogWarning("Brightness write failed err={Err} value={Value} ({Gdi} {Monitor})",
                        err, value, monitor.GdiDeviceName, monitor.Description);
                else
                    _log.LogDebug("Brightness {Value} -> {Gdi} {Monitor}",
                        value, monitor.GdiDeviceName, monitor.Description);
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Exception while writing brightness ({Monitor})", monitor.Description);
                return false;
            }
        }

        /// <summary>Short, unambiguous monitor label for the traffic log
        /// (Description alone isn't enough — "Generic PnP Monitor" is often repeated).</summary>
        private static string Tag(MonitorInfo m) => $"{m.GdiDeviceName} ({m.Description})";

        private string TryGetMonitorDeviceId(string adapterDeviceName)
        {
            try
            {
                var dd = new NativeMethods.DISPLAY_DEVICE
                {
                    cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
                };
                if (NativeMethods.EnumDisplayDevices(
                        adapterDeviceName, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    if (!string.IsNullOrWhiteSpace(dd.DeviceID))
                        return dd.DeviceID;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Exception while resolving monitor DeviceID");
            }

            // Fallback to the adapter name (\\.\DISPLAYx) — less stable, but better than nothing.
            return adapterDeviceName;
        }

        private void ReleaseHandles()
        {
            foreach (var pms in _ownedHandles)
            {
                try
                {
                    NativeMethods.DestroyPhysicalMonitors((uint)pms.Length, pms);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Exception while releasing monitor handles");
                }
            }
            _ownedHandles.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseHandles();
        }
    }
}
