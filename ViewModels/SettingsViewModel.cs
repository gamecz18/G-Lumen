using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using G_Lumen.Services;

namespace G_Lumen.ViewModels
{
    /// <summary>
    /// View-model for the Settings window: editing custom monitor names and
    /// reordering monitors (the order they appear in the popup).
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly SettingsStore _settings;
        private readonly ObservableCollection<MonitorViewModel> _mainMonitors;

        public SettingsViewModel(SettingsStore settings, ObservableCollection<MonitorViewModel> monitors)
        {
            _settings = settings;
            _mainMonitors = monitors;
            foreach (var m in monitors)
                Monitors.Add(new MonitorNameEntry(m,
                    settings.GetHdrMaxNits(m.StableId) ?? HdrService.DefaultMaxNits));
            Renumber();

            PopupScreenOptions.Add(new PopupScreenOption(null, "Automatic (screen with the system tray)"));
            foreach (var m in monitors)
                PopupScreenOptions.Add(new PopupScreenOption(m.StableId, m.Name));

            string? savedId = settings.GetPopupMonitor();
            _selectedPopupScreen = PopupScreenOptions.FirstOrDefault(o => o.StableId == savedId)
                ?? PopupScreenOptions[0];
        }

        public ObservableCollection<MonitorNameEntry> Monitors { get; } = new();

        /// <summary>Choices for where the popup appears: automatic + one per monitor.</summary>
        public ObservableCollection<PopupScreenOption> PopupScreenOptions { get; } = new();

        [ObservableProperty]
        private PopupScreenOption _selectedPopupScreen;

        /// <summary>Raised to close the window (wired up in code-behind).</summary>
        public event Action? RequestClose;

        /// <summary>Resets the HDR range field to the built-in default (480 nits).</summary>
        [RelayCommand]
        private void ResetHdrMax(MonitorNameEntry entry)
        {
            entry.HdrMaxNits = (decimal)HdrService.DefaultMaxNits;
            entry.HdrAutoInfo = null;
        }

        /// <summary>
        /// Fills the HDR range field with what the panel reports in its EDID
        /// (DXGI MaxFullFrameLuminance, falling back to peak). Only overwrites
        /// the field — the value can still be edited before saving.
        /// </summary>
        [RelayCommand]
        private void AutoHdrMax(MonitorNameEntry entry)
        {
            if (DxgiNative.TryGetMonitorLuminance(entry.Monitor.GdiDeviceName,
                    out double fullFrame, out double peak))
            {
                double nits = fullFrame >= 100 ? fullFrame : peak;
                if (nits >= 100)
                {
                    // Windows rejects SDR white level above 480 nits (error 87).
                    nits = Math.Min(nits, HdrService.ApiMaxNits);
                    entry.HdrMaxNits = Math.Round((decimal)nits);
                    entry.HdrAutoInfo = $"panel reports {fullFrame:0} nits full-frame, {peak:0} peak";
                    return;
                }
            }
            entry.HdrAutoInfo = "auto-detect failed — monitor doesn't report luminance";
        }

        [RelayCommand(CanExecute = nameof(CanMoveUp))]
        private void MoveUp(MonitorNameEntry entry) => Move(entry, -1);

        [RelayCommand(CanExecute = nameof(CanMoveDown))]
        private void MoveDown(MonitorNameEntry entry) => Move(entry, +1);

        private bool CanMoveUp(MonitorNameEntry? entry)
            => entry is not null && Monitors.IndexOf(entry) > 0;

        private bool CanMoveDown(MonitorNameEntry? entry)
            => entry is not null && Monitors.IndexOf(entry) < Monitors.Count - 1;

        private void Move(MonitorNameEntry entry, int delta)
        {
            int index = Monitors.IndexOf(entry);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= Monitors.Count)
                return;

            Monitors.Move(index, target);
            Renumber();
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
        }

        private void Renumber()
        {
            for (int i = 0; i < Monitors.Count; i++)
                Monitors[i].Position = i + 1;
        }

        [RelayCommand]
        private void Save()
        {
            foreach (var entry in Monitors)
            {
                _settings.SetName(entry.StableId, entry.Name);
                entry.Monitor.RefreshName();

                _settings.SetHdrMaxNits(entry.StableId,
                    entry.HdrMaxNits is decimal nits ? (double)nits : null);
                entry.Monitor.RefreshHdrRange();
            }

            _settings.SetOrder(Monitors.Select(e => e.StableId));
            _settings.SetPopupMonitor(SelectedPopupScreen.StableId);
            _settings.Save();

            // Reorder the live popup collection to match, without re-enumerating.
            for (int target = 0; target < Monitors.Count; target++)
            {
                int current = _mainMonitors.IndexOf(Monitors[target].Monitor);
                if (current >= 0 && current != target)
                    _mainMonitors.Move(current, target);
            }

            RequestClose?.Invoke();
        }

        [RelayCommand]
        private void Cancel() => RequestClose?.Invoke();
    }

    /// <summary>One item in the popup-screen dropdown (null StableId = automatic).</summary>
    public sealed record PopupScreenOption(string? StableId, string Label);

    /// <summary>One row in Settings: an editable monitor name + its position.</summary>
    public partial class MonitorNameEntry : ViewModelBase
    {
        public MonitorNameEntry(MonitorViewModel monitor, double hdrMaxNits)
        {
            Monitor = monitor;
            _name = monitor.Name;
            _hdrMaxNits = (decimal)hdrMaxNits;
        }

        public MonitorViewModel Monitor { get; }
        public string StableId => Monitor.StableId;
        public string Description => Monitor.Description;

        /// <summary>Show the HDR range editor only for HDR-capable monitors.</summary>
        public bool SupportsHdr => Monitor.SupportsHdr;

        [ObservableProperty]
        private string _name;

        /// <summary>Upper bound of the HDR slider in nits (100 % position).</summary>
        [ObservableProperty]
        private decimal? _hdrMaxNits;

        /// <summary>Result of the last "Auto" detection (null = nothing to show).</summary>
        [ObservableProperty]
        private string? _hdrAutoInfo;

        /// <summary>1-based position in the popup (updated while reordering).</summary>
        [ObservableProperty]
        private int _position;
    }
}
