using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace G_Lumen.Services
{
    /// <summary>
    /// DDC/CI servisní vrstva (Windows-only, dxva2.dll).
    /// Enumeruje fyzické monitory a posílá VCP příkazy.
    ///
    /// Realita tohoto HW: ZÁPIS (SetVCPFeature) funguje, ČTENÍ
    /// (GetVCPFeatureAndVCPFeatureReply) selhává s error 31 (ERROR_GEN_FAILURE),
    /// protože levný aktivní adaptér / AMD ovladač nezvládá I2C read-back.
    /// Čtení proto bereme jako "best effort" a aplikace funguje i bez něj.
    /// </summary>
    public sealed class DdcCiService : IDisposable
    {
        private const byte VcpBrightness = 0x10;

        private readonly ILogger _log;

        // Handle fyzických monitorů držíme naživu po celou dobu běhu;
        // uvolníme je hromadně v Dispose přes DestroyPhysicalMonitors.
        private readonly List<NativeMethods.PHYSICAL_MONITOR[]> _ownedHandles = new();
        private bool _disposed;

        public DdcCiService(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>
        /// Projde všechny logické monitory, získá k nim fyzické handly
        /// a vrátí seznam <see cref="MonitorInfo"/>. Předchozí handly uvolní.
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
                    continue;

                _ownedHandles.Add(pms);

                // DeviceID monitoru pod tímto adaptérem (\\.\DISPLAYx) — pro stabilní klíč.
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
                }
            }

            return result;
        }

        /// <summary>
        /// Pokusí se přečíst aktuální jas (0–100). Na tomto HW typicky vrátí false
        /// (error 31) — volající pak použije lokálně uloženou hodnotu.
        /// </summary>
        public bool TryGetBrightness(MonitorInfo monitor, out uint current, out uint max)
        {
            current = 0;
            max = 100;
            if (monitor.HPhysical == IntPtr.Zero)
                return false;

            try
            {
                // 1) High-level API (to, co Monitorian zkouší první).
                if (NativeMethods.GetMonitorBrightness(monitor.HPhysical, out uint _, out uint cur, out uint mx))
                {
                    current = cur;
                    max = mx == 0 ? 100 : mx;
                    return true;
                }

                // 2) Low-level VCP fallback.
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                        monitor.HPhysical, VcpBrightness, IntPtr.Zero, out uint vcur, out uint vmax))
                {
                    current = vcur;
                    max = vmax == 0 ? 100 : vmax;
                    return true;
                }

                _log.LogDebug("Brightness read failed err={Err} ({Monitor})",
                    Marshal.GetLastWin32Error(), monitor.Description);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TryGetBrightness exception ({Monitor})", monitor.Description);
                return false;
            }
        }

        /// <summary>
        /// Nastaví jas (0–100) přes SetVCPFeature(0x10). Vrací true při úspěchu.
        /// </summary>
        public bool SetBrightness(MonitorInfo monitor, uint value)
        {
            if (monitor.HPhysical == IntPtr.Zero)
                return false;

            try
            {
                bool ok = NativeMethods.SetVCPFeature(monitor.HPhysical, VcpBrightness, value);
                if (!ok)
                    _log.LogWarning("Brightness write failed err={Err} ({Monitor})",
                        Marshal.GetLastWin32Error(), monitor.Description);
                else
                    _log.LogDebug("Brightness {Value} -> {Monitor}", value, monitor.Description);
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SetBrightness exception ({Monitor})", monitor.Description);
                return false;
            }
        }

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
                _log.LogDebug(ex, "TryGetMonitorDeviceId exception");
            }

            // Fallback na název adaptéru (\\.\DISPLAYx) — méně stabilní, ale lepší než nic.
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
                    _log.LogDebug(ex, "DestroyPhysicalMonitors exception");
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
