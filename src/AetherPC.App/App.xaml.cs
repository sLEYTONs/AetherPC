using System.Diagnostics;
using System.IO;
using System.Windows;
using AetherPC.App.Services;
using AetherPC.App.ViewModels;
using AetherPC.Application;
using AetherPC.Core.Localization;
using AetherPC.Infrastructure;
using AetherPC.Optimization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherPC.App;

public partial class App : System.Windows.Application
{
    private static IHost? _host;
    private static int _exiting;
    public static IServiceProvider Services => _host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                AetherDialog.Error("AetherPC", args.Exception.Message);
            }
            catch { /* */ }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                Debug.WriteLine("Unhandled: " + (ex?.Message ?? args.ExceptionObject));
            }
            catch { /* */ }
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l =>
                {
                    l.ClearProviders();
                    l.AddDebug();
                    l.SetMinimumLevel(LogLevel.Warning);
                })
                .ConfigureServices((_, services) =>
                {
                    services.AddAetherApplication();
                    services.AddAetherInfrastructure();
                    services.AddAetherOptimization();

                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<HardwareViewModel>();
                    services.AddSingleton<MonitorViewModel>();
                    services.AddSingleton<OptimizeViewModel>();
                    services.AddSingleton<BeastModeViewModel>();
                    services.AddSingleton<CleanupViewModel>();
                    services.AddSingleton<BenchmarkViewModel>();
                    services.AddSingleton<SecurityViewModel>();
                    services.AddSingleton<DriversViewModel>();
                    services.AddSingleton<HistoryViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<ProcessesViewModel>();
                    services.AddSingleton<ServicesViewModel>();
                    services.AddSingleton<WelcomeViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            // Localización: Core solo ve ILocalizer; el catálogo vive en Infrastructure.
            Loc.Use(Services.GetRequiredService<AetherPC.Core.Localization.ILocalizer>());

            try
            {
                var settings = Services.GetRequiredService<AetherPC.Core.Abstractions.IAppSettingsStore>();
                var profile = await settings.LoadProfileAsync();

                if (string.IsNullOrWhiteSpace(profile.Theme)) profile.Theme = "Dark";
                if (string.IsNullOrWhiteSpace(profile.Language)) profile.Language = "es";
                ThemeService.Apply(profile.Theme);
                UiLoc.Instance.SetLanguage(profile.Language);

                var showWelcome = !profile.OnboardingCompleted;
                // No marcar OnboardingCompleted aquí: lo hace WelcomeView al pulsar Empezar.

                var main = Services.GetRequiredService<MainWindow>();
                MainWindow = main;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                main.Show();
                main.Activate();

                if (showWelcome)
                {
                    var nav = Services.GetRequiredService<INavigationService>();
                    nav.Navigate("welcome");
                }
            }
            catch
            {
                ThemeService.Apply("Dark");
                UiLoc.Instance.SetLanguage("es");
                var main = Services.GetRequiredService<MainWindow>();
                MainWindow = main;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                main.Show();
                main.Activate();
            }
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "No se pudo iniciar AetherPC.\n\n" + ex.Message,
                    "AetherPC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* */ }
            RequestFullExit();
        }
    }

    /// <summary>Cierre total al salir de la ventana principal.</summary>
    public static void RequestFullExit()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

        try
        {
            try
            {
                if (_host?.Services.GetService(typeof(DashboardViewModel)) is DashboardViewModel dash)
                    dash.StopLive();
                if (_host?.Services.GetService(typeof(MonitorViewModel)) is MonitorViewModel mon)
                    mon.StopLive();
                if (_host?.Services.GetService(typeof(AetherPC.Core.Abstractions.IDisplayControlService))
                    is AetherPC.Core.Abstractions.IDisplayControlService disp)
                    disp.RestoreAllSoftColor();
            }
            catch { /* */ }

            if (_host is not null)
            {
                try { _host.StopAsync(TimeSpan.FromMilliseconds(800)).GetAwaiter().GetResult(); } catch { /* */ }
                try { _host.Dispose(); } catch { /* */ }
                _host = null;
            }

            // Portable: limpiar AetherPC.sys que LHM deja junto al EXE
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var sidecar = Path.ChangeExtension(exe, ".sys");
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }
            catch { /* ignore */ }
        }
        finally
        {
            try { Current?.Shutdown(0); } catch { /* */ }
            // Salida definitiva para no dejar proceso zombie
            Environment.Exit(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 0)
        {
            try
            {
                if (_host is not null)
                {
                    try { _host.StopAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult(); } catch { /* */ }
                    try { _host.Dispose(); } catch { /* */ }
                    _host = null;
                }
            }
            catch { /* */ }
        }

        base.OnExit(e);
    }
}
