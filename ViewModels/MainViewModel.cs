using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using G_Lumen.Services;
using G_Lumen.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace G_Lumen.ViewModels
{
    /// <summary>
    /// Root view-model of the app. Holds the service layer, the monitor collection,
    /// and the tray menu commands (Refresh / Settings / Autostart / Exit).
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        private readonly DdcCiService _ddc;
        private readonly HdrService _hdr;
        private readonly WmiBrightnessService _wmi;
        private readonly SettingsStore _settings;
        private readonly AutostartManager _autostart;
        private readonly ILogger _log;
        private bool _suppressAutostartWrite;
        private bool _suppressMasterWrite;
        private SettingsWindow? _settingsWindow;

        // Scheduler: last value applied per monitor, so a manual slider tweak
        // isn't overridden until the curve actually moves to a new value.
        private readonly DispatcherTimer _scheduleTimer;
        private readonly Dictionary<string, int> _lastScheduled = new();

        // Debounce for auto apply: wake and hotplug fire several events in a burst,
        // and monitors need a few seconds before they accept DDC again.
        private readonly DispatcherTimer _reapplyTimer;

        public MainViewModel(DdcCiService ddc, HdrService hdr, WmiBrightnessService wmi,
            SettingsStore settings, AutostartManager autostart, ILogger logger, TrafficLog traffic)
        {
            _ddc = ddc;
            _hdr = hdr;
            _wmi = wmi;
            _settings = settings;
            _autostart = autostart;
            _log = logger;
            Traffic = traffic;

            _suppressAutostartWrite = true;
            AutostartEnabled = _autostart.IsEnabled;
            _suppressAutostartWrite = false;

            _showMasterSlider = settings.GetShowMasterSlider();
            _autoApplyEnabled = settings.GetAutoApply();

            Refresh();

            _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _scheduleTimer.Tick += (_, _) => ApplySchedules();
            _scheduleTimer.Start();
            ApplySchedules();

            _reapplyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _reapplyTimer.Tick += (_, _) =>
            {
                _reapplyTimer.Stop();
                _log.LogInformation("Auto apply: re-sending brightness after wake/display change");
                Refresh();
                ApplyAll();
            };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                ScheduleAutoApply();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
            => ScheduleAutoApply();

        /// <summary>(Re)starts the debounce timer on the UI thread.</summary>
        private void ScheduleAutoApply()
        {
            if (!AutoApplyEnabled)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                _reapplyTimer.Stop();
                _reapplyTimer.Start();
            });
        }

        public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

        /// <summary>
        /// GDI display name (\\.\DISPLAYx) of the monitor the popup should appear on,
        /// or null for automatic placement (screen with the system tray). Returns null
        /// when the chosen monitor is no longer connected.
        /// </summary>
        public string? PopupTargetGdiName
        {
            get
            {
                string? id = _settings.GetPopupMonitor();
                if (id is null)
                    return null;
                return Monitors.FirstOrDefault(m => m.StableId == id)?.GdiDeviceName;
            }
        }

        /// <summary>Live feed of monitor transactions for the diagnostics panel.</summary>
        public TrafficLog Traffic { get; }

        [ObservableProperty]
        private bool _autostartEnabled;

        /// <summary>Show the low-level traffic panel (DDC/CI, DisplayConfig) in the popup.</summary>
        [ObservableProperty]
        private bool _showDiagnostics;

        /// <summary>Master slider — drives all monitors at once.</summary>
        [ObservableProperty]
        private int _masterBrightness = 50;

        /// <summary>Master slider enabled in Settings (updated live on save).</summary>
        [ObservableProperty]
        private bool _showMasterSlider;

        /// <summary>What the popup actually binds to: enabled AND monitors exist.</summary>
        public bool MasterSliderVisible => ShowMasterSlider && Monitors.Count > 0;

        /// <summary>Automatically re-send brightness after wake / display changes.</summary>
        [ObservableProperty]
        private bool _autoApplyEnabled;

        partial void OnAutoApplyEnabledChanged(bool value)
        {
            _settings.SetAutoApply(value);
            _settings.Save();
            _log.LogInformation("Auto apply {State}", value ? "enabled" : "disabled");
        }

        /// <summary>Force-sends the current value of every monitor right now.</summary>
        [RelayCommand]
        private void ApplyAll()
        {
            foreach (var m in Monitors)
                m.ForceApplyCommand.Execute(null);
            _log.LogInformation("Apply all: {Count} monitor(s)", Monitors.Count);
        }

        partial void OnShowMasterSliderChanged(bool value)
            => OnPropertyChanged(nameof(MasterSliderVisible));

        partial void OnMasterBrightnessChanged(int value)
        {
            if (_suppressMasterWrite)
                return;
            foreach (var m in Monitors)
                m.Brightness = value;
        }

        /// <summary>
        /// Applies daily schedules. A new value is pushed only when the curve
        /// prescribes something different from its last application — manual
        /// adjustments in between are left alone.
        /// </summary>
        private void ApplySchedules()
        {
            try
            {
                var now = DateTime.Now.TimeOfDay;
                foreach (var m in Monitors)
                {
                    var schedule = _settings.GetSchedule(m.StableId);
                    if (schedule is not { Enabled: true })
                    {
                        _lastScheduled.Remove(m.StableId);
                        continue;
                    }

                    if (ScheduleEvaluator.Evaluate(schedule, now) is not int target)
                        continue;

                    if (_lastScheduled.TryGetValue(m.StableId, out int last) && last == target)
                        continue;

                    _lastScheduled[m.StableId] = target;
                    m.Brightness = target;
                    _log.LogDebug("Schedule: {Monitor} -> {Target}%", m.Name, target);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Applying schedules failed");
            }
        }

        [RelayCommand]
        private void Refresh()
        {
            try
            {
                _hdr.RefreshPaths();
                _wmi.RefreshInstances();
                Monitors.Clear();

                // Apply the saved display order; unknown monitors keep their
                // enumeration order at the end (OrderBy is a stable sort).
                var order = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var id in _settings.GetOrder())
                    order.TryAdd(id, order.Count);

                var infos = _ddc.Enumerate()
                    .OrderBy(i => order.TryGetValue(i.StableId, out int idx) ? idx : int.MaxValue);

                foreach (var info in infos)
                    Monitors.Add(new MonitorViewModel(_ddc, _hdr, _wmi, _settings, info));

                // Master slider starts at the average without pushing writes.
                _suppressMasterWrite = true;
                MasterBrightness = Monitors.Count > 0
                    ? (int)Math.Round(Monitors.Average(m => m.Brightness))
                    : 50;
                _suppressMasterWrite = false;
                OnPropertyChanged(nameof(MasterSliderVisible));

                _log.LogInformation("Refresh: found {Count} monitor(s)", Monitors.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Monitor refresh failed");
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
                    DataContext = new SettingsViewModel(_settings, this),
                };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
                _log.LogInformation("Settings opened");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Opening settings failed");
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
                _log.LogError(ex, "Opening the log folder failed");
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
