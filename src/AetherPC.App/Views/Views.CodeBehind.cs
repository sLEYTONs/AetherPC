using System.Windows;
using System.Windows.Controls;
using AetherPC.App.ViewModels;
using AetherPC.App.Services;

namespace AetherPC.App.Views;

public partial class WelcomeView : System.Windows.Controls.UserControl
{
    public WelcomeView() => InitializeComponent();
}

public partial class HardwareView : UserControl
{
    public HardwareView() => InitializeComponent();
    private HardwareViewModel? Vm => DataContext as HardwareViewModel;

    private async void HardwareView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try
        {
            // Siempre forzar carga al abrir la vista si aún no hay datos;
            // si ya hay, refrescar edad (Refresh manual fuerza de nuevo).
            await Vm.LoadCoreAsync(force: Vm.Snapshot is null);
        }
        catch (Exception ex)
        {
            try { Vm.Status = "Error: " + ex.Message; } catch { /* */ }
        }
    }
}
public partial class MonitorView : UserControl { public MonitorView() => InitializeComponent(); }

public partial class BenchmarkView : UserControl
{
    private int _gate;
    public BenchmarkView() => InitializeComponent();
    private BenchmarkViewModel? Vm => DataContext as BenchmarkViewModel;
    private async void BenchmarkView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null || System.Threading.Interlocked.Exchange(ref _gate, 1) != 0) return;
        try { await Vm.LoadCoreAsync(); } catch { /* */ }
    }
}

public partial class SecurityView : UserControl
{
    private int _gate;
    public SecurityView() => InitializeComponent();
    private SecurityViewModel? Vm => DataContext as SecurityViewModel;
    private async void SecurityView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null || System.Threading.Interlocked.Exchange(ref _gate, 1) != 0) return;
        try
        {
            await Vm.LoadCoreAsync();
        }
        catch (Exception ex)
        {
            try { if (Vm is not null) Vm.Status = "Error: " + ex.Message; } catch { /* */ }
        }
    }
}

public partial class DriversView : UserControl
{
    private int _gate;
    public DriversView() => InitializeComponent();
    private DriversViewModel? Vm => DataContext as DriversViewModel;
    private async void DriversView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null || System.Threading.Interlocked.Exchange(ref _gate, 1) != 0) return;
        try { await Vm.LoadCoreAsync(); } catch { /* */ }
    }
}

public partial class HistoryView : UserControl
{
    private int _gate;
    public HistoryView() => InitializeComponent();
    private HistoryViewModel? Vm => DataContext as HistoryViewModel;
    private async void HistoryView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null || System.Threading.Interlocked.Exchange(ref _gate, 1) != 0) return;
        try { await Vm.LoadCoreAsync(); } catch { /* */ }
    }
}

public partial class SettingsView : UserControl
{
    private int _gate;
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnPrefToggle), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(OnPrefToggle), true);
    }
    private SettingsViewModel? Vm => DataContext as SettingsViewModel;
    private async void SettingsView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null || System.Threading.Interlocked.Exchange(ref _gate, 1) != 0) return;
        try { await Vm.LoadCoreAsync(); } catch { /* */ }
    }

    private async void OnPrefToggle(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not CheckBox) return;
        try { await Vm.SavePreferenceCommand.ExecuteAsync(null); } catch { /* */ }
    }
}

public partial class ServicesView : UserControl
{
    private int _loadGate;

    public ServicesView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) => await TryLoadAsync();
    }

    private ServicesViewModel? Vm => DataContext as ServicesViewModel;

    private async void ServicesView_OnLoaded(object sender, RoutedEventArgs e)
        => await TryLoadAsync();

    private async Task TryLoadAsync()
    {
        if (Vm is null) return;
        if (System.Threading.Interlocked.Exchange(ref _loadGate, 1) != 0) return;
        try { await Vm.RefreshCoreAsync(); }
        catch { /* Status en VM */ }
    }

    private async void BtnRefresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        ((Button)sender).IsEnabled = false;
        try { await Vm.RefreshCoreAsync(); }
        finally { ((Button)sender).IsEnabled = true; }
    }
}

public partial class OptimizeView : UserControl
{
    public OptimizeView() => InitializeComponent();
    private OptimizeViewModel? Vm => DataContext as OptimizeViewModel;

    private async void BtnAnalyze_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) { AetherDialog.Error("AetherPC", "DataContext nulo. Reinicia AetherPC."); return; }
        ((Button)sender).IsEnabled = false;
        try { await Vm.AnalyzeCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error("Optimizar", ex.Message); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    private async void BtnApply_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) { AetherDialog.Error("AetherPC", "DataContext nulo. Reinicia AetherPC."); return; }
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try { await Vm.ApplyCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error("Optimizar", ex.Message); }
        finally { btn.IsEnabled = true; }
    }
}

public partial class BeastModeView : UserControl
{
    public BeastModeView() => InitializeComponent();

    private BeastModeViewModel? Vm => DataContext as BeastModeViewModel;

    private async void BtnAnalyze_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) { AetherDialog.Error("AetherPC", "DataContext nulo. Reinicia AetherPC."); return; }
        ((Button)sender).IsEnabled = false;
        try { await Vm.AnalyzeCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error("Modo Bestia", ex.Message); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    private async void BtnApply_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) { AetherDialog.Error("AetherPC", "DataContext nulo. Reinicia AetherPC."); return; }
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try { await Vm.ApplyCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error("Modo Bestia", ex.Message); }
        finally { btn.IsEnabled = true; }
    }
}

public partial class CleanupView : UserControl
{
    private int _gate;
    public CleanupView() => InitializeComponent();
    private CleanupViewModel? Vm => DataContext as CleanupViewModel;

    private async void CleanupView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null || System.Threading.Interlocked.Exchange(ref _gate, 1) != 0) return;
        try { await Vm.ScanCoreAsync(); } catch { /* */ }
    }

    private async void BtnScan_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        ((Button)sender).IsEnabled = false;
        try { await Vm.ScanCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error("Limpieza", ex.Message); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    private async void BtnCleanSafe_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        ((Button)sender).IsEnabled = false;
        try { await Vm.CleanCoreAsync(safeOnly: true); }
        catch (Exception ex) { AetherDialog.Error("Limpieza", ex.Message); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    private async void BtnCleanSelected_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        ((Button)sender).IsEnabled = false;
        try { await Vm.CleanCoreAsync(safeOnly: false); }
        catch (Exception ex) { AetherDialog.Error("Limpieza", ex.Message); }
        finally { ((Button)sender).IsEnabled = true; }
    }
}

public partial class ProcessesView : UserControl
{
    private int _loadGate;

    public ProcessesView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) => await TryInitialLoadAsync();
    }

    private ProcessesViewModel? Vm => DataContext as ProcessesViewModel;

    private async void ProcessesView_OnLoaded(object sender, RoutedEventArgs e)
        => await TryInitialLoadAsync();

    private async Task TryInitialLoadAsync()
    {
        if (Vm is null) return;
        if (System.Threading.Interlocked.Exchange(ref _loadGate, 1) != 0) return;
        try { await Vm.RefreshCoreAsync(); }
        catch { /* Status en VM */ }
    }

    private async void BtnRefresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        ((Button)sender).IsEnabled = false;
        try { await Vm.RefreshCoreAsync(); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    private async void BtnClose_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try { await Vm.CloseSelectedCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error(UiLoc.Instance.T("Page.Processes"), ex.Message); }
    }

    private async void BtnPriority_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try { await Vm.LowerPriorityCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error(UiLoc.Instance.T("Page.Processes"), ex.Message); }
    }

    private async void BtnSuspend_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try { await Vm.SuspendSelectedCoreAsync(); }
        catch (Exception ex) { AetherDialog.Error(UiLoc.Instance.T("Page.Processes"), ex.Message); }
    }
}
