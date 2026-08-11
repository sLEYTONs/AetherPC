using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AetherPC.App.Services;
using AetherPC.Application.Scanning;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherPC.App.ViewModels;

public sealed class TrendBar
{
    public double BarHeight { get; init; }
    public string Tip { get; init; } = "";
}

/// <summary>UI: Diagnóstico de rendimiento (antes Benchmarks). Mantiene IBenchmarkService.</summary>
public partial class BenchmarkViewModel : ObservableObject
{
    private readonly IBenchmarkService _bench;
    private readonly ScanEngine _scan;
    private readonly IPerformanceDiagnosis _diag;
    private CancellationTokenSource? _cts;
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _cpuTimesPrimed;

    public ObservableCollection<BenchmarkResult> History { get; } = new();
    public ObservableCollection<BottleneckFinding> Bottlenecks { get; } = new();
    public ObservableCollection<TrendBar> CpuTrend { get; } = new();
    public ObservableCollection<TrendBar> RamTrend { get; } = new();
    public ObservableCollection<TrendBar> DiskTrend { get; } = new();

    [ObservableProperty] private string _section = "summary";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _primaryLimit = "";
    [ObservableProperty] private string _secondaryLimit = "";
    [ObservableProperty] private string _lastCpu = "—";
    [ObservableProperty] private string _lastRam = "—";
    [ObservableProperty] private string _lastDisk = "—";
    [ObservableProperty] private string _lastCpuShort = "—";
    [ObservableProperty] private string _lastRamShort = "—";
    [ObservableProperty] private string _lastDiskShort = "—";
    [ObservableProperty] private string _compareText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _liveCpu;
    [ObservableProperty] private double _liveRam;
    [ObservableProperty] private double _liveProgress;
    [ObservableProperty] private bool _liveIndeterminate = true;
    [ObservableProperty] private string _liveCpuText = "—";
    [ObservableProperty] private string _liveRamText = "—";
    [ObservableProperty] private string _liveGpuText = "—";
    [ObservableProperty] private string _liveDetail = "";
    [ObservableProperty] private string _lastRunSummary = "";
    [ObservableProperty] private bool _hasLastRun;
    [ObservableProperty] private bool _hasCpuTrend;
    [ObservableProperty] private bool _hasRamTrend;
    [ObservableProperty] private bool _hasDiskTrend;
    [ObservableProperty] private string _cpuTrendEmpty = "";
    [ObservableProperty] private string _ramTrendEmpty = "";
    [ObservableProperty] private string _diskTrendEmpty = "";

    public bool IsSummary => Section == "summary";
    public bool IsBottlenecks => Section == "bottlenecks";
    public bool IsTests => Section == "tests";
    public bool IsCompare => Section == "compare";

    public BenchmarkViewModel(IBenchmarkService bench, ScanEngine scan, IPerformanceDiagnosis diag)
    {
        _bench = bench;
        _scan = scan;
        _diag = diag;
        Status = UiLoc.Instance.T("Diag.Blurb");
        var empty = UiLoc.Instance.T("Diag.TrendEmpty");
        CpuTrendEmpty = empty;
        RamTrendEmpty = empty;
        DiskTrendEmpty = empty;
    }

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsSummary));
        OnPropertyChanged(nameof(IsBottlenecks));
        OnPropertyChanged(nameof(IsTests));
        OnPropertyChanged(nameof(IsCompare));
    }

    [RelayCommand]
    private void SelectSection(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s)) Section = s;
    }

    [RelayCommand]
    private async Task LoadAsync() => await LoadCoreAsync();

    public async Task LoadCoreAsync()
    {
        try
        {
            var list = await _bench.ListHistoryAsync();
            History.Clear();
            foreach (var h in list) History.Add(h);
            RefreshTrends(list);
            UpdateLastLabels(list);
            BuildCompare(list);

            var snap = await _scan.GetSnapshotAsync(ScanDepth.Fast, force: false);
            var findings = _diag.Analyze(snap, list.Take(20).ToList());
            Bottlenecks.Clear();
            foreach (var f in findings) Bottlenecks.Add(f);
            PrimaryLimit = findings.Count > 0
                ? UiLoc.Instance.T(findings[0].TitleKey) + " — " + findings[0].Evidence
                : UiLoc.Instance.T("Diag.Bottle.None");
            SecondaryLimit = findings.Count > 1
                ? UiLoc.Instance.T(findings[1].TitleKey) + " — " + findings[1].Evidence
                : "";
            SummaryText = UiLoc.Instance.T("Diag.SummaryLine",
                snap.Cpu.Name,
                $"{snap.Memory.TotalBytes / (1024.0 * 1024 * 1024):F0} GB",
                snap.Gpu?.Name ?? "—");
            LiveGpuText = snap.Gpu?.Name ?? UiLoc.Instance.T("Diag.GpuUnavailable");
            Status = History.Count == 0
                ? UiLoc.Instance.T("Diag.NoHistory")
                : UiLoc.Instance.T("Diag.HistoryCount", History.Count);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Diag.Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RunCpuAsync() => await RunSafe(() => _bench.RunCpuAsync(_cts!.Token), "CPU");

    [RelayCommand]
    private async Task RunRamAsync() => await RunSafe(() => _bench.RunMemoryAsync(_cts!.Token), "RAM");

    [RelayCommand]
    private async Task RunDiskAsync() => await RunSafe(() => _bench.RunDiskAsync(null, _cts!.Token), "Disk");

    [RelayCommand]
    private async Task RunAllAsync()
    {
        if (Busy) return;
        if (!AetherDialog.Confirm(
                UiLoc.Instance.T("Diag.RunAllTitle"),
                UiLoc.Instance.T("Diag.RunAllBody")))
            return;
        await RunCpuAsync();
        if (_cts?.IsCancellationRequested == true) return;
        await RunRamAsync();
        if (_cts?.IsCancellationRequested == true) return;
        await RunDiskAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* */ }
        ProgressText = UiLoc.Instance.T("Diag.Cancelling");
    }

    private async Task RunSafe(Func<Task<BenchmarkResult>> action, string label)
    {
        if (Busy) return;
        Busy = true;
        _cts = new CancellationTokenSource();
        LiveIndeterminate = true;
        LiveProgress = 0;
        LiveDetail = UiLoc.Instance.T("Diag.Running", label);
        ProgressText = LiveDetail;
        Status = ProgressText;
        Section = "tests";
        _cpuTimesPrimed = false;
        var meterCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var meterTask = SampleLiveMetersAsync(label, meterCts.Token);
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var r = await Task.Run(async () => await action().ConfigureAwait(false)).ConfigureAwait(true);
            History.Insert(0, r);
            while (History.Count > 100) History.RemoveAt(History.Count - 1);
            Status = $"{r.Kind}: {r.Score} {r.Unit}";
            ProgressText = Status;
            LastRunSummary = $"{r.Kind}: {r.Score} {r.Unit}\n{r.Details}";
            HasLastRun = true;
            LiveProgress = 100;
            LiveIndeterminate = false;
            LiveDetail = LastRunSummary;
            await LoadCoreAsync();
        }
        catch (OperationCanceledException)
        {
            Status = UiLoc.Instance.T("Diag.Cancelled");
            ProgressText = Status;
            LiveDetail = Status;
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Diag.Error", ex.Message);
            ProgressText = Status;
            LiveDetail = Status;
        }
        finally
        {
            try { meterCts.Cancel(); } catch { /* */ }
            try { await meterTask.ConfigureAwait(true); } catch { /* */ }
            meterCts.Dispose();
            Busy = false;
            _cts = null;
        }
    }

    private async Task SampleLiveMetersAsync(string label, CancellationToken ct)
    {
        try
        {
            // Primera lectura GetSystemTimes siempre es 0 — cebar antes del bucle.
            _ = ReadSystemCpuPercent();
            try { await Task.Delay(350, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }

            var started = Environment.TickCount64;
            double lastCpu = 0;
            while (!ct.IsCancellationRequested)
            {
                double cpu = ReadSystemCpuPercent();
                if (cpu < 0.5)
                {
                    // Segunda muestra inmediata si la primera quedó baja
                    try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    var again = ReadSystemCpuPercent();
                    if (again > cpu) cpu = again;
                }
                if (cpu < 0.05 && lastCpu > 0.5)
                    cpu = lastCpu;
                lastCpu = cpu;

                double ramPct = 0;
                string ramText = "—";
                try
                {
                    var snap = await _scan.GetSnapshotAsync(ScanDepth.Live, force: false).ConfigureAwait(false);
                    // Preferir CPU del scanner si GetSystemTimes falló (cpu≈0) y el snap trae valor
                    if (cpu < 0.5 && snap.Cpu.UsagePercent > cpu)
                        cpu = snap.Cpu.UsagePercent;
                    ramPct = snap.Memory.UsagePercent;
                    ramText = $"{snap.Memory.UsagePercent:F0}% · {snap.Memory.UsedBytes / (1024.0 * 1024 * 1024):F1}/{snap.Memory.TotalBytes / (1024.0 * 1024 * 1024):F0} GB";
                    if (!string.IsNullOrWhiteSpace(snap.Gpu?.Name))
                        LiveGpuText = snap.Gpu.Name;
                }
                catch { /* */ }

                var elapsed = Environment.TickCount64 - started;
                var approx = label switch
                {
                    "CPU" => Math.Min(95, elapsed / 25.0),
                    "RAM" => Math.Min(95, elapsed / 40.0),
                    _ => Math.Min(95, elapsed / 80.0)
                };

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                var cpuCopy = Math.Clamp(cpu, 0, 100);
                var ramCopy = ramPct;
                var ramTextCopy = ramText;
                void Apply()
                {
                    LiveCpu = cpuCopy;
                    LiveCpuText = $"{cpuCopy:F0}%";
                    LiveRam = ramCopy;
                    LiveRamText = ramTextCopy;
                    LiveProgress = approx;
                    LiveIndeterminate = false;
                    LiveDetail = UiLoc.Instance.T("Diag.LiveDetail", label, $"{cpuCopy:F0}%", ramTextCopy);
                }
                if (dispatcher is null || dispatcher.CheckAccess()) Apply();
                else await dispatcher.InvokeAsync(Apply);

                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch { /* ignore meter errors */ }
    }

    /// <summary>CPU% vía GetSystemTimes — fiable en ES/EN y sin PerformanceCounter.</summary>
    private double ReadSystemCpuPercent()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return 0;

            var idleL = FileTimeToLong(idle);
            var kernelL = FileTimeToLong(kernel);
            var userL = FileTimeToLong(user);

            if (!_cpuTimesPrimed)
            {
                _prevIdle = idleL;
                _prevKernel = kernelL;
                _prevUser = userL;
                _cpuTimesPrimed = true;
                return 0; // primera muestra no es válida (igual que PerformanceCounter)
            }

            var idleDelta = idleL - _prevIdle;
            var kernelDelta = kernelL - _prevKernel;
            var userDelta = userL - _prevUser;
            _prevIdle = idleL;
            _prevKernel = kernelL;
            _prevUser = userL;

            var total = kernelDelta + userDelta;
            if (total <= 0) return 0;
            // kernel incluye idle en Windows
            var busy = total - idleDelta;
            if (busy < 0) busy = 0;
            return Math.Clamp(100.0 * busy / total, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private static long FileTimeToLong(FileTime ft)
        => ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    private void RefreshTrends(IReadOnlyList<BenchmarkResult> list)
    {
        const double chartH = 72;

        void Fill(ObservableCollection<TrendBar> col, string kind, Action<bool> setHas)
        {
            col.Clear();
            var items = list
                .Where(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && x.Score > 0)
                .Take(16)
                .Reverse()
                .ToList();
            setHas(items.Count > 0);
            if (items.Count == 0) return;

            var max = items.Max(x => x.Score);
            if (max <= 0) max = 1;
            foreach (var b in items)
            {
                var h = Math.Max(4, b.Score / max * chartH);
                col.Add(new TrendBar
                {
                    BarHeight = h,
                    Tip = $"{b.Score:0.##} {b.Unit} · {b.CreatedAt:g}"
                });
            }
        }

        Fill(CpuTrend, "CPU", v => HasCpuTrend = v);
        Fill(RamTrend, "RAM", v => HasRamTrend = v);
        Fill(DiskTrend, "Disk", v => HasDiskTrend = v);

        var empty = UiLoc.Instance.T("Diag.TrendEmpty");
        CpuTrendEmpty = empty;
        RamTrendEmpty = empty;
        DiskTrendEmpty = empty;
    }

    private void UpdateLastLabels(IReadOnlyList<BenchmarkResult> list)
    {
        string Fmt(string kind)
        {
            var b = list.FirstOrDefault(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && x.Score > 0);
            return b is null ? "—" : $"{b.Score} {b.Unit} ({b.CreatedAt:g})";
        }
        string Short(string kind)
        {
            var b = list.FirstOrDefault(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && x.Score > 0);
            return b is null ? "—" : $"{b.Score:0.#}";
        }
        LastCpu = Fmt("CPU");
        LastRam = Fmt("RAM");
        LastDisk = Fmt("Disk");
        LastCpuShort = Short("CPU");
        LastRamShort = Short("RAM");
        LastDiskShort = Short("Disk");
    }

    private void BuildCompare(IReadOnlyList<BenchmarkResult> list)
    {
        string Diff(string kind)
        {
            var items = list
                .Where(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && x.Score > 0)
                .Take(2)
                .ToList();
            if (items.Count == 0) return UiLoc.Instance.T("Diag.CompareNeedTwo", kind);
            if (items.Count == 1) return UiLoc.Instance.T("Diag.CompareOnlyOne", kind);
            var newer = items[0];
            var older = items[1];
            var delta = newer.Score - older.Score;
            var sign = delta >= 0 ? "+" : "";
            return UiLoc.Instance.T("Diag.CompareLine", kind, older.Score, newer.Score, $"{sign}{delta:F1}", newer.Unit);
        }
        CompareText = Diff("CPU") + "\n" + Diff("RAM") + "\n" + Diff("Disk");
    }
}
