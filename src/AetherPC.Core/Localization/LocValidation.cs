namespace AetherPC.Core.Localization;

public sealed record LocValidationResult(
    IReadOnlyList<string> MissingInEn,
    IReadOnlyList<string> MissingInEs,
    IReadOnlyList<string> EmptyValues)
{
    public bool Ok => MissingInEn.Count == 0 && MissingInEs.Count == 0 && EmptyValues.Count == 0;
}
