using Avalonia.Controls;
using Avalonia.Input;
using G_Lumen.ViewModels;

namespace G_Lumen.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            DataContextChanged += (_, _) =>
            {
                if (DataContext is SettingsViewModel vm)
                    vm.RequestClose += Close;
            };
        }

        // Drag state for the schedule graph (one drag at a time).
        private MonitorNameEntry? _dragEntry;
        private SchedulePointEntry? _dragPoint;

        /// <summary>
        /// Schedule graph interaction:
        ///  • left-drag a circle — move the point,
        ///  • Shift + left-click — add a point (and start dragging it),
        ///  • right-click a circle — remove the point.
        /// </summary>
        private void OnScheduleGraphPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!TryGetGraphPosition(sender, e, out var control, out var entry,
                    out double xFrac, out double yFrac))
                return;

            var props = e.GetCurrentPoint(control).Properties;

            if (props.IsRightButtonPressed)
            {
                entry.RemovePointNear(xFrac, yFrac);
                return;
            }

            if (!props.IsLeftButtonPressed)
                return;

            var point = entry.HitTestPoint(xFrac, yFrac);
            if (point is null && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                point = entry.AddPointAtFraction(xFrac, yFrac);

            if (point is not null)
            {
                _dragEntry = entry;
                _dragPoint = point;
                e.Pointer.Capture(control);
            }
        }

        private void OnScheduleGraphMoved(object? sender, PointerEventArgs e)
        {
            if (_dragEntry is null || _dragPoint is null)
                return;
            if (!TryGetGraphPosition(sender, e, out _, out var entry,
                    out double xFrac, out double yFrac)
                || !ReferenceEquals(entry, _dragEntry))
                return;

            _dragEntry.MoveSchedulePoint(_dragPoint, xFrac, yFrac);
        }

        private void OnScheduleGraphReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_dragEntry is not null)
            {
                _dragEntry.SortSchedulePoints();
                e.Pointer.Capture(null);
            }
            _dragEntry = null;
            _dragPoint = null;
        }

        private static bool TryGetGraphPosition(object? sender, PointerEventArgs e,
            out Control control, out MonitorNameEntry entry, out double xFrac, out double yFrac)
        {
            control = null!;
            entry = null!;
            xFrac = yFrac = 0;

            if (sender is not Control c
                || c.DataContext is not MonitorNameEntry en
                || c.Bounds.Width <= 0
                || c.Bounds.Height <= 0)
                return false;

            var pos = e.GetPosition(c);
            control = c;
            entry = en;
            xFrac = pos.X / c.Bounds.Width;
            yFrac = pos.Y / c.Bounds.Height;
            return true;
        }
    }
}
