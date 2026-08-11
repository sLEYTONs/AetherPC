using System.ComponentModel;
using System.Runtime.CompilerServices;
using AetherPC.Core.Localization;

namespace AetherPC.App.Services;

/// <summary>
/// Adaptador WPF sobre <see cref="ILocalizer"/> / <see cref="Loc"/>.
/// No contiene el catálogo: solo notifica a los bindings cuando cambia el idioma.
/// </summary>
public sealed class UiLoc : INotifyPropertyChanged
{
    public static UiLoc Instance { get; } = new();

    public string Language => Loc.Language;
    public bool IsEnglish => string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.T(key);

    public string T(string key) => Loc.T(key);

    public string T(string key, params object[] args) => Loc.T(key, args);

    public string Format(string key, params object[] args) => Loc.Format(key, args);

    public void SetLanguage(string? lang)
    {
        Loc.SetLanguage(lang);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
