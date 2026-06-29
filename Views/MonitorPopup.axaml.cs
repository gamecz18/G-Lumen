using System;
using Avalonia;
using Avalonia.Controls;

namespace G_Lumen.Views
{
    /// <summary>
    /// Borderless popup s posuvníky jasu. Otevírá se z tray ikony, zavírá se
    /// (skrývá, nezavírá) při ztrátě fokusu, aby šel znovu rychle ukázat.
    /// </summary>
    public partial class MonitorPopup : Window
    {
        public MonitorPopup()
        {
            InitializeComponent();
            WindowStartupLocation = WindowStartupLocation.Manual;
            Deactivated += (_, _) => Hide();
        }

        /// <summary>Zobrazí popup vpravo dole u systémové lišty.</summary>
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
            // Po prvním layoutu známe skutečnou výšku (SizeToContent) → přepočítej pozici.
            PositionBottomRight();
        }

        private void PositionBottomRight()
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
                return;

            var area = screen.WorkingArea; // v pixelech, bez taskbaru
            double scale = screen.Scaling;

            int width = (int)Math.Round(Math.Max(ClientSize.Width, Width) * scale);
            int height = (int)Math.Round(Math.Max(ClientSize.Height, Height) * scale);

            const int margin = 8;
            int x = area.X + area.Width - width - (int)(margin * scale);
            int y = area.Y + area.Height - height - (int)(margin * scale);

            Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
        }
    }
}
