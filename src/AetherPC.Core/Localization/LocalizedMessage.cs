namespace AetherPC.Core.Localization;

/// <summary>
/// Mensaje localizable persistible: clave estable + parámetros (sin frase traducida).
/// </summary>
public sealed class LocalizedMessage
{
    public string Key { get; init; } = string.Empty;
    public string[] Args { get; init; } = Array.Empty<string>();

    public static LocalizedMessage Of(string key, params object?[] args) => new()
    {
        Key = key,
        Args = args.Select(a => a?.ToString() ?? string.Empty).ToArray()
    };

    public string Resolve() => Args.Length == 0 ? Loc.T(Key) : Loc.T(Key, Args.Cast<object>().ToArray());
}
