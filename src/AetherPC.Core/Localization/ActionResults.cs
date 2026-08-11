using AetherPC.Core.Enums;
using AetherPC.Core.Models;

namespace AetherPC.Core.Localization;

/// <summary>
/// Fábrica de <see cref="ActionResult"/> con códigos de estado y claves de mensaje (sin frases embebidas).
/// </summary>
public static class ActionResults
{
    public static ActionResult Ok(string actionId, string detailKey, ActionApplyStatus status = ActionApplyStatus.Applied, string? rollbackToken = null, params object?[] args)
        => new()
        {
            ActionId = actionId,
            Success = true,
            Status = status,
            DetailKey = detailKey,
            DetailArgs = ToArgs(args),
            RollbackToken = rollbackToken
        };

    public static ActionResult Fail(string actionId, string detailKey, ActionApplyStatus status = ActionApplyStatus.Failed, params object?[] args)
        => new()
        {
            ActionId = actionId,
            Success = false,
            Status = status,
            DetailKey = detailKey,
            DetailArgs = ToArgs(args)
        };

    public static ActionResult Skip(string actionId, string detailKey, ActionApplyStatus status = ActionApplyStatus.Skipped, params object?[] args)
        => new()
        {
            ActionId = actionId,
            Success = true, // omitir ≠ fallar (p. ej. proceso ya cerrado / no hay juego)
            Status = status,
            DetailKey = detailKey,
            DetailArgs = ToArgs(args)
        };

    public static ActionResult FromException(string actionId, Exception ex, string? prefixKey = null)
        => new()
        {
            ActionId = actionId,
            Success = false,
            Status = ActionApplyStatus.Failed,
            DetailKey = prefixKey ?? "Exec.Error",
            DetailArgs = new[] { ex.Message },
            Detail = ex.Message
        };

    private static string[] ToArgs(object?[]? args)
        => args is null || args.Length == 0
            ? Array.Empty<string>()
            : args.Select(a => a?.ToString() ?? string.Empty).ToArray();
}
