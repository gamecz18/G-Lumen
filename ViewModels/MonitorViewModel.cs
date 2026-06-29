using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using G_Lumen.Services;

namespace G_Lumen.ViewModels
{
    /// <summary>
    /// Jeden monitor v popupu. Slider mění <see cref="Brightness"/> (0–100).
    /// Zápis je throttlovaný (DDC write ~50 ms, drag slideru by jinak zahltil I2C).
    ///
    /// Dva režimy ovládání:
    ///  • SDR (výchozí): jas přes DDC/CI VCP 0x10.
    ///  • HDR (<see cref="HdrMode"/>): jas přes SDR white level (DisplayConfig),
    ///    protože DDC na HDR monitoru reálný jas neovládá.
    /// </summary>
    public partial class MonitorViewModel : ViewModelBase
    {
        private readonly DdcCiService _ddc;
        private readonly HdrService _hdr;
        private readonly SettingsStore _settings;
        private readonly MonitorInfo _monitor;
        private readonly DispatcherTimer _throttle;

        // True během inicializace / přepínání režimu, aby nastavení hodnoty
        // nespustilo zápis na monitor.
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

            // Výchozí režim: uložená volba, jinak podle toho, jestli je HDR právě aktivní.
            _hdrMode = settings.GetHdrMode(monitor.StableId)
                       ?? _hdr.IsHdrActive(monitor.GdiDeviceName);

            _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _throttle.Tick += OnThrottleTick;

            _suppressWrite = true;
            Brightness = ResolveInitialBrightness();
            _suppressWrite = false;
        }

        /// <summary>Zobrazený název (vlastní z nastavení, jinak popis monitoru).</summary>
        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private int _brightness;

        /// <summary>Slider ovládá SDR white level místo DDC (pro HDR monitory).</summary>
        [ObservableProperty]
        private bool _hdrMode;

        /// <summary>Monitor podporuje HDR — jen tehdy má smysl přepínač zobrazovat.</summary>
        public bool SupportsHdr { get; }

        /// <summary>Stabilní ID pro persistenci (klíč do settings).</summary>
        public string StableId => _monitor.StableId;

        /// <summary>Původní popis monitoru (placeholder při přejmenování).</summary>
        public string Description => _monitor.Description;

        /// <summary>Aktualizuje zobrazený název z nastavení (volá Settings okno po uložení).</summary>
        public void RefreshName()
            => Name = _settings.GetName(_monitor.StableId) ?? _monitor.Description;

        private int ResolveInitialBrightness()
        {
            if (HdrMode)
            {
                if (_hdr.TryGetSdrNits(_monitor.GdiDeviceName, out double nits))
                    return HdrService.NitsToPercent(nits);
                return _settings.GetBrightness(_monitor.StableId) ?? 50;
            }

            if (_ddc.TryGetBrightness(_monitor, out uint cur, out uint max) && max > 0)
                return (int)Math.Round(cur * 100.0 / max);

            return _settings.GetBrightness(_monitor.StableId) ?? 50;
        }

        partial void OnHdrModeChanged(bool value)
        {
            _settings.SetHdrMode(_monitor.StableId, value);
            _settings.Save();

            // Přepnutí zdroje → ukaž aktuální hodnotu nového zdroje bez zápisu.
            _suppressWrite = true;
            Brightness = ResolveInitialBrightness();
            _suppressWrite = false;
        }

        partial void OnBrightnessChanged(int value)
        {
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
