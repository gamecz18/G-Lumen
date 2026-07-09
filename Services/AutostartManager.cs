using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace G_Lumen.Services
{
    /// <summary>
    /// Launch on Windows sign-in via the HKCU Run key (no admin rights needed).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class AutostartManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "G-Lumen";

        /// <summary>Is the executable path registered for autostart?</summary>
        public bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                    return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Autostart] IsEnabled failed: {ex.Message}");
                    return false;
                }
            }
        }

        public void Enable()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                    return;

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                key?.SetValue(ValueName, $"\"{exe}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Autostart] Enable failed: {ex.Message}");
            }
        }

        public void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Autostart] Disable failed: {ex.Message}");
            }
        }
    }
}
