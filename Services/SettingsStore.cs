using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace G_Lumen.Services
{
    /// <summary>
    /// Perzistence posledních zapsaných hodnot jasu per monitor do
    /// %AppData%\G-Lumen\settings.json. Protože čtení z monitoru na tomto HW
    /// nefunguje, je tohle hlavní zdroj pravdy o tom, co slider má ukazovat.
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
                    _data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
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

        /// <summary>Poslední uložený jas (0–100), nebo null pokud monitor neznáme.</summary>
        public int? GetBrightness(string stableId)
            => _data.Brightness.TryGetValue(stableId, out int v) ? v : null;

        public void SetBrightness(string stableId, int value)
            => _data.Brightness[stableId] = value;

        /// <summary>Vlastní název monitoru, nebo null pokud nebyl nastaven.</summary>
        public string? GetName(string stableId)
            => _data.Names.TryGetValue(stableId, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public void SetName(string stableId, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                _data.Names.Remove(stableId);
            else
                _data.Names[stableId] = name.Trim();
        }

        /// <summary>Je pro monitor zapnut HDR režim (slider ovládá SDR white level)?</summary>
        public bool? GetHdrMode(string stableId)
            => _data.HdrMode.TryGetValue(stableId, out bool v) ? v : null;

        public void SetHdrMode(string stableId, bool value)
            => _data.HdrMode[stableId] = value;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
        };

        private sealed class SettingsData
        {
            public Dictionary<string, int> Brightness { get; set; } = new();
            public Dictionary<string, string> Names { get; set; } = new();
            public Dictionary<string, bool> HdrMode { get; set; } = new();
        }
    }
}
