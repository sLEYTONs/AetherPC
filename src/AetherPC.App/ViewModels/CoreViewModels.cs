using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AetherPC.App.Services;
using AetherPC.Application.Recommendations;
using AetherPC.Application.Scanning;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherPC.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _nav;
    private readonly IPrivilegeService _privileges;

    public MainViewModel(INavigationService nav, IPrivilegeService privileges)
    {
        _nav = nav;
        _privileges = privileges;
        _nav.Navigated += (_, _) => SyncNav();
        if (_nav is System.ComponentModel.INotifyPropertyChanged inpc)
        {
            inpc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is null or nameof(INavigationService.CurrentKey) or nameof(INavigationService.Current))
                    SyncNav();
            };
        }
        UiLoc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ElevationLabel));
        _nav.Navigate("dashboard");
        SyncNav();
    }

    private void SyncNav()
    {
        CurrentKey = _nav.CurrentKey;
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CurrentKey));
        OnPropertyChanged(nameof(ShowSidebar));
        OnPropertyChanged(nameof(ContentInset));
    }

    public ObservableObject? CurrentPage => _nav.Current;

    /// <summary>Clave de página abierta — fuente única del estado Selected de la sidebar.</summary>
    [ObservableProperty] private string _currentKey = "dashboard";

    public bool ShowSidebar => !string.Equals(CurrentKey, "welcome", StringComparison.OrdinalIgnoreCase);

    /// <summary>En bienvenida: 0 (tarjeta centrada a pantalla). Con menú: hueco para sidebar + titlebar.</summary>
    public Thickness ContentInset => ShowSidebar
        ? new Thickness(236, 40, 16, 14)
        : new Thickness(0);

    public bool IsElevated => _privileges.IsElevated;
    public string ElevationLabel => IsElevated
        ? UiLoc.Instance.T("App.Admin")
        : UiLoc.Instance.T("App.StandardUser");

    [RelayCommand]
    private void Navigate(string key) => _nav.Navigate(key);
}

/// <summary>Fila de recomendación en Inicio con impacto localizado (Impacto/Impact).</summary>
public sealed class HomeRecommendationItem : ObservableObject
{
    public HomeRecommendationItem(Recommendation model) => Model = model;
    public Recommendation Model { get; }
    public string Title => Model.Title;
    public string Problem => Model.Problem;
    public string RiskLabel => Model.Risk switch
    {
        RiskLevel.Low => UiLoc.Instance.T("Risk.Low"),
        RiskLevel.Medium => UiLoc.Instance.T("Risk.Medium"),
        RiskLevel.High => UiLoc.Instance.T("Risk.High"),
        _ => Model.Risk.ToString()
    };
    public string RiskDisplay => UiLoc.Instance.T("Home.ImpactLine", RiskLabel);
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(RiskLabel));
        OnPropertyChanged(nameof(RiskDisplay));
    }
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly ScanEngine _scan;
    private readonly IRecommendationEngine _recs;
    private readonly IAppSettingsStore _settings;
    private readonly IHealthScorer _health;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private CancellationTokenSource? _analysisCts;
    private int _refreshGate;

    [ObservableProperty] private int _healthScore;
    [ObservableProperty] private string _healthScoreText = "—";
    [ObservableProperty] private string _healthScoreHint = UiLoc.Instance.T("Home.NoAnalysisYet");
    [ObservableProperty] private string _cpuName = "…";
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private double _ramUsage;
    [ObservableProperty] private string _ramDetail = "";
    [ObservableProperty] private string _gpuName = "…";
    [ObservableProperty] private string _tempLabel = "…";
    [ObservableProperty] private string _diskSummary = "";
    [ObservableProperty] private string _networkSummary = "";
    [ObservableProperty] private string _securitySummary = "";
    [ObservableProperty] private string _osSummary = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = UiLoc.Instance.T("Home.Starting");
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressStage = "";
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _timingText = "";
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _hardwareProfileSummary = "";
    [ObservableProperty] private int _availableFactorCount;

    private CancellationTokenSource? _hideProgressCts;
    private int _progressHideGeneration;

    public ObservableCollection<HomeRecommendationItem> Recommendations { get; } = new();
    public ObservableCollection<HealthFactor> Factors { get; } = new();

    public DashboardViewModel(ScanEngine scan, IRecommendationEngine recs, IAppSettingsStore settings, IHealthScorer health)
    {
        _scan = scan;
        _recs = recs;
        _settings = settings;
        _health = health;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshLiveAsync();
        _timer.Start();
        UiLoc.Instance.PropertyChanged += async (_, _) => await RefreshRecommendationsForLanguageAsync();
        _ = BootstrapAsync();
    }

    private SystemSnapshot? _lastDeep;

    /// <summary>Tras Optimizar/Bestia: reescanea y actualiza recomendaciones (quita las ya resueltas).</summary>
    public async Task RefreshAfterOptimizationsAsync()
    {
        try
        {
            _scan.InvalidateCache();
            var profile = await _settings.LoadProfileAsync();
            var deep = await _scan.GetSnapshotAsync(ScanDepth.Deep, force: true);
            Apply(deep);
            _lastDeep = deep;
            var list = await _recs.AnalyzeAsync(deep, profile);
            SetRecommendations(list);
            var hw = HardwareProfileBuilder.Build(deep, profile);
            HardwareProfileSummary = UiLoc.Instance.T("Home.ProfileLine", hw.Aggressiveness, hw.PrimaryLimitation, $"{hw.RamGb:F0}");
            StatusText = UiLoc.Instance.T("Home.ReadyAt", HardwareProfileSummary, DateTime.Now.ToString("HH:mm:ss"));
        }
        catch
        {
            /* no tumbar UI */
        }
    }

    private void SetRecommendations(IEnumerable<Recommendation> list)
    {
        Recommendations.Clear();
        foreach (var r in list.Take(8))
            Recommendations.Add(new HomeRecommendationItem(r));
    }

    private async Task RefreshRecommendationsForLanguageAsync()
    {
        try
        {
            if (_lastDeep is null)
            {
                foreach (var item in Recommendations)
                    item.RefreshLanguage();
                return;
            }
            var profile = await _settings.LoadProfileAsync();
            var list = await _recs.AnalyzeAsync(_lastDeep, profile);
            SetRecommendations(list);
            var hw = HardwareProfileBuilder.Build(_lastDeep, profile);
            HardwareProfileSummary = UiLoc.Instance.T("Home.ProfileLine", hw.Aggressiveness, hw.PrimaryLimitation, $"{hw.RamGb:F0}");
            StatusText = UiLoc.Instance.T("Home.ReadyAt", HardwareProfileSummary, DateTime.Now.ToString("HH:mm:ss"));

            // Re-puntuar salud para que los factores salgan en el idioma activo.
            var (score, factors) = _health.Score(_lastDeep);
            _lastDeep.HealthScore = score;
            _lastDeep.HealthFactors = factors;
            Apply(_lastDeep);
        }
        catch
        {
            foreach (var item in Recommendations)
                item.RefreshLanguage();
        }
    }

    private void BeginProgress()
    {
        _hideProgressCts?.Cancel();
        Interlocked.Increment(ref _progressHideGeneration);
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressStage = "";
    }

    private void CompleteProgress(double finalPercent = 100)
    {
        ProgressPercent = finalPercent;
        var gen = Interlocked.Increment(ref _progressHideGeneration);
        _hideProgressCts?.Cancel();
        _hideProgressCts = new CancellationTokenSource();
        var token = _hideProgressCts.Token;
        _ = HideProgressAfterDelayAsync(gen, token);
    }

    private async Task HideProgressAfterDelayAsync(int generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != _progressHideGeneration || IsBusy) return;
                ShowProgress = false;
                ProgressPercent = 0;
                ProgressStage = "";
            });
        }
        catch (OperationCanceledException)
        {
            /* nuevo análisis o cierre */
        }
    }

    public void StopLive()
    {
        try { _timer.Stop(); } catch { /* */ }
        try { _analysisCts?.Cancel(); } catch { /* */ }
    }

    private async Task BootstrapAsync()
    {
        try
        {
            var profile = await _settings.LoadProfileAsync();
            // Si AutoRefresh está off y hay caché: muestra live sin re-escanear a fondo
            if (!profile.AutoRefreshOnLaunch)
            {
                StatusText = UiLoc.Instance.T("Home.Blurb");
                var cached = await _scan.GetSnapshotAsync(ScanDepth.Fast, force: !profile.PreferCachedAnalysis);
                Apply(cached);
                TimingText = FormatTimings(cached);
                StatusText = UiLoc.Instance.T("Home.CachedReady");
                IsBusy = false;
                CanCancel = false;
                ShowProgress = false;
                return;
            }

            IsBusy = true;
            CanCancel = true;
            BeginProgress();
            _analysisCts = new CancellationTokenSource();
            var progress = new Progress<ScanProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressStage = p.Detail ?? p.Stage;
            });

            StatusText = UiLoc.Instance.T("Home.ScanFast");
            var force = !profile.PreferCachedAnalysis;
            var fast = await _scan.GetSnapshotAsync(ScanDepth.Fast, force: force, progress, _analysisCts.Token);
            Apply(fast);
            TimingText = FormatTimings(fast);
            StatusText = UiLoc.Instance.T("Home.FastReady", (object)(fast.StageTimingsMs?.GetValueOrDefault("total", 0) ?? 0));

            StatusText = UiLoc.Instance.T("Home.ScanDeep");
            var deep = await _scan.GetSnapshotAsync(ScanDepth.Deep, force: true, progress, _analysisCts.Token);
            Apply(deep);
            TimingText = FormatTimings(deep);
            _lastDeep = deep;

            var hw = HardwareProfileBuilder.Build(deep, profile);
            HardwareProfileSummary = UiLoc.Instance.T("Home.ProfileLine", hw.Aggressiveness, hw.PrimaryLimitation, $"{hw.RamGb:F0}");
            var list = await _recs.AnalyzeAsync(deep, profile, _analysisCts.Token);
            SetRecommendations(list);

            StatusText = UiLoc.Instance.T("Home.ReadyAt", HardwareProfileSummary, DateTime.Now.ToString("HH:mm:ss"));
            CompleteProgress(100);
        }
        catch (OperationCanceledException)
        {
            StatusText = UiLoc.Instance.T("Home.Cancelled");
            CompleteProgress(ProgressPercent);
        }
        catch (Exception ex)
        {
            StatusText = UiLoc.Instance.T("Home.Error", ex.Message);
            CompleteProgress(ProgressPercent);
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        CancelAnalysis();
        // Clic manual siempre fuerza análisis completo
        try
        {
            IsBusy = true;
            CanCancel = true;
            BeginProgress();
            _analysisCts = new CancellationTokenSource();
            var progress = new Progress<ScanProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressStage = p.Detail ?? p.Stage;
            });
            StatusText = UiLoc.Instance.T("Home.ScanFast");
            var fast = await _scan.GetSnapshotAsync(ScanDepth.Fast, force: true, progress, _analysisCts.Token);
            Apply(fast);
            StatusText = UiLoc.Instance.T("Home.ScanDeep");
            var deep = await _scan.GetSnapshotAsync(ScanDepth.Deep, force: true, progress, _analysisCts.Token);
            Apply(deep);
            _lastDeep = deep;
            TimingText = FormatTimings(deep);
            var profile = await _settings.LoadProfileAsync(_analysisCts.Token);
            var hw = HardwareProfileBuilder.Build(deep, profile);
            HardwareProfileSummary = UiLoc.Instance.T("Home.ProfileLine", hw.Aggressiveness, hw.PrimaryLimitation, $"{hw.RamGb:F0}");
            var list = await _recs.AnalyzeAsync(deep, profile, _analysisCts.Token);
            SetRecommendations(list);
            StatusText = UiLoc.Instance.T("Home.ReadyAt", HardwareProfileSummary, DateTime.Now.ToString("HH:mm:ss"));
            CompleteProgress(100);
        }
        catch (OperationCanceledException)
        {
            StatusText = UiLoc.Instance.T("Home.Cancelled");
            CompleteProgress(ProgressPercent);
        }
        catch (Exception ex)
        {
            StatusText = UiLoc.Instance.T("Home.Error", ex.Message);
            CompleteProgress(ProgressPercent);
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
        }
    }

    [RelayCommand]
    private void CancelAnalysis()
    {
        try
        {
            _analysisCts?.Cancel();
            _scan.Cancel();
        }
        catch { /* ignore */ }
        CanCancel = false;
        StatusText = "Cancelando…";
    }

    private async Task RefreshLiveAsync()
    {
        if (IsBusy) return;
        if (Interlocked.Exchange(ref _refreshGate, 1) == 1) return;
        try
        {
            var snap = await _scan.GetSnapshotAsync(ScanDepth.Live);
            // Suavizado exponencial: evita saltos bruscos en los % de Inicio
            CpuUsage = SmoothPercent(CpuUsage, snap.Cpu.UsagePercent);
            RamUsage = SmoothPercent(RamUsage, snap.Memory.UsagePercent);
            RamDetail = $"{Format(snap.Memory.UsedBytes)} / {Format(snap.Memory.TotalBytes)}";
        }
        catch { /* keep UI alive */ }
        finally { Interlocked.Exchange(ref _refreshGate, 0); }
    }

    /// <summary>Interpola hacia el valor real (media móvil exponencial).</summary>
    private static double SmoothPercent(double current, double target, double alpha = 0.38)
        => current + (target - current) * alpha;

    private void Apply(SystemSnapshot snap)
    {
        HealthScore = snap.HealthScore;
        var available = snap.HealthFactors.Count(f => f.IsAvailable);
        AvailableFactorCount = available;
        if (available == 0)
        {
            HealthScoreText = "—";
            HealthScoreHint = UiLoc.Instance.T("Home.NotEnoughMetrics");
        }
        else
        {
            HealthScoreText = HealthScore.ToString();
            HealthScoreHint = UiLoc.Instance.T("Home.RealFactors", available);
        }

        CpuName = string.IsNullOrWhiteSpace(snap.Cpu.Name) ? NotDetected.Text : snap.Cpu.Name;
        CpuUsage = snap.Cpu.UsagePercent;
        RamUsage = snap.Memory.TotalBytes > 0 ? snap.Memory.UsagePercent : 0;
        RamDetail = snap.Memory.TotalBytes > 0
            ? $"{Format(snap.Memory.UsedBytes)} / {Format(snap.Memory.TotalBytes)}"
            : NotDetected.Text;
        GpuName = string.IsNullOrWhiteSpace(snap.Gpu?.Name) ? NotDetected.Text : snap.Gpu!.Name;

        var t = snap.Thermals.CpuCelsius ?? snap.Cpu.TemperatureCelsius;
        if (t is not null)
            TempLabel = $"{t:F0} °C";
        else
        {
            var src = snap.Thermals.Source ?? "";
            TempLabel = src.Contains("warming", StringComparison.OrdinalIgnoreCase)
                ? UiLoc.Instance.T("Sensors.Warming")
                : src.Equals("pending", StringComparison.OrdinalIgnoreCase)
                    ? (snap.Depth == ScanDepthUsed.Fast
                        ? UiLoc.Instance.T("Sensors.Deferred")
                        : UiLoc.Instance.T("Sensors.Loading"))
                    : UiLoc.Instance.T("Common.NotDetected");
        }

        DiskSummary = snap.Disks.Count == 0
            ? NotDetected.Text
            : string.Join(" · ", snap.Disks
                .Where(d => d.TotalBytes > 0)
                .Select(d => $"{d.DriveLetter} {d.MediaType} {d.UsedPercent:F0}%"));

        NetworkSummary = snap.Network.IsConnected
            ? $"{(string.IsNullOrWhiteSpace(snap.Network.PrimaryAdapter) ? "NIC" : snap.Network.PrimaryAdapter)} ({snap.Network.IPv4})"
            : UiLoc.Instance.T("Home.NoConnection");

        if (snap.Security.Source is "deferred" or "live-skip" or "" || snap.Security.Source.StartsWith("error:", StringComparison.Ordinal))
        {
            SecuritySummary = UiLoc.Instance.T("Home.SecurityNotRead");
        }
        else
        {
            SecuritySummary = UiLoc.Instance.T("Home.SecurityLine",
                Fmt(snap.Security.DefenderEnabled), Fmt(snap.Security.FirewallEnabled),
                Fmt(snap.Security.TpmPresent), Fmt(snap.Security.SecureBootEnabled));
        }

        OsSummary = string.IsNullOrWhiteSpace(snap.Os.Caption) || snap.Os.Caption == NotDetected.Text
            ? NotDetected.Text
            : $"{snap.Os.Caption} · Build {snap.Os.Build} · Uptime {snap.Uptime:dd\\.hh\\:mm}";

        Factors.Clear();
        foreach (var f in snap.HealthFactors) Factors.Add(f);
    }

    private static string FormatTimings(SystemSnapshot snap)
    {
        if (snap.StageTimingsMs is null || snap.StageTimingsMs.Count == 0) return "";
        return string.Join(" · ", snap.StageTimingsMs.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => $"{kv.Key}:{kv.Value:F0}ms"));
    }

    private static string Fmt(bool? v) => v is null ? "N/D" : v.Value ? "OK" : "Off";
    private static string Format(ulong b) => b > 1024UL * 1024 * 1024 ? $"{b / (1024.0 * 1024 * 1024):F1} GB" : $"{b / (1024.0 * 1024):F0} MB";
}

/// <summary>Monitor con nombre localizado (Primary monitor / Monitor principal).</summary>
public sealed class HardwareMonitorItem
{
    public HardwareMonitorItem(MonitorInfo info) => Info = info;
    public MonitorInfo Info { get; }
    public string Name
    {
        get
        {
            var n = Info.Name ?? "";
            if (string.IsNullOrWhiteSpace(n)
                || n.Equals("__PRIMARY__", StringComparison.OrdinalIgnoreCase)
                || n.Contains("principal", StringComparison.OrdinalIgnoreCase)
                || n.Contains("primary", StringComparison.OrdinalIgnoreCase)
                || n.Contains("predeterminado", StringComparison.OrdinalIgnoreCase)
                || n.Contains("default monitor", StringComparison.OrdinalIgnoreCase))
                return UiLoc.Instance.T("Hardware.PrimaryMonitor");
            return n;
        }
    }
    public int? ScreenWidth => Info.ScreenWidth;
    public int? ScreenHeight => Info.ScreenHeight;
    public double? RefreshHz => Info.RefreshHz;
    public bool IsPrimary => Info.IsPrimary;
}

/// <summary>Adaptador de red con estado localizado (Up/Down ↔ Activo/Inactivo).</summary>
public sealed class HardwareAdapterItem
{
    public HardwareAdapterItem(NetworkAdapterInfo info) => Info = info;
    public NetworkAdapterInfo Info { get; }
    public string Name => Compact(Info.Name);
    public string Type => LocalizeType(Info.Type);
    public string Status => LocalizeStatus(Info.Status);
    public string TypeStatus
    {
        get
        {
            var t = Type;
            var st = Status;
            if (string.IsNullOrWhiteSpace(t)) return st;
            if (string.IsNullOrWhiteSpace(st)) return t;
            return t + " · " + st;
        }
    }
    public string? Speed => Info.Speed;
    public string? IPv4 => Info.IPv4;
    public string? Gateway => Info.Gateway;
    public bool IsVirtual => Info.IsVirtual;

    private static string LocalizeType(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Equals("Ethernet", StringComparison.OrdinalIgnoreCase)) return UiLoc.Instance.T("Net.Type.Ethernet");
        if (s.Equals("Wireless80211", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Wifi", StringComparison.OrdinalIgnoreCase)
            || s.Contains("inalámbric", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Net.Type.Wifi"); // WiFi, sin guion que parte la línea
        if (s.Equals("Loopback", StringComparison.OrdinalIgnoreCase)) return UiLoc.Instance.T("Net.Type.Loopback");
        if (s.Equals("Tunnel", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Ppp", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Net.Type.Tunnel");
        return s;
    }

    private static string LocalizeStatus(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Equals("Up", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Activo", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Net.Status.Up");
        if (s.Equals("Down", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Inactivo", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Net.Status.Down");
        if (s.Equals("Dormant", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Net.Status.Dormant");
        if (s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Desconocido", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Net.Status.Unknown");
        return string.IsNullOrWhiteSpace(s) ? UiLoc.Instance.T("Common.NotDetected") : s;
    }

    private static string Compact(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var n = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        n = n.Replace("Wi - Fi", "WiFi", StringComparison.OrdinalIgnoreCase)
             .Replace("Wi- Fi", "WiFi", StringComparison.OrdinalIgnoreCase)
             .Replace("Wi -Fi", "WiFi", StringComparison.OrdinalIgnoreCase)
             .Replace("Wi-Fi", "WiFi", StringComparison.OrdinalIgnoreCase);
        return n;
    }
}

public partial class HardwareViewModel : ObservableObject
{
    private readonly ScanEngine _scan;
    private readonly INavigationService _nav;

    [ObservableProperty] private SystemSnapshot? _snapshot;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _section = "summary";
    [ObservableProperty] private bool _showVirtualAdapters;
    [ObservableProperty] private string _ageText = "";
    [ObservableProperty] private string _summaryCpu = "—";
    [ObservableProperty] private string _summaryGpu = "—";
    [ObservableProperty] private string _summaryRam = "—";
    [ObservableProperty] private string _summaryStorage = "—";
    [ObservableProperty] private string _summaryBoard = "—";

    public ObservableCollection<HardwareMonitorItem> VisibleMonitors { get; } = new();
    public ObservableCollection<HardwareAdapterItem> VisibleAdapters { get; } = new();

    public bool IsSummary => Section == "summary";
    public bool IsCpu => Section == "cpu";
    public bool IsGpu => Section == "gpu";
    public bool IsMemory => Section == "memory";
    public bool IsStorage => Section == "storage";
    public bool IsBoard => Section == "board";
    public bool IsDisplays => Section == "displays";
    public bool IsNetwork => Section == "network";
    public string ThermalNote
    {
        get
        {
            if (Snapshot is null) return "";
            if (Snapshot.Thermals.CpuCelsius is not null || Snapshot.Thermals.GpuCelsius is not null)
                return "";
            var src = Snapshot.Thermals.Source ?? "";
            if (src.Contains("warming", StringComparison.OrdinalIgnoreCase))
                return UiLoc.Instance.T("Sensors.Warming");
            if (src.Equals("pending", StringComparison.OrdinalIgnoreCase))
                return UiLoc.Instance.T("Sensors.Loading");
            return UiLoc.Instance.T("Common.NotDetected");
        }
    }

    public HardwareViewModel(ScanEngine scan, INavigationService nav)
    {
        _scan = scan;
        _nav = nav;
        Status = UiLoc.Instance.T("Hardware.ReadyHint");
        UiLoc.Instance.PropertyChanged += (_, _) =>
        {
            Status = UiLoc.Instance.T("Hardware.ReadyHint");
            RefreshAge();
            RebuildMonitors();
            RebuildAdapters();
            OnPropertyChanged(nameof(ThermalNote));
        };
    }

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsSummary));
        OnPropertyChanged(nameof(IsCpu));
        OnPropertyChanged(nameof(IsGpu));
        OnPropertyChanged(nameof(IsMemory));
        OnPropertyChanged(nameof(IsStorage));
        OnPropertyChanged(nameof(IsBoard));
        OnPropertyChanged(nameof(IsDisplays));
        OnPropertyChanged(nameof(IsNetwork));
    }

    partial void OnShowVirtualAdaptersChanged(bool value) => RebuildAdapters();

    [RelayCommand]
    private void SelectSection(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s)) Section = s;
    }

    [RelayCommand]
    private async Task LoadAsync() => await LoadCoreAsync(force: true);

    [RelayCommand]
    private void OpenMonitor() => _nav.Navigate("monitor");

    [RelayCommand]
    private void CopySummary()
    {
        if (Snapshot is null) return;
        var text = BuildCopyText(Snapshot);
        try { System.Windows.Clipboard.SetText(text); Status = UiLoc.Instance.T("Hardware.Copied"); }
        catch { Status = UiLoc.Instance.T("Hardware.CopyFailed"); }
    }

    [RelayCommand]
    private void CopySection()
    {
        if (Snapshot is null) return;
        var text = Section switch
        {
            "cpu" => $"CPU: {Snapshot.Cpu.Name}\n{Snapshot.Cpu.Cores} cores / {Snapshot.Cpu.LogicalProcessors} threads",
            "gpu" => string.Join("\n", Snapshot.Gpus.Select(g => $"GPU: {g.Name}" + (g.DriverVersion is null ? "" : $" · Driver {g.DriverVersion}"))),
            "memory" => $"RAM: {FmtBytes(Snapshot.Memory.TotalBytes)} {Snapshot.Memory.MemoryType} {Snapshot.Memory.SpeedMhz:0} MHz",
            "storage" => string.Join("\n", Snapshot.Disks.Select(d => $"{d.DriveLetter} {d.Model ?? d.Name} {FmtBytes(d.TotalBytes)}")),
            "board" => $"MB: {Snapshot.Motherboard.Manufacturer} {Snapshot.Motherboard.Product}\nBIOS: {Snapshot.Bios.Vendor} {Snapshot.Bios.Version}",
            "displays" => string.Join("\n", VisibleMonitors.Select(m => $"{m.Name} {m.ScreenWidth}x{m.ScreenHeight}")),
            "network" => string.Join("\n", VisibleAdapters.Select(a => $"{a.Name} · {a.Status} · {a.IPv4}")),
            _ => BuildCopyText(Snapshot)
        };
        try { System.Windows.Clipboard.SetText(text); Status = UiLoc.Instance.T("Hardware.Copied"); }
        catch { Status = UiLoc.Instance.T("Hardware.CopyFailed"); }
    }

    public async Task LoadCoreAsync(bool force = false)
    {
        if (IsBusy) return;
        if (!force && Snapshot is not null)
        {
            RefreshAge();
            return;
        }

        IsBusy = true;
        if (HasData) Status = UiLoc.Instance.T("Hardware.Updating");
        else Status = UiLoc.Instance.T("Hardware.ScanningFast");

        var progress = new Progress<ScanProgress>(p =>
        {
            void Apply()
            {
                Progress = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Detail)) Status = p.Detail!;
                else if (!string.IsNullOrWhiteSpace(p.Stage)) Status = p.Stage;
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Apply();
            else dispatcher.Invoke(Apply);
        });

        try
        {
            var fast = await _scan.GetSnapshotAsync(ScanDepth.Fast, force: true, progress).ConfigureAwait(true);
            ApplySnapshot(fast);
            Status = UiLoc.Instance.T("Hardware.ScanningDeep");
            var deep = await _scan.GetSnapshotAsync(ScanDepth.Deep, force: true, progress).ConfigureAwait(true);
            ApplySnapshot(deep);
            var ms = deep.StageTimingsMs?.GetValueOrDefault("total", 0) ?? 0;
            Status = UiLoc.Instance.T("Hardware.ReadyMs", ms);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Hardware.Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            RefreshAge();
        }
    }

    private void ApplySnapshot(SystemSnapshot snap)
    {
        // Notificar cambio de Snapshot de forma explícita para reactivar bindings anidados
        Snapshot = snap;
        HasData = true;
        SummaryCpu = ShortName(snap.Cpu.Name);
        SummaryGpu = ShortName(snap.Gpu?.Name ?? snap.Gpus.FirstOrDefault()?.Name);
        SummaryRam = snap.Memory.TotalBytes > 0 ? FmtBytes(snap.Memory.TotalBytes) : "—";
        var totalDisk = snap.Disks.Where(d => d.TotalBytes > 0).Sum(d => (double)d.TotalBytes);
        var media = snap.Disks.Select(d => d.MediaType).FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m) && m != NotDetected.Text && !m.StartsWith("Pendiente", StringComparison.OrdinalIgnoreCase));
        SummaryStorage = totalDisk > 0
            ? $"{FmtBytes((ulong)totalDisk)}" + (string.IsNullOrWhiteSpace(media) ? "" : $" {media}")
            : "—";
        SummaryBoard = ShortName(string.IsNullOrWhiteSpace(snap.Motherboard.Product) || snap.Motherboard.Product == NotDetected.Text
            ? snap.Motherboard.Manufacturer
            : $"{snap.Motherboard.Manufacturer} {snap.Motherboard.Product}");
        RebuildMonitors();
        RebuildAdapters();
        OnPropertyChanged(nameof(ThermalNote));
        RefreshAge();
        if (string.IsNullOrWhiteSpace(Section)) Section = "summary";
        OnPropertyChanged(nameof(IsSummary));
        OnPropertyChanged(nameof(IsCpu));
        OnPropertyChanged(nameof(IsGpu));
        OnPropertyChanged(nameof(IsMemory));
        OnPropertyChanged(nameof(IsStorage));
        OnPropertyChanged(nameof(IsBoard));
        OnPropertyChanged(nameof(IsDisplays));
        OnPropertyChanged(nameof(IsNetwork));
    }

    private void RebuildMonitors()
    {
        VisibleMonitors.Clear();
        if (Snapshot is null) return;
        foreach (var m in Snapshot.Monitors)
            VisibleMonitors.Add(new HardwareMonitorItem(m));
    }

    private void RebuildAdapters()
    {
        VisibleAdapters.Clear();
        if (Snapshot is null) return;
        foreach (var a in Snapshot.NetworkAdapters)
        {
            if (!ShowVirtualAdapters && a.IsVirtual) continue;
            VisibleAdapters.Add(new HardwareAdapterItem(a));
        }
    }

    private void RefreshAge()
    {
        if (Snapshot is null)
        {
            AgeText = "";
            return;
        }
        var ago = DateTimeOffset.Now - Snapshot.CapturedAt;
        if (ago.TotalSeconds < 60)
            AgeText = UiLoc.Instance.T("Hardware.AgeSeconds", (int)Math.Max(1, ago.TotalSeconds));
        else if (ago.TotalMinutes < 60)
            AgeText = UiLoc.Instance.T("Hardware.AgeMinutes", (int)ago.TotalMinutes);
        else
            AgeText = UiLoc.Instance.T("Hardware.AgeAt", Snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss"));
    }

    private static string BuildCopyText(SystemSnapshot s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CPU: {s.Cpu.Name}");
        if (s.Gpus.Count > 0) sb.AppendLine($"GPU: {string.Join(", ", s.Gpus.Select(g => g.Name))}");
        if (s.Memory.TotalBytes > 0)
            sb.AppendLine($"RAM: {FmtBytes(s.Memory.TotalBytes)} {s.Memory.MemoryType} {(s.Memory.SpeedMhz is null ? "" : $"{s.Memory.SpeedMhz:0} MHz")}".Trim());
        foreach (var d in s.Disks.Where(d => d.TotalBytes > 0))
            sb.AppendLine($"Storage: {d.DriveLetter} {d.Model ?? d.Name} {FmtBytes(d.TotalBytes)} {d.MediaType}");
        if (s.Motherboard.Product != NotDetected.Text)
            sb.AppendLine($"Board: {s.Motherboard.Manufacturer} {s.Motherboard.Product}");
        if (s.Bios.Version != NotDetected.Text)
            sb.AppendLine($"BIOS: {s.Bios.Vendor} {s.Bios.Version}");
        return sb.ToString().Trim();
    }

    private static string ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == NotDetected.Text) return "—";
        name = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ").Trim();
        return name.Length <= 42 ? name : name[..39].TrimEnd() + "…";
    }

    private static string FmtBytes(ulong b)
        => b >= 1024UL * 1024 * 1024
            ? $"{b / (1024.0 * 1024 * 1024):0.#} GB"
            : $"{b / (1024.0 * 1024):0.#} MB";
}

public partial class ProcessRowItem : ObservableObject
{
    public ProcessInfo Info { get; }
    [ObservableProperty] private bool _isChecked;

    public ProcessRowItem(ProcessInfo info) => Info = info;

    public int Pid => Info.Pid;
    public string Name => Info.Name;
    public string Category => Info.Category.ToString();
    public double CpuPercent => Info.CpuPercent;
    public double WorkingSetMb => Info.WorkingSetMb;
    public bool HasMainWindow => Info.HasMainWindow;
    public bool IsProtected => Info.IsProtected || Info.IsCritical;
    public string? Company => Info.Company;
    public string CpuLabel => $"{Info.CpuPercent:F1}%";
    public string RamLabel => $"{Info.WorkingSetMb:F0} MB";
    public string MetaLine =>
        string.IsNullOrWhiteSpace(Info.Company)
            ? $"PID {Info.Pid}"
            : $"PID {Info.Pid} · {Info.Company}";
}

public partial class ProcessesViewModel : ObservableObject
{
    private readonly IProcessService _processes;
    private IReadOnlyList<ProcessInfo> _cache = Array.Empty<ProcessInfo>();
    private System.Windows.Threading.DispatcherTimer? _filterDebounce;
    private int _filterVersion;

    public ObservableCollection<ProcessRowItem> Items { get; } = new();

    /// <summary>Valor sentinel interno (no traducido) usado para "sin filtro" en categoría/estado.
    /// Se mantiene en inglés a propósito para que la comparación no dependa del idioma activo.</summary>
    private const string AllFilterValue = "All";

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _status = UiLoc.Instance.T("Processes.Status.Ready");
    [ObservableProperty] private string _categoryFilter = AllFilterValue;
    [ObservableProperty] private bool _onlyHighCpu;
    [ObservableProperty] private bool _onlyBackground;
    [ObservableProperty] private bool _hideProtected = true;
    [ObservableProperty] private ProcessRowItem? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private int _checkedCount;
    /// <summary>cpu_desc | cpu_asc | ram_desc | ram_asc | name</summary>
    [ObservableProperty] private string _sortKey = "cpu_desc";

    public string CpuSortLabel => SortKey switch
    {
        "cpu_asc" => "CPU ↑",
        "cpu_desc" => "CPU ↓",
        _ => "CPU"
    };

    public string RamSortLabel => SortKey switch
    {
        "ram_asc" => "RAM ↑",
        "ram_desc" => "RAM ↓",
        _ => "RAM"
    };

    public string[] CategoryFilters { get; } =
    {
        AllFilterValue, "User", "Background", "Launcher", "Updater", "Helper", "Telemetry",
        "WindowsComponent", "Security", "System", "Unknown"
    };

    private bool _statusIsDefault = true;

    public ProcessesViewModel(IProcessService processes)
    {
        _processes = processes;
        SelectionSummary = UiLoc.Instance.T("Processes.NoneMarked");
        UiLoc.Instance.PropertyChanged += (_, _) =>
        {
            if (_statusIsDefault)
            {
                _status = UiLoc.Instance.T("Processes.Status.Ready");
                OnPropertyChanged(nameof(Status));
            }
            UpdateCheckedSummary();
            if (SelectedRow is not null)
                OnSelectedRowChanged(SelectedRow);
        };
    }

    partial void OnStatusChanged(string value) => _statusIsDefault = false;

    partial void OnSortKeyChanged(string value)
    {
        OnPropertyChanged(nameof(CpuSortLabel));
        OnPropertyChanged(nameof(RamSortLabel));
        ApplyFilterFromCache();
    }

    [RelayCommand]
    private void SortByCpu()
    {
        SortKey = SortKey == "cpu_desc" ? "cpu_asc" : "cpu_desc";
    }

    [RelayCommand]
    private void SortByRam()
    {
        SortKey = SortKey == "ram_desc" ? "ram_asc" : "ram_desc";
    }

    [RelayCommand]
    private void SortCpuUp() => SortKey = "cpu_asc";

    [RelayCommand]
    private void SortCpuDown() => SortKey = "cpu_desc";

    [RelayCommand]
    private void SortRamUp() => SortKey = "ram_asc";

    [RelayCommand]
    private void SortRamDown() => SortKey = "ram_desc";


    partial void OnSelectedRowChanged(ProcessRowItem? value)
    {
        if (value is null) { Detail = ""; return; }
        var p = value.Info;
        Detail =
            $"{p.Name} · PID {p.Pid}\n" +
            $"{p.Category} · {p.Priority} · CPU {p.CpuPercent:F1}% · RAM {p.WorkingSetMb:F0} MB\n" +
            $"{(p.HasMainWindow ? UiLoc.Instance.T("Processes.Detail.WithWindow") : UiLoc.Instance.T("Processes.Detail.Background"))} · {(p.IsProtected || p.IsCritical ? UiLoc.Instance.T("Processes.Detail.Protected") : UiLoc.Instance.T("Processes.Detail.Closeable"))}\n" +
            $"{p.Path ?? UiLoc.Instance.T("Processes.Detail.NoPath")}";
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshCoreAsync();

    public async Task RefreshCoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = UiLoc.Instance.T("Processes.Status.SamplingCpu");
        try
        {
            var list = await _processes.GetProcessesAsync().ConfigureAwait(true);
            _cache = list ?? Array.Empty<ProcessInfo>();
            ApplyFilterFromCache();
            Status = UiLoc.Instance.T("Processes.Status.VisibleHint", Items.Count);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Processes.Status.ReadError", ex.Message);
            _cache = Array.Empty<ProcessInfo>();
            Items.Clear();
            UpdateCheckedSummary();
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnFilterChanged(string value) => DebounceFilter();
    partial void OnCategoryFilterChanged(string value) => ApplyFilterFromCache();
    partial void OnOnlyHighCpuChanged(bool value) => ApplyFilterFromCache();
    partial void OnOnlyBackgroundChanged(bool value) => ApplyFilterFromCache();
    partial void OnHideProtectedChanged(bool value) => ApplyFilterFromCache();

    private void DebounceFilter()
    {
        _filterDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounce.Stop();
        _filterDebounce.Tick -= OnFilterTick;
        _filterDebounce.Tick += OnFilterTick;
        _filterDebounce.Start();
    }

    private void OnFilterTick(object? sender, EventArgs e)
    {
        _filterDebounce?.Stop();
        ApplyFilterFromCache();
    }

    private void ApplyFilterFromCache()
    {
        var version = ++_filterVersion;
        var checkedPids = Items.Where(i => i.IsChecked).Select(i => i.Pid).ToHashSet();
        var filter = Filter ?? "";
        var cat = CategoryFilter;
        var hideProt = HideProtected;
        var onlyCpu = OnlyHighCpu;
        var onlyBg = OnlyBackground;

        var filtered = new List<ProcessInfo>(128);
        foreach (var p in _cache)
        {
            if (hideProt && (p.IsProtected || p.IsCritical)) continue;
            if (onlyCpu && p.CpuPercent < 5) continue;
            if (onlyBg && p.HasMainWindow) continue;
            if (cat != AllFilterValue &&
                !string.Equals(p.Category.ToString(), cat, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var hit =
                    p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (p.Company?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
                if (!hit) continue;
            }
            filtered.Add(p);
        }

        IEnumerable<ProcessInfo> ordered = SortKey switch
        {
            "cpu_asc" => filtered.OrderBy(p => p.CpuPercent).ThenBy(p => p.Name),
            "ram_desc" => filtered.OrderByDescending(p => p.WorkingSetBytes).ThenByDescending(p => p.CpuPercent),
            "ram_asc" => filtered.OrderBy(p => p.WorkingSetBytes).ThenBy(p => p.Name),
            "name" => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes)
        };

        var built = ordered.Take(250).Select(p => new ProcessRowItem(p)
        {
            IsChecked = checkedPids.Contains(p.Pid)
        }).ToList();

        if (version != _filterVersion) return;

        Items.Clear();
        foreach (var row in built)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            Items.Add(row);
        }
        UpdateCheckedSummary();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessRowItem.IsChecked))
            UpdateCheckedSummary();
    }

    private void UpdateCheckedSummary()
    {
        CheckedCount = Items.Count(i => i.IsChecked);
        SelectionSummary = CheckedCount == 0
            ? UiLoc.Instance.T("Processes.NoneMarked")
            : UiLoc.Instance.T("Processes.MarkedToClose", CheckedCount);
    }

    [RelayCommand]
    private void CheckAllVisible()
    {
        foreach (var i in Items.Where(x => !x.IsProtected))
            i.IsChecked = true;
        UpdateCheckedSummary();
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var i in Items)
            i.IsChecked = false;
        UpdateCheckedSummary();
    }

    [RelayCommand]
    private async Task CloseSelectedAsync() => await CloseSelectedCoreAsync();

    public async Task CloseSelectedCoreAsync()
    {
        var targets = Items.Where(i => i.IsChecked && !i.IsProtected).Select(i => i.Info).ToList();
        if (targets.Count == 0 && SelectedRow is not null && !SelectedRow.IsProtected)
            targets.Add(SelectedRow.Info);

        if (targets.Count == 0)
        {
            Status = UiLoc.Instance.T("Processes.Status.Select");
            AetherDialog.Info("AetherPC", Status);
            return;
        }

        if (IsBusy) return;
        if (!AetherDialog.Confirm(
                UiLoc.Instance.T("Processes.CloseConfirmTitle"),
                UiLoc.Instance.T("Processes.CloseConfirmBody",
                    targets.Count,
                    string.Join("\n", targets.Take(12).Select(t => $"• {t.Name} ({t.Pid})")) +
                    (targets.Count > 12 ? UiLoc.Instance.T("Processes.CloseConfirmMore", targets.Count - 12) : "")),
                UiLoc.Instance.T("Processes.CloseYes"),
                UiLoc.Instance.T("Common.Cancel")))
            return;

        IsBusy = true;
        var ok = 0;
        var fail = 0;
        try
        {
            foreach (var p in targets)
            {
                Status = UiLoc.Instance.T("Processes.Closing", p.Name);
                var r = await _processes.CloseGracefulAsync(p.Pid);
                if (!r.Success)
                    r = await _processes.CloseByTargetAsync(p.Path ?? p.Name, forceIfNeeded: true);
                if (r.Status == ActionApplyStatus.Skipped)
                    continue; // proceso ya no activo: omitir sin error
                if (r.Success) ok++; else fail++;
            }
            Status = fail == 0
                ? UiLoc.Instance.T("Processes.ClosedOk", ok)
                : UiLoc.Instance.T("Processes.ClosedPartial", ok, fail);
            if (fail > 0)
                AetherDialog.Warn(UiLoc.Instance.T("Dialog.Result"), Status);
            else
                AetherDialog.Success(UiLoc.Instance.T("Dialog.Result"), Status);
            IsBusy = false;
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Processes.Status.CloseError", ex.Message);
            AetherDialog.Info("AetherPC", Status);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LowerPriorityAsync() => await LowerPriorityCoreAsync();

    public async Task LowerPriorityCoreAsync()
    {
        var targets = Items.Where(i => i.IsChecked && !i.IsProtected).Select(i => i.Info).ToList();
        if (targets.Count == 0 && SelectedRow is not null && !SelectedRow.IsProtected)
            targets.Add(SelectedRow.Info);
        if (targets.Count == 0 || IsBusy) return;

        IsBusy = true;
        try
        {
            var n = 0;
            foreach (var p in targets)
            {
                var r = await _processes.SetPriorityAsync(p.Path ?? p.Name, ProcessPriorityKind.BelowNormal);
                if (r.Status == ActionApplyStatus.Skipped) continue;
                if (r.Success) n++;
            }
            Status = UiLoc.Instance.T("Processes.PriorityLowered", n);
            AetherDialog.Info("AetherPC", Status);
            IsBusy = false;
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Processes.Status.CloseError", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SuspendSelectedAsync() => await SuspendSelectedCoreAsync();

    public async Task SuspendSelectedCoreAsync()
    {
        var targets = Items.Where(i => i.IsChecked && !i.IsProtected && !i.HasMainWindow).Select(i => i.Info).ToList();
        if (targets.Count == 0)
        {
            Status = UiLoc.Instance.T("Processes.Status.SuspendHint");
            AetherDialog.Info("AetherPC", Status);
            return;
        }
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var n = 0;
            foreach (var p in targets)
            {
                var r = await _processes.SuspendAsync(p.Path ?? p.Name);
                if (r.Success) n++;
            }
            Status = UiLoc.Instance.T("Processes.Suspended", n);
            AetherDialog.Info("AetherPC", Status);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Processes.Status.CloseError", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenLocation()
    {
        var path = SelectedRow?.Info.Path;
        if (path is null || !File.Exists(path))
        {
            Status = UiLoc.Instance.T("Processes.Detail.NoPath");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Processes.Status.OpenFail", ex.Message);
        }
    }
}
