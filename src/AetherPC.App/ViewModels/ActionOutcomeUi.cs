using AetherPC.App.Services;
using AetherPC.Core.Enums;
using AetherPC.Core.Models;

namespace AetherPC.App.ViewModels;

/// <summary>Formato de resultados compartido (Optimizar, Bestia). Omitidos ≠ errores.</summary>
internal static class ActionOutcomeUi
{
    public static bool IsHardFail(ActionResult r)
        => !r.Success && r.Status != ActionApplyStatus.Skipped;

    public static bool Match(string resultId, string actionId)
    {
        if (resultId.Equals(actionId, StringComparison.OrdinalIgnoreCase)) return true;
        if (actionId.StartsWith(resultId + ":", StringComparison.OrdinalIgnoreCase)) return true;
        if (resultId.StartsWith(actionId + ":", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Format(ActionResult r)
    {
        var detail = string.IsNullOrWhiteSpace(r.ResolvedDetail) ? r.Detail : r.ResolvedDetail;
        if (r.Status == ActionApplyStatus.Skipped)
            return UiLoc.Instance.T("Outcome.Skipped", detail);
        if (r.Success)
            return r.Verified
                ? UiLoc.Instance.T("Outcome.Verified", detail)
                : UiLoc.Instance.T("Outcome.Ok", detail);
        return UiLoc.Instance.T("Outcome.Fail", detail);
    }
}
