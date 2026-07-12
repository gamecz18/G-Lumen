using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Extensions.Logging;

namespace G_Lumen.Services
{
    /// <summary>
    /// Brightness for internal (laptop) panels via WMI (root\wmi):
    /// WmiMonitorBrightness for reading, WmiMonitorBrightnessMethods.WmiSetBrightness
    /// for writing. Internal panels don't speak DDC/CI — this is the documented
    /// Windows path (the same one the OS brightness slider uses), so unlike DDC
    /// both reading and writing work.
    /// </summary>
    public sealed class WmiBrightnessService
    {
        private const string WmiScope = @"\\.\root\wmi";

        private readonly ILogger _log;
        private readonly TrafficLog _traffic;

        // Instance list is cached — on desktops without an internal panel the query
        // throws "Not supported" and repeating it per monitor just spams the log.
        private IReadOnlyList<string>? _instances;

        public WmiBrightnessService(ILogger logger, TrafficLog traffic)
        {
            _log = logger;
            _traffic = traffic;
        }

        /// <summary>Drops the cached instance list (called on monitor refresh).</summary>
        public void RefreshInstances() => _instances = null;

        /// <summary>
        /// Finds the WMI instance name belonging to the given monitor DeviceID
        /// (device interface path from EnumDisplayDevices). Returns null when
        /// no instance matches — typically for external DDC monitors.
        /// </summary>
        public string? FindInstanceFor(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            string wanted = NormalizeId(deviceId);
            foreach (var instance in EnumerateInstances())
            {
                if (NormalizeId(instance) == wanted)
                {
                    _traffic.In("WMI", $"WmiMonitorBrightness instance matched: {instance}", true);
                    return instance;
                }
            }
            return null;
        }

        /// <summary>
        /// The only WMI brightness instance, if exactly one exists. Fallback for
        /// internal panels whose DeviceID doesn't match the WMI InstanceName format.
        /// </summary>
        public string? SingleInstanceOrNull()
        {
            var all = EnumerateInstances();
            return all.Count == 1 ? all[0] : null;
        }

        /// <summary>Reads the current brightness (0–100) of the panel.</summary>
        public bool TryGetBrightness(string instanceName, out uint current)
        {
            current = 0;
            try
            {
                using var searcher = new ManagementObjectSearcher(WmiScope,
                    "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (!string.Equals((string?)mo["InstanceName"], instanceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        current = (byte)mo["CurrentBrightness"];
                        _traffic.In("WMI", $"WmiMonitorBrightness → {current} % · {instanceName}", true);
                        return true;
                    }
                }

                _traffic.In("WMI", $"WmiMonitorBrightness — instance not found · {instanceName}", false);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WMI brightness read failed ({Instance})", instanceName);
                _traffic.In("WMI", $"WmiMonitorBrightness read failed · {instanceName}", false);
                return false;
            }
        }

        /// <summary>Sets brightness (0–100) via WmiSetBrightness. Returns true on success.</summary>
        public bool SetBrightness(string instanceName, uint value)
        {
            try
            {
                value = Math.Min(value, 100);
                using var searcher = new ManagementObjectSearcher(WmiScope,
                    "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (!string.Equals((string?)mo["InstanceName"], instanceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Args: Timeout (s), Brightness (0–100).
                        mo.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)value });
                        _traffic.Out("WMI", $"WmiSetBrightness({value}) · {instanceName}", true);
                        _log.LogDebug("WMI brightness {Value} -> {Instance}", value, instanceName);
                        return true;
                    }
                }

                _traffic.Out("WMI", $"WmiSetBrightness({value}) — instance not found · {instanceName}", false);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WMI brightness write failed value={Value} ({Instance})", value, instanceName);
                _traffic.Out("WMI", $"WmiSetBrightness({value}) failed · {instanceName}", false);
                return false;
            }
        }

        private IReadOnlyList<string> EnumerateInstances()
        {
            if (_instances is not null)
                return _instances;

            var result = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(WmiScope,
                    "SELECT InstanceName FROM WmiMonitorBrightness WHERE Active=TRUE");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["InstanceName"] is string name && !string.IsNullOrWhiteSpace(name))
                            result.Add(name);
                    }
                }
            }
            catch (ManagementException ex)
            {
                // "Not supported" is normal on desktops without an internal panel.
                _log.LogDebug(ex, "WmiMonitorBrightness not available (no internal panel?)");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WMI brightness instance enumeration failed");
            }
            _instances = result;
            return result;
        }

        /// <summary>
        /// Normalizes both ID formats onto a common shape for comparison:
        ///  EnumDisplayDevices: \\?\DISPLAY#SDC4172#4&amp;1a2b3c&amp;0&amp;UID265#{guid}
        ///  WMI InstanceName:   DISPLAY\SDC4172\4&amp;1a2b3c&amp;0&amp;UID265_0
        /// </summary>
        private static string NormalizeId(string id)
        {
            string s = id.Trim();
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
                s = s.Substring(4);
            s = s.Replace('#', '\\');

            int brace = s.IndexOf('{');
            if (brace >= 0)
                s = s.Substring(0, brace);
            s = s.TrimEnd('\\');

            // WMI instance names end with "_<n>".
            int us = s.LastIndexOf('_');
            if (us > 0 && us >= s.Length - 3)
                s = s.Substring(0, us);

            return s.ToUpperInvariant();
        }
    }
}
