using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using G_Lumen.Services;
using G_Lumen.ViewModels;
using G_Lumen.Views;
using Microsoft.Extensions.Logging;

namespace G_Lumen
{
    public partial class App : Application
    {
        private readonly ILogger _log = AppLog.CreateLogger("App");
        private DdcCiService? _ddc;
        private MainViewModel? _mainViewModel;
        private MonitorPopup? _popup;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            try
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    // Tray app: no window at startup, runs until the user chooses Exit.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    var settings = new SettingsStore();
                    settings.Load();

                    var traffic = new TrafficLog(AppLog.TrafficLogPath);
                    _ddc = new DdcCiService(AppLog.CreateLogger<DdcCiService>(), traffic);
                    _mainViewModel = new MainViewModel(
                        _ddc,
                        new HdrService(AppLog.CreateLogger<HdrService>(), traffic),
                        new WmiBrightnessService(AppLog.CreateLogger<WmiBrightnessService>(), traffic),
                        settings,
                        new AutostartManager(),
                        AppLog.CreateLogger<MainViewModel>(),
                        traffic);

                    // App DataContext = source for tray menu (NativeMenu) bindings.
                    DataContext = _mainViewModel;

                    desktop.Exit += (_, _) => _ddc?.Dispose();

                    // Optional auto-open of the popup at startup (diagnostics): GLUMEN_AUTOSHOW=1.
                    if (Environment.GetEnvironmentVariable("GLUMEN_AUTOSHOW") == "1")
                        Dispatcher.UIThread.Post(TogglePopup, DispatcherPriority.Background);
                }

                base.OnFrameworkInitializationCompleted();
            }
            catch (Exception ex)
            {

                _log.LogCritical(ex, "Application initialization failed");
                throw;
            }
        }

        private void OnTrayClicked(object? sender, EventArgs e) => TogglePopup();

        private void TogglePopup()
        {
            try
            {
                if (_mainViewModel is null)
                    return;

                _popup ??= new MonitorPopup { DataContext = _mainViewModel };

                if (_popup.IsVisible)
                    _popup.Hide();
                else
                    _popup.ShowAtTray();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Showing the popup failed");
            }
        }
    }
}
