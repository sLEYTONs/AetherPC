namespace AetherPC.Core.Localization;

/// <summary>
/// Abstracción de localización para dominio/motores. Sin dependencias WPF ni de UI.
/// La implementación concreta vive en Infrastructure (catálogo) o App (adaptador visual).
/// </summary>
public interface ILocalizer
{
    string Language { get; }
    bool IsEnglish { get; }
    void SetLanguage(string? lang);
    string T(string key);
    string T(string key, params object[] args);
    bool Has(string key);
    LocValidationResult Validate();
}
