using System.Windows;
using System.Windows.Media;
using AetherPC.App.Services;

namespace AetherPC.App.Views;

public partial class AetherDialogWindow : Window
{
    public string TitleText { get; set; } = "AetherPC";
    public string MessageText { get; set; } = "";
    public AetherDialogKind Kind { get; set; } = AetherDialogKind.Info;
    public string PrimaryButtonText { get; set; } = "OK";
    public string CancelButtonText { get; set; } = "Cancel";
    public bool ShowCancel { get; set; }

    public AetherDialogWindow()
    {
        InitializeComponent();
        PrimaryButtonText = UiLoc.Instance.Language == "en" ? "OK" : "Entendido";
        CancelButtonText = UiLoc.Instance.T("Common.Cancel");
        Loaded += OnLoaded;
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && ShowCancel)
            {
                DialogResult = false;
                Close();
            }
            else if (e.Key == System.Windows.Input.Key.Enter)
            {
                DialogResult = true;
                Close();
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBlock.Text = TitleText;
        MessageBlock.Text = MessageText;
        BtnPrimary.Content = PrimaryButtonText;
        BtnCancel.Content = CancelButtonText;
        BtnCancel.Visibility = ShowCancel ? Visibility.Visible : Visibility.Collapsed;

        var (accent, badge, icon) = Kind switch
        {
            AetherDialogKind.Success => (Color.FromRgb(0x1F, 0x8A, 0x4C), Color.FromRgb(0x22, 0xC5, 0x5E), "✓"),
            AetherDialogKind.Warning => (Color.FromRgb(0xB4, 0x6E, 0x00), Color.FromRgb(0xF0, 0xA2, 0x02), "!"),
            AetherDialogKind.Error => (Color.FromRgb(0xA1, 0x2B, 0x2B), Color.FromRgb(0xFF, 0x5C, 0x5C), "✕"),
            AetherDialogKind.Confirm => (Color.FromRgb(0x1F, 0x6F, 0xEB), Color.FromRgb(0x3B, 0xA4, 0xFF), "?"),
            _ => (Color.FromRgb(0x1A, 0x27, 0x40), Color.FromRgb(0x3B, 0xA4, 0xFF), "i")
        };

        AccentBar.Background = new LinearGradientBrush(
            Color.FromArgb(0x55, accent.R, accent.G, accent.B),
            Color.FromArgb(0x00, accent.R, accent.G, accent.B),
            90);
        KindBadge.Background = new SolidColorBrush(badge);
        KindIcon.Text = icon;
        BtnPrimary.Background = new SolidColorBrush(
            Kind is AetherDialogKind.Error ? Color.FromRgb(0xC4, 0x3C, 0x3C)
            : Kind is AetherDialogKind.Success ? Color.FromRgb(0x1F, 0x8A, 0x4C)
            : Kind is AetherDialogKind.Warning ? Color.FromRgb(0xC4, 0x7E, 0x00)
            : Color.FromRgb(0x1F, 0x6F, 0xEB));

        BtnPrimary.Focus();

        // No superar ~80% del área de trabajo (DPI / pantallas pequeñas)
        try
        {
            var area = SystemParameters.WorkArea;
            MaxHeight = Math.Max(220, area.Height * 0.82);
            MaxWidth = Math.Min(720, Math.Max(360, area.Width * 0.9));
            if (ActualWidth > MaxWidth)
                Width = MaxWidth;
        }
        catch
        {
            // SystemParameters puede fallar en entornos headless; conservar XAML
        }

        // CenterOwner falla a menudo con AllowsTransparency + SizeToContent: recentrar a mano.
        CenterOverOwner();
    }

    private void CenterOverOwner()
    {
        try
        {
            UpdateLayout();
            if (Owner is { IsVisible: true } owner)
            {
                var left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
                var top = owner.Top + (owner.ActualHeight - ActualHeight) / 2;
                // Si el owner está maximizado, Left/Top pueden ser negativos (borde Aero).
                var area = owner.RestoreBounds.Width > 0
                    ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
                    : SystemParameters.WorkArea;
                Left = Math.Max(area.Left, left);
                Top = Math.Max(area.Top, top);
            }
            else
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - ActualWidth) / 2;
                Top = area.Top + (area.Height - ActualHeight) / 2;
            }
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void BtnPrimary_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
