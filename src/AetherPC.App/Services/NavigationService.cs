using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AetherPC.App.Services;

public interface INavigationService
{
    ObservableObject? Current { get; }
    string CurrentKey { get; }
    event EventHandler? Navigated;
    void Navigate(string key);
}

public sealed class NavigationService : ObservableObject, INavigationService
{
    private readonly IServiceProvider _sp;
    private ObservableObject? _current;
    private string _currentKey = "dashboard";

    public NavigationService(IServiceProvider sp) => _sp = sp;

    public ObservableObject? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public string CurrentKey
    {
        get => _currentKey;
        private set => SetProperty(ref _currentKey, value);
    }

    public event EventHandler? Navigated;

    public void Navigate(string key)
    {
        try
        {
            var resolvedKey = NormalizeKey(key);
            var page = Resolve(resolvedKey);
            if (page is null)
            {
                resolvedKey = "dashboard";
                page = Resolve(resolvedKey);
            }
            if (page is null) return;

            CurrentKey = resolvedKey;
            Current = page;
            Navigated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            try
            {
                AetherDialog.Error("AetherPC", "No se pudo abrir la página.\n\n" + ex.Message);
            }
            catch { /* */ }
        }
    }

    private static string NormalizeKey(string key) => key switch
    {
        "dashboard" or "hardware" or "monitor" or "optimize" or "beast" or "cleanup"
            or "drivers" or "history" or "settings"
            or "processes" or "services" or "welcome" => key,
        _ => "dashboard"
    };

    private ObservableObject? Resolve(string key)
    {
        Type? type = key switch
        {
            "dashboard" => typeof(ViewModels.DashboardViewModel),
            "hardware" => typeof(ViewModels.HardwareViewModel),
            "monitor" => typeof(ViewModels.MonitorViewModel),
            "optimize" => typeof(ViewModels.OptimizeViewModel),
            "beast" => typeof(ViewModels.BeastModeViewModel),
            "cleanup" => typeof(ViewModels.CleanupViewModel),
            "drivers" => typeof(ViewModels.DriversViewModel),
            "history" => typeof(ViewModels.HistoryViewModel),
            "settings" => typeof(ViewModels.SettingsViewModel),
            "processes" => typeof(ViewModels.ProcessesViewModel),
            "services" => typeof(ViewModels.ServicesViewModel),
            "welcome" => typeof(ViewModels.WelcomeViewModel),
            _ => typeof(ViewModels.DashboardViewModel)
        };

        try
        {
            return _sp.GetService(type!) as ObservableObject;
        }
        catch
        {
            return null;
        }
    }
}
