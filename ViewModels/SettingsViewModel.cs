using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
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
        private readonly MainViewModel _main;
        private readonly ObservableCollection<MonitorViewModel> _mainMonitors;

        public SettingsViewModel(SettingsStore settings, MainViewModel main)
        {
            _settings = settings;
            _main = main;
            _mainMonitors = main.Monitors;
            var monitors = _mainMonitors;
            _showMasterSlider = settings.GetShowMasterSlider();
            foreach (var m in monitors)
                Monitors.Add(new MonitorNameEntry(m,
                    settings.GetHdrMaxNits(m.StableId) ?? HdrService.DefaultMaxNits,
                    settings.GetSchedule(m.StableId)));
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

        /// <summary>Show the master slider (all monitors at once) in the popup.</summary>
        [ObservableProperty]
        private bool _showMasterSlider;

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

                _settings.SetSchedule(entry.StableId, entry.ToScheduleData());
            }

            _settings.SetOrder(Monitors.Select(e => e.StableId));
            _settings.SetPopupMonitor(SelectedPopupScreen.StableId);
            _settings.SetShowMasterSlider(ShowMasterSlider);
            _settings.Save();

            _main.ShowMasterSlider = ShowMasterSlider;

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
        public MonitorNameEntry(MonitorViewModel monitor, double hdrMaxNits, ScheduleData? schedule)
        {
            Monitor = monitor;
            _name = monitor.Name;
            _hdrMaxNits = (decimal)hdrMaxNits;

            _scheduleEnabled = schedule?.Enabled ?? false;
            _scheduleMode = schedule?.Mode ?? ScheduleMode.Linear;
            foreach (var p in schedule?.Points ?? new List<SchedulePoint>())
                SchedulePoints.Add(Wire(new SchedulePointEntry(p.Time, p.Percent)));
            UpdateSchedulePreview();
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

        // ---- Daily brightness schedule -------------------------------------

        /// <summary>Available interpolation modes (for the ComboBox).</summary>
        public static ScheduleMode[] ScheduleModes { get; } =
            { ScheduleMode.Linear, ScheduleMode.Steps, ScheduleMode.Smooth };

        public ObservableCollection<SchedulePointEntry> SchedulePoints { get; } = new();

        [ObservableProperty]
        private bool _scheduleEnabled;

        [ObservableProperty]
        private ScheduleMode _scheduleMode;

        /// <summary>Polyline points (240×60 box) previewing the daily curve.</summary>
        [ObservableProperty]
        private List<Point> _schedulePreview = new();

        /// <summary>Draggable circles for each valid point (positions in the 240×60 box).</summary>
        [ObservableProperty]
        private List<ScheduleMarker> _scheduleMarkers = new();

        partial void OnScheduleModeChanged(ScheduleMode value) => UpdateSchedulePreview();

        [RelayCommand]
        private void AddSchedulePoint()
        {
            InsertSorted(Wire(new SchedulePointEntry("12:00", 50)));
            UpdateSchedulePreview();
        }

        /// <summary>
        /// Adds a point from a click in the graph. Fractions are 0–1 relative to
        /// the graph area: x = time of day, y = top-down brightness. Time snaps
        /// to 5 minutes. Returns the new point so a drag can start right away.
        /// </summary>
        public SchedulePointEntry AddPointAtFraction(double xFraction, double yFraction)
        {
            var point = Wire(new SchedulePointEntry(
                FractionToTime(xFraction), FractionToPercent(yFraction)));
            InsertSorted(point);
            ScheduleEnabled = true;
            UpdateSchedulePreview();
            return point;
        }

        /// <summary>Finds the point whose graph circle is near the given position.</summary>
        public SchedulePointEntry? HitTestPoint(double xFraction, double yFraction)
        {
            double cx = xFraction * 240.0;
            double cy = yFraction * 60.0;

            SchedulePointEntry? best = null;
            double bestDistSq = 10 * 10; // grab radius in canvas units
            foreach (var p in SchedulePoints)
            {
                if (!ScheduleEvaluator.TryParseTime(p.Time, out var t))
                    continue;
                double x = t.TotalMinutes / 1440.0 * 240.0;
                double y = 58.0 - (double)Math.Clamp(p.Percent ?? 50, 0, 100) * 0.56;
                double distSq = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Moves a point while dragging its circle (preview updates via wiring).</summary>
        public void MoveSchedulePoint(SchedulePointEntry point, double xFraction, double yFraction)
        {
            point.Time = FractionToTime(xFraction);
            point.Percent = FractionToPercent(yFraction);
        }

        /// <summary>Right-click in the graph: removes the circle under the cursor.</summary>
        public void RemovePointNear(double xFraction, double yFraction)
        {
            if (HitTestPoint(xFraction, yFraction) is { } point)
            {
                SchedulePoints.Remove(point);
                UpdateSchedulePreview();
            }
        }

        /// <summary>Restores time order in the visible list (after a drag ends).</summary>
        public void SortSchedulePoints()
        {
            var sorted = SchedulePoints
                .OrderBy(p => ScheduleEvaluator.TryParseTime(p.Time, out var t)
                    ? t.TotalMinutes
                    : double.MaxValue)
                .ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = SchedulePoints.IndexOf(sorted[i]);
                if (current != i)
                    SchedulePoints.Move(current, i);
            }
        }

        private static string FractionToTime(double xFraction)
        {
            int minutes = (int)Math.Round(Math.Clamp(xFraction, 0, 1) * 1440 / 5.0) * 5;
            if (minutes >= 1440)
                minutes = 1435;
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }

        private static int FractionToPercent(double yFraction)
            => (int)Math.Round(Math.Clamp(1 - yFraction, 0, 1) * 100);

        /// <summary>Keeps the visible point list ordered by time of day.</summary>
        private void InsertSorted(SchedulePointEntry point)
        {
            double minutes = ScheduleEvaluator.TryParseTime(point.Time, out var t)
                ? t.TotalMinutes
                : double.MaxValue;

            int index = 0;
            while (index < SchedulePoints.Count
                   && ScheduleEvaluator.TryParseTime(SchedulePoints[index].Time, out var other)
                   && other.TotalMinutes <= minutes)
                index++;
            SchedulePoints.Insert(index, point);
        }

        [RelayCommand]
        private void RemoveSchedulePoint(SchedulePointEntry point)
        {
            SchedulePoints.Remove(point);
            UpdateSchedulePreview();
        }

        /// <summary>Snapshot of the schedule editor for persistence (empty times are dropped).</summary>
        public ScheduleData ToScheduleData() => new()
        {
            Enabled = ScheduleEnabled,
            Mode = ScheduleMode,
            Points = SchedulePoints
                .Where(p => !string.IsNullOrWhiteSpace(p.Time))
                .Select(p => new SchedulePoint
                {
                    Time = p.Time,
                    Percent = (int)Math.Clamp(p.Percent ?? 50, 0, 100),
                })
                .ToList(),
        };

        private SchedulePointEntry Wire(SchedulePointEntry point)
        {
            point.PropertyChanged += (_, _) => UpdateSchedulePreview();
            return point;
        }

        private void UpdateSchedulePreview()
        {
            var data = ToScheduleData();
            var pts = new List<Point>();
            for (int minute = 0; minute <= 1440; minute += 10)
            {
                int value = ScheduleEvaluator.Evaluate(
                    data, TimeSpan.FromMinutes(Math.Min(minute, 1439))) ?? 50;
                pts.Add(new Point(
                    minute / 1440.0 * 240.0,
                    58.0 - value * 0.56));
            }
            SchedulePreview = pts;

            var markers = new List<ScheduleMarker>();
            foreach (var p in SchedulePoints)
            {
                if (!ScheduleEvaluator.TryParseTime(p.Time, out var t))
                    continue;
                double x = t.TotalMinutes / 1440.0 * 240.0;
                double y = 58.0 - (double)Math.Clamp(p.Percent ?? 50, 0, 100) * 0.56;
                // 9×9 circle centred on the point.
                markers.Add(new ScheduleMarker(new Thickness(x - 4.5, y - 4.5, 0, 0)));
            }
            ScheduleMarkers = markers;
        }
    }

    /// <summary>Position of one point's circle in the 240×60 graph box.</summary>
    public sealed record ScheduleMarker(Thickness Margin);

    /// <summary>One editable point of the daily schedule (time + brightness).</summary>
    public partial class SchedulePointEntry : ViewModelBase
    {
        private bool _normalizing;

        public SchedulePointEntry(string time, int percent)
        {
            _time = time;
            _percent = percent;
        }

        /// <summary>Time of day as "HH:mm".</summary>
        [ObservableProperty]
        private string _time;

        [ObservableProperty]
        private decimal? _percent;

        /// <summary>
        /// Auto-completes shorthand input on focus loss: "15" → 15:00,
        /// "1525" → 15:25, "930" → 09:30, "15:2" → 15:02. Anything that
        /// isn't a valid time of day (e.g. "1689") clears the field.
        /// </summary>
        partial void OnTimeChanged(string value)
        {
            if (_normalizing)
                return;

            string normalized = NormalizeTime(value);
            if (normalized == value)
                return;

            _normalizing = true;
            Time = normalized;
            _normalizing = false;
        }

        private static string NormalizeTime(string? input)
        {
            string s = (input ?? "").Trim().Replace(".", ":").Replace(",", ":");
            if (s.Length == 0)
                return "";

            int hours, minutes;
            if (s.Contains(':'))
            {
                var parts = s.Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out hours)
                    || !int.TryParse(parts[1], out minutes))
                    return "";
            }
            else
            {
                if (s.Length > 4 || !s.All(char.IsDigit))
                    return "";
                // 1–2 digits = whole hour; 3–4 digits = hour + minutes.
                hours = int.Parse(s.Length <= 2 ? s : s[..^2]);
                minutes = s.Length <= 2 ? 0 : int.Parse(s[^2..]);
            }

            return hours is >= 0 and <= 23 && minutes is >= 0 and <= 59
                ? $"{hours:00}:{minutes:00}"
                : "";
        }
    }
}
