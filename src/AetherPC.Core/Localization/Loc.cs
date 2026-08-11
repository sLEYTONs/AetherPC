using AetherPC.Core.Enums;

namespace AetherPC.Core.Localization;

/// <summary>
/// Fachada estática sobre <see cref="ILocalizer"/>. Los motores usan Loc.T / Loc.Format;
/// no conocen WPF ni el catálogo concreto. Registrar con <see cref="Use"/> al arrancar.
/// </summary>
public static class Loc
{
    private static ILocalizer _provider = PassthroughLocalizer.Instance;

    public static ILocalizer Current => _provider;

    public static string Language => _provider.Language;
    public static bool IsEnglish => _provider.IsEnglish;

    public static void Use(ILocalizer provider)
        => _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public static void SetLanguage(string? lang) => _provider.SetLanguage(lang);

    public static string T(string key) => _provider.T(key);

    public static string T(string key, params object[] args) => _provider.T(key, args);

    /// <summary>Alias explícito para plantillas parametrizadas.</summary>
    public static string Format(string key, params object[] args) => _provider.T(key, args);

    public static bool Has(string key) => _provider.Has(key);

    public static LocValidationResult Validate() => _provider.Validate();

    public static string StatusLabel(ActionApplyStatus status) => status switch
    {
        ActionApplyStatus.Applied => T("Status.Applied"),
        ActionApplyStatus.Failed => T("Status.Failed"),
        ActionApplyStatus.Skipped => T("Status.Skipped"),
        ActionApplyStatus.NeedsReboot => T("Status.NeedsReboot"),
        ActionApplyStatus.AlreadyApplied => T("Status.AlreadyApplied"),
        ActionApplyStatus.NotCompatible => T("Status.NotCompatible"),
        ActionApplyStatus.Cancelled => T("Status.Cancelled"),
        _ => T("Status.Pending")
    };

    /// <summary>Resuelve detalle de resultado: clave+args si existen; si no, texto técnico legacy.</summary>
    public static string ResolveDetail(string? detailKey, IReadOnlyList<string>? detailArgs, string? fallbackDetail)
    {
        if (!string.IsNullOrWhiteSpace(detailKey) && Has(detailKey))
        {
            var args = detailArgs is { Count: > 0 }
                ? detailArgs.Cast<object>().ToArray()
                : Array.Empty<object>();
            return args.Length == 0 ? T(detailKey) : T(detailKey, args);
        }

        return fallbackDetail ?? string.Empty;
    }
}

/// <summary>Proveedor mínimo hasta registrar el catálogo (devuelve la clave).</summary>
internal sealed class PassthroughLocalizer : ILocalizer
{
    public static PassthroughLocalizer Instance { get; } = new();
    public string Language { get; private set; } = "es";
    public bool IsEnglish => Language == "en";
    public void SetLanguage(string? lang)
        => Language = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";
    public string T(string key) => key;
    public string T(string key, params object[] args)
    {
        try { return string.Format(key, args); }
        catch { return key; }
    }
    public bool Has(string key) => false;
    public LocValidationResult Validate() => new([], [], []);
}
