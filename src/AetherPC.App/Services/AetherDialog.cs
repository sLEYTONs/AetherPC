using System.Windows;

namespace AetherPC.App.Services;

public enum AetherDialogKind
{
    Info,
    Success,
    Warning,
    Error,
    Confirm
}

/// <summary>Diálogos con el diseño de AetherPC (reemplaza MessageBox clásico).</summary>
public static class AetherDialog
{
    public static void Info(string title, string message)
        => Show(title, message, AetherDialogKind.Info, UiLoc.Instance.T("Common.GotIt"), showCancel: false);

    public static void Success(string title, string message)
        => Show(title, message, AetherDialogKind.Success, UiLoc.Instance.T("Common.Done"), showCancel: false);

    public static void Warn(string title, string message)
        => Show(title, message, AetherDialogKind.Warning, UiLoc.Instance.T("Common.GotIt"), showCancel: false);

    public static void Error(string title, string message)
        => Show(title, message, AetherDialogKind.Error, UiLoc.Instance.T("Common.Close"), showCancel: false);

    public static bool Confirm(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null)
        => Show(
            title,
            message,
            AetherDialogKind.Confirm,
            confirmText ?? UiLoc.Instance.T("Common.YesContinue"),
            showCancel: true,
            cancelText ?? UiLoc.Instance.T("Common.Cancel"));

    private static bool Show(
        string title,
        string message,
        AetherDialogKind kind,
        string primaryText,
        bool showCancel,
        string? cancelText = null)
    {
        var owner = System.Windows.Application.Current?.MainWindow is { IsVisible: true } mw ? mw : null;
        var dlg = new Views.AetherDialogWindow
        {
            Owner = owner,
            TitleText = title,
            MessageText = message,
            Kind = kind,
            PrimaryButtonText = primaryText,
            CancelButtonText = cancelText ?? UiLoc.Instance.T("Common.Cancel"),
            ShowCancel = showCancel
        };

        if (owner is null)
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dlg.ShowDialog() == true;
    }
}
