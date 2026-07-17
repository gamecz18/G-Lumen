using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace G_Lumen.Services
{
    /// <summary>
    /// Persists the last-written brightness values per monitor to
    /// %AppData%\G-Lumen\settings.json. Since reading from the monitor doesn't
    /// work on this hardware, this is the main source of truth for what the
    /// slider should show.
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly string _path;
        private SettingsData _data = new();

        public SettingsStore()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "G-Lumen");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "settings.json");
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path);
                    _data = JsonSerializer.Deserialize<SettingsData>(json, SerializerOptions) ?? new SettingsData();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Load failed: {ex.Message}");
                _data = new SettingsData();
            }
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(_data, SerializerOptions);
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            }
        }

        /// <summary>Last saved brightness (0–100), or null if the monitor is unknown.</summary>
        public int? GetBrightness(string stableId)
            => _data.Brightness.TryGetValue(stableId, out int v) ? v : null;

        public void SetBrightness(string stableId, int value)
            => _data.Brightness[stableId] = value;

        /// <summary>Custom monitor name, or null if none was set.</summary>
        public string? GetName(string stableId)
            => _data.Names.TryGetValue(stableId, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public void SetName(string stableId, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                _data.Names.Remove(stableId);
            else
                _data.Names[stableId] = name.Trim();
        }

        /// <summary>Is HDR mode enabled for the monitor (slider controls SDR white level)?</summary>
        public bool? GetHdrMode(string stableId)
            => _data.HdrMode.TryGetValue(stableId, out bool v) ? v : null;

        public void SetHdrMode(string stableId, bool value)
            => _data.HdrMode[stableId] = value;

        /// <summary>
        /// Upper bound of the HDR slider in nits (SDR white level at 100 %),
        /// or null to use the default (<see cref="HdrService.DefaultMaxNits"/>).
        /// Clamped to the Windows API ceiling (480 nits) — higher values fail
        /// with ERROR_INVALID_PARAMETER, so they are never returned even if an
        /// older settings.json contains them.
        /// </summary>
        public double? GetHdrMaxNits(string stableId)
            => _data.HdrMaxNits.TryGetValue(stableId, out double v)
                ? Math.Clamp(v, 100, HdrService.ApiMaxNits)
                : null;

        public void SetHdrMaxNits(string stableId, double? value)
        {
            if (value is not double v || v <= HdrService.MinNits)
                _data.HdrMaxNits.Remove(stableId);
            else
                _data.HdrMaxNits[stableId] = Math.Clamp(v, 100, HdrService.ApiMaxNits);
        }

        /// <summary>
        /// StableId of the monitor the popup should appear on,
        /// or null for automatic (screen with the system tray).
        /// </summary>
        public string? GetPopupMonitor()
            => string.IsNullOrWhiteSpace(_data.PopupMonitor) ? null : _data.PopupMonitor;

        public void SetPopupMonitor(string? stableId)
            => _data.PopupMonitor = stableId;

        /// <summary>Show the master slider (all monitors at once) in the popup. Default on.</summary>
        public bool GetShowMasterSlider() => _data.ShowMasterSlider ?? true;

        public void SetShowMasterSlider(bool value) => _data.ShowMasterSlider = value;

        /// <summary>Re-apply saved brightness automatically after wake / display changes. Default on.</summary>
        public bool GetAutoApply() => _data.AutoApply ?? true;

        public void SetAutoApply(bool value) => _data.AutoApply = value;

        /// <summary>Daily brightness schedule for the monitor, or null if none was set.</summary>
        public ScheduleData? GetSchedule(string stableId)
            => _data.Schedules.TryGetValue(stableId, out var s) ? s : null;

        public void SetSchedule(string stableId, ScheduleData? schedule)
        {
            if (schedule is null || schedule.Points.Count == 0)
                _data.Schedules.Remove(stableId);
            else
                _data.Schedules[stableId] = schedule;
        }

        /// <summary>
        /// Saved display order of monitors (StableIds, first = top of the popup).
        /// Monitors not in the list keep their enumeration order at the end.
        /// </summary>
        public IReadOnlyList<string> GetOrder() => _data.Order;

        public void SetOrder(IEnumerable<string> stableIds)
            => _data.Order = new List<string>(stableIds);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private sealed class SettingsData
        {
            public Dictionary<string, int> Brightness { get; set; } = new();
            public Dictionary<string, string> Names { get; set; } = new();
            public Dictionary<string, bool> HdrMode { get; set; } = new();
            public Dictionary<string, double> HdrMaxNits { get; set; } = new();
            public Dictionary<string, ScheduleData> Schedules { get; set; } = new();
            public List<string> Order { get; set; } = new();
            public string? PopupMonitor { get; set; }
            public bool? ShowMasterSlider { get; set; }
            public bool? AutoApply { get; set; }
        }
    }
}
