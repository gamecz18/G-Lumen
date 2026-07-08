using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

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

            // SizeToContent: rozbalení diagnostiky / detailu monitoru změní výšku
            // → okno se musí znovu přilepit k liště.
            SizeChanged += (_, _) =>
            {
                if (IsVisible)
                    PositionBottomRight();
            };
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

        /// <summary>
        /// Tažení úchytu nad diagnostickým logem: mění výšku výpisu.
        /// Okno je ukotvené dole, takže růst logu vizuálně roztahuje popup nahoru.
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

            var area = screen.WorkingArea; // v pixelech, bez taskbaru
            double scale = screen.Scaling;

            // Popup nesmí přerůst pracovní plochu (jinak by se obsah ořezával).
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
