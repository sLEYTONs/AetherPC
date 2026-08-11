using AetherPC.App.Services;
using AetherPC.Core.Windows;

namespace AetherPC.App.ViewModels;

/// <summary>Comprueba elevación antes de aplicar Optimizar / Bestia.</summary>
internal static class ElevationGate
{
    public static bool EnsureAdminOrWarn()
    {
        if (ProcessPrivileges.IsElevated)
        {
            ProcessPrivileges.EnableDebugAndPriorityPrivileges();
            return true;
        }

        AetherDialog.Warn("AetherPC", UiLoc.Instance.T("Vm.NeedAdmin"));
        return false;
    }
}
