using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace G_Lumen.Views
{
    /// <summary>
    /// Borderless popup with brightness sliders. Opens from the tray icon, closes
    /// (hides, doesn't destroy) on focus loss, so it can be shown again quickly.
    /// </summary>
    public partial class MonitorPopup : Window
    {
        public MonitorPopup()
        {
            InitializeComponent();
            WindowStartupLocation = WindowStartupLocation.Manual;
            Deactivated += (_, _) => Hide();

            // SizeToContent: expanding diagnostics / monitor detail changes the height
            // → the window has to re-snap to the tray corner.
            SizeChanged += (_, _) =>
            {
                if (IsVisible)
                    PositionBottomRight();
            };
        }

        /// <summary>Shows the popup at the bottom-right, near the system tray.</summary>
        public void ShowAtTray()
        {
            if (!IsVisible)
                Show();

            Activate();
            PositionBottomRight();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            // The real height (SizeToContent) is only known after the first layout pass → recompute position.
            PositionBottomRight();
        }

        /// <summary>
        /// Dragging the handle above the diagnostics log: resizes the log.
        /// The window is anchored at the bottom, so a growing log visually pushes the popup upward.
        /// </summary>
        private void OnLogResizeDragDelta(object? sender, VectorEventArgs e)
        {
            double current = double.IsNaN(LogScroll.Height)
                ? LogScroll.Bounds.Height
                : LogScroll.Height;

            LogScroll.Height = Math.Clamp(current - e.Vector.Y, 90, 600);
        }

        private void PositionBottomRight()
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
                return;

            var area = screen.WorkingArea; // in pixels, excludes the taskbar
            double scale = screen.Scaling;

            // The popup must not outgrow the work area (otherwise content would be clipped).
            MaxHeight = Math.Max(300, area.Height / scale - 16);

            int width = (int)Math.Round(Math.Max(ClientSize.Width, Width) * scale);
            int height = (int)Math.Round(Math.Max(ClientSize.Height, Height) * scale);

            const int margin = 8;
            int x = area.X + area.Width - width - (int)(margin * scale);
            int y = area.Y + area.Height - height - (int)(margin * scale);

            Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
        }
    }
}
