using Avalonia.Controls;
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
    }
}
