using System.Windows;
using System.Windows.Media;
using AetherPC.App.ViewModels;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using Control = System.Windows.Controls.Control;
using UIElement = System.Windows.UIElement;

namespace AetherPC.App;

public partial class MainWindow : FluentWindow
{
    private bool _ready;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.CurrentKey) or null)
            {
                Dispatcher.BeginInvoke(ApplyNavSelectedFromCurrentKey, System.Windows.Threading.DispatcherPriority.Loaded);
                Dispatcher.BeginInvoke(ApplyNavSelectedFromCurrentKey, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        };
        try { FitToScreen(); } catch { /* no tumbar el arranque */ }
        SourceInitialized += (_, _) =>
        {
            try { FitToScreen(); } catch { /* */ }
        };
        ContentRendered += (_, _) =>
        {
            _ready = true;
            ApplyNavSelectedFromCurrentKey();
        };
        Closed += (_, _) =>
        {
            if (_ready)
                App.RequestFullExit();
        };
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try { FitToScreen(); } catch { /* */ }
        try { ChromeTitleBar.Title = ""; } catch { /* */ }
        _ready = true;
        ApplyNavSelectedFromCurrentKey();
    }

    /// <summary>
    /// Selected permanente ligado a CurrentKey (página real de INavigationService).
    /// No depende de hover ni del último click.
    /// </summary>
    private void ApplyNavSelectedFromCurrentKey()
    {
        if (DataContext is not MainViewModel vm || NavPanel is null)
            return;

        var key = vm.CurrentKey ?? "";
        var activeBg = TryFindBrush("BrushNavActiveBg");
        var activeFg = TryFindBrush("BrushNavActiveFg");
        var activeBorder = TryFindBrush("BrushNavActiveBorder");
        var activeBar = TryFindBrush("BrushNavActiveBar");
        var accent = TryFindBrush("BrushAccent");

        foreach (var child in NavPanel.Children)
        {
            if (child is not Button btn || btn.Tag is not string tag)
                continue;

            var selected = string.Equals(tag, key, StringComparison.OrdinalIgnoreCase);

            if (selected)
            {
                if (activeBg is not null)
                    btn.SetValue(Control.BackgroundProperty, activeBg);
                if (activeBorder is not null)
                    btn.SetValue(Control.BorderBrushProperty, activeBorder);
                if (activeFg is not null)
                    btn.SetValue(Control.ForegroundProperty, activeFg);
                btn.SetValue(Control.FontWeightProperty, FontWeights.SemiBold);

                if (btn.Template?.FindName("bd", btn) is Border bd)
                {
                    if (activeBg is not null)
                        bd.SetValue(Border.BackgroundProperty, activeBg);
                    if (activeBorder is not null)
                        bd.SetValue(Border.BorderBrushProperty, activeBorder);
                }

                if (btn.Template?.FindName("activeBar", btn) is Border bar)
                {
                    bar.SetValue(UIElement.OpacityProperty, 1.0);
                    if (activeBar is not null)
                        bar.SetValue(Border.BackgroundProperty, activeBar);
                }

                if (accent is not null)
                    ApplyAccentToIcons(btn, accent);
            }
            else
            {
                btn.ClearValue(Control.BackgroundProperty);
                btn.ClearValue(Control.BorderBrushProperty);
                btn.ClearValue(Control.ForegroundProperty);
                btn.ClearValue(Control.FontWeightProperty);
                btn.ClearValue(UIElement.OpacityProperty);

                if (btn.Template?.FindName("bd", btn) is Border bd)
                {
                    bd.ClearValue(Border.BackgroundProperty);
                    bd.ClearValue(Border.BorderBrushProperty);
                }

                if (btn.Template?.FindName("activeBar", btn) is Border bar)
                    bar.SetValue(UIElement.OpacityProperty, 0.0);
            }
        }
    }

    private static void ApplyAccentToIcons(DependencyObject root, Brush accent)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Shapes.Shape shape)
                shape.Fill = accent;
            ApplyAccentToIcons(child, accent);
        }
    }

    private static Brush? TryFindBrush(string key)
    {
        try { return System.Windows.Application.Current?.TryFindResource(key) as Brush; }
        catch { return null; }
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            ClearSizeCaps();
            return;
        }

        try { FitToScreen(); } catch { /* */ }
    }

    private void FitToScreen()
    {
        if (WindowState == WindowState.Maximized) return;

        var wa = SystemParameters.WorkArea;
        if (wa.Width < 100 || wa.Height < 100) return;

        ClearSizeCaps();

        var minW = Math.Min(960, wa.Width * 0.92);
        var minH = Math.Min(600, wa.Height * 0.88);
        MinWidth = Math.Max(640, minW * 0.85);
        MinHeight = Math.Max(480, minH * 0.85);

        var targetW = Math.Min(1440, wa.Width * 0.92);
        var targetH = Math.Min(Math.Max(wa.Height * 0.92, MinHeight), wa.Height);

        Width = Math.Max(MinWidth, Math.Min(targetW, wa.Width));
        Height = Math.Max(MinHeight, Math.Min(targetH, wa.Height));

        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
    }

    private void ClearSizeCaps()
    {
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
    }
}
