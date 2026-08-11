using System.Diagnostics;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using Microsoft.Extensions.Logging;

namespace AetherPC.Application.Scanning;

public sealed class ScanEngine
{
    private readonly ISystemScanner _scanner;
    private readonly IHealthScorer _health;
    private readonly IRecommendationEngine _recommendations;
    private readonly IAppSettingsStore _settings;
    private readonly IHistoryStore _history;
    private readonly ILogger<ScanEngine> _logger;

    private SystemSnapshot? _cache;
    private DateTimeOffset _cacheAt;
    private ScanDepthUsed _cacheDepth;
    private readonly TimeSpan _liveTtl = TimeSpan.FromMilliseconds(800);
    private readonly TimeSpan _fastTtl = TimeSpan.FromSeconds(15);

    private CancellationTokenSource? _runningCts;

    public ScanEngine(
        ISystemScanner scanner,
        IHealthScorer health,
        IRecommendationEngine recommendations,
        IAppSettingsStore settings,
        IHistoryStore history,
        ILogger<ScanEngine> logger)
    {
        _scanner = scanner;
        _health = health;
        _recommendations = recommendations;
        _settings = settings;
        _history = history;
        _logger = logger;
    }

    public void Cancel()
    {
        try { _runningCts?.Cancel(); } catch { /* ignore */ }
    }

    public void InvalidateCache()
    {
        _cache = null;
        _cacheAt = DateTimeOffset.MinValue;
    }

    public async Task<SystemSnapshot> GetSnapshotAsync(
        ScanDepth depth = ScanDepth.Fast,
        bool force = false,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!force && _cache is not null)
        {
            var age = DateTimeOffset.Now - _cacheAt;
            if (depth == ScanDepth.Live && age < _liveTtl && _cacheDepth >= ScanDepthUsed.Fast)
            {
                // Refresca solo métricas live encima del inventario
                var live = await _scanner.CaptureSnapshotAsync(ScanDepth.Live, ct);
                return MergeLive(_cache, live);
            }
            if (depth == ScanDepth.Fast && age < _fastTtl && _cacheDepth >= ScanDepthUsed.Fast)
                return _cache;
            if (depth == ScanDepth.Deep && age < _fastTtl && _cacheDepth == ScanDepthUsed.Deep)
                return _cache;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningCts = linked;
        var token = linked.Token;
        var sw = Stopwatch.StartNew();

        progress?.Report(new ScanProgress { Stage = depth.ToString(), Percent = 5, Detail = "Iniciando…" });

        try
        {
            progress?.Report(new ScanProgress
            {
                Stage = depth.ToString(),
                Percent = 20,
                Detail = depth == ScanDepth.Deep ? "Análisis profundo…" : "Escaneo rápido…"
            });

            var snap = await _scanner.CaptureSnapshotAsync(depth, token);
            progress?.Report(new ScanProgress
            {
                Stage = depth.ToString(),
                Percent = 80,
                Detail = "Calculando salud…",
                Elapsed = sw.Elapsed
            });

            var (score, factors) = _health.Score(snap);
            snap.HealthScore = score;
            snap.HealthFactors = factors;

            _cache = snap;
            _cacheAt = DateTimeOffset.Now;
            _cacheDepth = snap.Depth;

            progress?.Report(new ScanProgress
            {
                Stage = "Done",
                Percent = 100,
                Detail = $"Completado en {sw.Elapsed.TotalSeconds:F1}s",
                Elapsed = sw.Elapsed
            });

            _logger.LogInformation("Scan {Depth} {Ms:F0}ms score={Score}", depth, sw.Elapsed.TotalMilliseconds, score);
            return snap;
        }
        finally
        {
            if (ReferenceEquals(_runningCts, linked))
                _runningCts = null;
        }
    }

    /// <summary>Solo relee mecanismos de seguridad. No ejecuta análisis completo del equipo.</summary>
    public async Task<SecurityInfo> RefreshSecurityAsync(CancellationToken ct = default)
    {
        var sec = await _scanner.CaptureSecurityAsync(ct);
        if (_cache is not null)
            _cache.Security = sec;
        return sec;
    }

    public async Task<FullAnalysisResult> RunFullAnalysisAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Análisis completo (fast→deep)");
        var started = DateTimeOffset.Now;

        progress?.Report(new ScanProgress { Stage = "Fast", Percent = 10, Detail = "Escaneo rápido…" });
        var snapshot = await GetSnapshotAsync(ScanDepth.Fast, force: true, progress, ct);

        progress?.Report(new ScanProgress { Stage = "Deep", Percent = 45, Detail = "Escaneo profundo…" });
        snapshot = await GetSnapshotAsync(ScanDepth.Deep, force: true, progress, ct);

        var profile = await _settings.LoadProfileAsync(ct);
        progress?.Report(new ScanProgress { Stage = "Recommendations", Percent = 85, Detail = "Recomendaciones…" });
        var recs = await _recommendations.AnalyzeAsync(snapshot, profile, ct);

        await _history.AddAsync(new HistoryEntry
        {
            Kind = "Scan",
            Title = Loc.T("History.FullAnalysis"),
            DetailJson = $"{{\"health\":{snapshot.HealthScore},\"recommendations\":{recs.Count},\"seconds\":{(DateTimeOffset.Now - started).TotalSeconds:F1}}}",
            CanRollback = false
        }, ct);

        return new FullAnalysisResult
        {
            Snapshot = snapshot,
            Recommendations = recs,
            Duration = DateTimeOffset.Now - started
        };
    }

    private static SystemSnapshot MergeLive(SystemSnapshot baseSnap, SystemSnapshot live)
    {
        return new SystemSnapshot
        {
            CapturedAt = live.CapturedAt,
            Depth = ScanDepthUsed.Live,
            Os = baseSnap.Os,
            Cpu = baseSnap.Cpu with { UsagePercent = live.Cpu.UsagePercent },
            Gpu = baseSnap.Gpu,
            Gpus = baseSnap.Gpus,
            Memory = live.Memory with
            {
                SpeedMhz = baseSnap.Memory.SpeedMhz,
                MemoryType = baseSnap.Memory.MemoryType,
                SlotCount = baseSnap.Memory.SlotCount
            },
            Disks = baseSnap.Disks,
            Motherboard = baseSnap.Motherboard,
            Bios = baseSnap.Bios,
            Monitors = baseSnap.Monitors,
            Network = baseSnap.Network,
            NetworkAdapters = baseSnap.NetworkAdapters,
            Security = baseSnap.Security,
            Thermals = baseSnap.Thermals,
            ProcessCount = live.ProcessCount,
            Uptime = live.Uptime,
            HealthScore = baseSnap.HealthScore,
            HealthFactors = baseSnap.HealthFactors,
            DetectionNotes = baseSnap.DetectionNotes,
            StageTimingsMs = live.StageTimingsMs,
            IsPortable = baseSnap.IsPortable
        };
    }
}

public sealed class FullAnalysisResult
{
    public required SystemSnapshot Snapshot { get; init; }
    public required IReadOnlyList<Recommendation> Recommendations { get; init; }
    public TimeSpan Duration { get; init; }
}
