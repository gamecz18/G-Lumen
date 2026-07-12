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
                Monitors.Add(new MonitorNameEntry(m));
            Renumber();
        }

        public ObservableCollection<MonitorNameEntry> Monitors { get; } = new();

        /// <summary>Raised to close the window (wired up in code-behind).</summary>
        public event Action? RequestClose;

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
            }

            _settings.SetOrder(Monitors.Select(e => e.StableId));
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

    /// <summary>One row in Settings: an editable monitor name + its position.</summary>
    public partial class MonitorNameEntry : ViewModelBase
    {
        public MonitorNameEntry(MonitorViewModel monitor)
        {
            Monitor = monitor;
            _name = monitor.Name;
        }

        public MonitorViewModel Monitor { get; }
        public string StableId => Monitor.StableId;
        public string Description => Monitor.Description;

        [ObservableProperty]
        private string _name;

        /// <summary>1-based position in the popup (updated while reordering).</summary>
        [ObservableProperty]
        private int _position;
    }
}
