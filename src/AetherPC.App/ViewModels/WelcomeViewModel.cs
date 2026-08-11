using AetherPC.App.Services;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherPC.App.ViewModels;

public partial class WelcomeViewModel : ObservableObject
{
    private readonly IAppSettingsStore _settings;
    private readonly INavigationService _nav;

    public WelcomeViewModel(IAppSettingsStore settings, INavigationService nav)
    {
        _settings = settings;
        _nav = nav;
    }

    [RelayCommand]
    private async Task SetLanguageAsync(string? lang)
    {
        var code = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";
        UiLoc.Instance.SetLanguage(code);
        try
        {
            var profile = await _settings.LoadProfileAsync();
            profile.Language = code;
            await _settings.SaveProfileAsync(profile);
        }
        catch { /* */ }
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(StartLabel));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            var profile = await _settings.LoadProfileAsync();
            profile.OnboardingCompleted = true;
            if (string.IsNullOrWhiteSpace(profile.Language))
                profile.Language = UiLoc.Instance.IsEnglish ? "en" : "es";
            await _settings.SaveProfileAsync(profile);
        }
        catch { /* */ }
        _nav.Navigate("dashboard");
    }

    public string Title => UiLoc.Instance.T("Welcome.Title");
    public string Body => UiLoc.Instance.T("Welcome.Body");
    public string StartLabel => UiLoc.Instance.T("Welcome.Start");
}
