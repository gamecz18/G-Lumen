using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using G_Lumen.Services;
using G_Lumen.Views;
using Microsoft.Extensions.Logging;

namespace G_Lumen.ViewModels
{
    /// <summary>
    /// Kořenový view-model aplikace. Drží servisní vrstvu, kolekci monitorů
    /// a příkazy pro tray menu (Obnovit / Nastavení / Autostart / Konec).
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        private readonly DdcCiService _ddc;
        private readonly HdrService _hdr;
        private readonly SettingsStore _settings;
        private readonly AutostartManager _autostart;
        private readonly ILogger _log;
        private bool _suppressAutostartWrite;
        private SettingsWindow? _settingsWindow;

        public MainViewModel(DdcCiService ddc, HdrService hdr, SettingsStore settings,
            AutostartManager autostart, ILogger logger, TrafficLog traffic)
        {
            _ddc = ddc;
            _hdr = hdr;
            _settings = settings;
            _autostart = autostart;
            _log = logger;
            Traffic = traffic;

            _suppressAutostartWrite = true;
            AutostartEnabled = _autostart.IsEnabled;
            _suppressAutostartWrite = false;

            Refresh();
        }

        public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

        /// <summary>Živý výpis transakcí s monitory pro diagnostický panel.</summary>
        public TrafficLog Traffic { get; }

        [ObservableProperty]
        private bool _autostartEnabled;

        /// <summary>Zobrazit v popupu panel s low-level provozem (DDC/CI, DisplayConfig).</summary>
        [ObservableProperty]
        private bool _showDiagnostics;

        [RelayCommand]
        private void Refresh()
        {
            try
            {
                _hdr.RefreshPaths();
                Monitors.Clear();
                foreach (var info in _ddc.Enumerate())
                    Monitors.Add(new MonitorViewModel(_ddc, _hdr, _settings, info));
                _log.LogInformation("Obnova: nalezeno {Count} monitor(ů)", Monitors.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Obnova monitorů selhala");
            }
        }

        [RelayCommand]
        private void OpenSettings()
        {
            try
            {
                if (_settingsWindow is { } existing)
                {
                    existing.Activate();
                    return;
                }

                _settingsWindow = new SettingsWindow
                {
                    DataContext = new SettingsViewModel(_settings, Monitors),
                };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
                _log.LogInformation("Nastavení otevřeno");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Otevření nastavení selhalo");
            }
        }

        [RelayCommand]
        private void ClearTraffic() => Traffic.Clear();

        [RelayCommand]
        private void OpenLogFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLog.LogDirectory)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Otevření složky s logy selhalo");
            }
        }

        [RelayCommand]
        private void Exit()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        partial void OnAutostartEnabledChanged(bool value)
        {
            if (_suppressAutostartWrite)
                return;

            if (value)
                _autostart.Enable();
            else
                _autostart.Disable();
        }
    }
}
