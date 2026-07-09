using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using G_Lumen.Services;

namespace G_Lumen.ViewModels
{
    /// <summary>
    /// View-model for the Settings window. Currently just editing custom monitor names.
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly SettingsStore _settings;

        public SettingsViewModel(SettingsStore settings, IEnumerable<MonitorViewModel> monitors)
        {
            _settings = settings;
            foreach (var m in monitors)
                Monitors.Add(new MonitorNameEntry(m));
        }

        public ObservableCollection<MonitorNameEntry> Monitors { get; } = new();

        /// <summary>Raised to close the window (wired up in code-behind).</summary>
        public event Action? RequestClose;

        [RelayCommand]
        private void Save()
        {
            foreach (var entry in Monitors)
            {
                _settings.SetName(entry.StableId, entry.Name);
                entry.Monitor.RefreshName();
            }
            _settings.Save();
            RequestClose?.Invoke();
        }

        [RelayCommand]
        private void Cancel() => RequestClose?.Invoke();
    }

    /// <summary>One row in Settings: an editable monitor name.</summary>
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
    }
}
