using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using G_Lumen.Services;

namespace G_Lumen.ViewModels
{
    /// <summary>
    /// One monitor in the popup. The slider drives <see cref="Brightness"/> (0–100).
    /// Writes are throttled (DDC write ~50 ms, dragging the slider would otherwise
    /// flood the I2C bus).
    ///
    /// Two control modes:
    ///  • SDR (default): brightness via DDC/CI VCP 0x10.
    ///  • HDR (<see cref="HdrMode"/>): brightness via SDR white level (DisplayConfig),
    ///    since DDC doesn't control real brightness on an HDR monitor.
    /// </summary>
    public partial class MonitorViewModel : ViewModelBase
    {
        private readonly DdcCiService _ddc;
        private readonly HdrService _hdr;
        private readonly SettingsStore _settings;
        private readonly MonitorInfo _monitor;
        private readonly DispatcherTimer _throttle;

        // True during initialization / mode switching, so setting the value
        // doesn't trigger a write to the monitor.
        private bool _suppressWrite;
        private bool _pending;
        private int _pendingValue;

        public MonitorViewModel(DdcCiService ddc, HdrService hdr, SettingsStore settings, MonitorInfo monitor)
        {
            _ddc = ddc;
            _hdr = hdr;
            _settings = settings;
            _monitor = monitor;

            _name = settings.GetName(monitor.StableId) ?? monitor.Description;
            SupportsHdr = _hdr.IsHdrAvailable(monitor.GdiDeviceName);

            bool hdrActiveNow = SupportsHdr && _hdr.IsHdrActive(monitor.GdiDeviceName);
            HdrStatusText = SupportsHdr
                ? hdrActiveNow ? "supported · currently active" : "supported · off"
                : "not supported";

            // Default mode: saved choice, otherwise based on whether HDR is currently active.
            _hdrMode = settings.GetHdrMode(monitor.StableId) ?? hdrActiveNow;

            _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _throttle.Tick += OnThrottleTick;

            _suppressWrite = true;
            Brightness = ResolveInitialBrightness();
            _suppressWrite = false;
        }

        /// <summary>Displayed name (custom from settings, otherwise the monitor description).</summary>
        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private int _brightness;

        /// <summary>Slider controls the SDR white level instead of DDC (for HDR monitors).</summary>
        [ObservableProperty]
        private bool _hdrMode;

        /// <summary>Expanded low-level monitor detail (ID, handle, API paths).</summary>
        [ObservableProperty]
        private bool _isExpanded;

        /// <summary>How reading the value from the monitor went at startup.</summary>
        [ObservableProperty]
        private string _readInfo = "—";

        /// <summary>Monitor supports HDR — only then does showing the toggle make sense.</summary>
        public bool SupportsHdr { get; }

        /// <summary>HDR state at startup ("supported · active" etc.).</summary>
        public string HdrStatusText { get; }

        /// <summary>Stable ID for persistence (key into settings).</summary>
        public string StableId => _monitor.StableId;

        /// <summary>Original monitor description (placeholder when renaming).</summary>
        public string Description => _monitor.Description;

        /// <summary>GDI display name (\\.\DISPLAYx).</summary>
        public string GdiDeviceName => _monitor.GdiDeviceName;

        /// <summary>Physical monitor handle (hex, for diagnostics).</summary>
        public string HandleHex => $"0x{_monitor.HPhysical.ToInt64():X}";

        /// <summary>Short description of the active path, shown under the monitor name.</summary>
        public string Subtitle => HdrMode
            ? $"DisplayConfig · SDR white ≈ {HdrService.PercentToNits(Brightness):0} nits"
            : "DDC/CI · VCP 0x10 (brightness)";

        /// <summary>Which API is used to write brightness.</summary>
        public string WriteInfo => HdrMode
            ? "DisplayConfigSetDeviceInfo · SDR white level"
            : "SetVCPFeature · VCP 0x10";

        /// <summary>Refreshes the displayed name from settings (called by the Settings window after saving).</summary>
        public void RefreshName()
            => Name = _settings.GetName(_monitor.StableId) ?? _monitor.Description;

        private int ResolveInitialBrightness()
        {
            if (HdrMode)
            {
                if (_hdr.TryGetSdrNits(_monitor.GdiDeviceName, out double nits))
                {
                    ReadInfo = "DisplayConfig · read works";
                    return HdrService.NitsToPercent(nits);
                }
                ReadInfo = "DisplayConfig · read failed → saved value";
                return _settings.GetBrightness(_monitor.StableId) ?? 50;
            }

            if (_ddc.TryGetBrightness(_monitor, out uint cur, out uint max) && max > 0)
            {
                ReadInfo = "DDC/CI · read works";
                return (int)Math.Round(cur * 100.0 / max);
            }

            ReadInfo = "DDC/CI · read fails → saved value";
            return _settings.GetBrightness(_monitor.StableId) ?? 50;
        }

        partial void OnHdrModeChanged(bool value)
        {
            _settings.SetHdrMode(_monitor.StableId, value);
            _settings.Save();

            // Switched source → show the current value of the new source without writing.
            _suppressWrite = true;
            Brightness = ResolveInitialBrightness();
            _suppressWrite = false;

            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(WriteInfo));
        }

        partial void OnBrightnessChanged(int value)
        {
            OnPropertyChanged(nameof(Subtitle));

            if (_suppressWrite)
                return;

            _pendingValue = Math.Clamp(value, 0, 100);
            _pending = true;
            if (!_throttle.IsEnabled)
                _throttle.Start();
        }

        private void OnThrottleTick(object? sender, EventArgs e)
        {
            _throttle.Stop();
            if (!_pending)
                return;

            _pending = false;
            int value = _pendingValue;

            if (HdrMode)
                _hdr.SetSdrNits(_monitor.GdiDeviceName, HdrService.PercentToNits(value));
            else
                _ddc.SetBrightness(_monitor, (uint)value);

            _settings.SetBrightness(_monitor.StableId, value);
            _settings.Save();
        }
    }
}
