using AetherPC.Core.Enums;
using AetherPC.Core.Models;

namespace AetherPC.Core.Abstractions;

public enum ScanDepth
{
    /// <summary>CPU/RAM/GPU/Disk/OS/Network + seguridad ligera (sin sensores ni extras deep).</summary>
    Fast = 0,
    /// <summary>Incluye sensores, placa, BIOS, monitores, media física (seguridad también).</summary>
    Deep = 1,
    /// <summary>Solo métricas volátiles (uso CPU/RAM) reutilizando inventario cacheado.</summary>
    Live = 2
}

public sealed class ScanProgress
{
    public string Stage { get; init; } = "";
    public double Percent { get; init; }
    public string? Detail { get; init; }
    public TimeSpan? Elapsed { get; init; }
}

public interface ISystemScanner
{
    /// <summary>Snapshot según profundidad. Live reutiliza inventario estático.</summary>
    Task<SystemSnapshot> CaptureSnapshotAsync(ScanDepth depth = ScanDepth.Fast, CancellationToken ct = default);

    /// <summary>Compat: equivalente a Fast.</summary>
    Task<SystemSnapshot> CaptureSnapshotAsync(CancellationToken ct = default)
        => CaptureSnapshotAsync(ScanDepth.Fast, ct);

    /// <summary>Solo estado de seguridad (lectura). No inventaria hardware ni sensores.</summary>
    Task<SecurityInfo> CaptureSecurityAsync(CancellationToken ct = default);
}

public interface IProcessService
{
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default);

    /// <summary>Muestrea CPU ~1–2s y genera sugerencias seguras para el plan de optimización.</summary>
    Task<IReadOnlyList<ProcessOptimizationHint>> AnalyzeForOptimizationAsync(
        SystemSnapshot snapshot,
        IReadOnlyList<StartupItem>? startupItems = null,
        CancellationToken ct = default,
        bool beastMode = false);

    Task<ActionResult> CloseGracefulAsync(int pid, CancellationToken ct = default);
    Task<ActionResult> CloseByTargetAsync(string targetKey, bool forceIfNeeded, CancellationToken ct = default);
    Task<ActionResult> SetPriorityAsync(string targetKey, ProcessPriorityKind priority, CancellationToken ct = default);
    Task<ActionResult> SuspendAsync(string targetKey, CancellationToken ct = default);
    Task<ActionResult> ResumeAsync(string targetKey, CancellationToken ct = default);
    bool IsProtected(ProcessInfo info);
}

public interface IServiceEnumerator
{
    Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default);
}

public interface IStartupService
{
    Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken ct = default);
    /// <summary>Deshabilita una entrada de inicio en HKCU\Run (no toca críticos).</summary>
    Task<ActionResult> DisableRunEntryAsync(string name, CancellationToken ct = default);
}

public interface IDriverService
{
    Task<IReadOnlyList<DriverInfo>> GetDriversAsync(CancellationToken ct = default);
}

public interface ISensorService
{
    bool IsReady { get; }
    Task WarmupAsync(CancellationToken ct = default);
    Task<ThermalInfo> ReadThermalsAsync(CancellationToken ct = default);
}

public interface ICleanupService
{
    Task<IReadOnlyList<CleanupCandidate>> ScanAsync(CancellationToken ct = default);
    Task<ActionResult> CleanAsync(IEnumerable<string> candidateIds, CancellationToken ct = default);
}

public interface IOptimizationEngine
{
    /// <summary>Plan según hardware. Un solo motor: Standard o Bestia (perfil calculado, no presets).</summary>
    Task<OptimizationPlan> BuildPlanAsync(SystemSnapshot snapshot, bool beastMode = false, CancellationToken ct = default);
    Task<OptimizationPlan> BuildPlanAsync(SystemSnapshot snapshot, OptimizationPlanKind kind, CancellationToken ct = default)
        => BuildPlanAsync(snapshot, beastMode: kind == OptimizationPlanKind.Beast, ct);
    Task<OptimizationPlan> BuildBeastModePlanAsync(SystemSnapshot snapshot, CancellationToken ct = default)
        => BuildPlanAsync(snapshot, beastMode: true, ct);

    /// <summary>
    /// Ejecuta el plan. Si selectedOnly=true, solo acciones con IsSelected.
    /// progress tipado opcional; el overload string sigue disponible vía adaptador.
    /// </summary>
    Task<OptimizationResult> ExecutePlanAsync(
        OptimizationPlan plan,
        IProgress<string>? progress = null,
        SystemSnapshot? context = null,
        CancellationToken ct = default);

    Task<OptimizationResult> ExecutePlanAsync(
        OptimizationPlan plan,
        bool selectedOnly,
        IProgress<OptimizationProgress>? progress,
        SystemSnapshot? context = null,
        CancellationToken ct = default);

    /// <summary>Aplica soft-rollback de tokens (registro/power/servicio/inicio), luego marca historial.</summary>
    Task<bool> RollbackAsync(Guid historyId, CancellationToken ct = default);
}

public interface IRestorePointService
{
    Task<(bool Success, string Message)> CreateAsync(string description, CancellationToken ct = default);
    bool IsAvailable { get; }
}

public interface IHistoryStore
{
    Task<Guid> AddAsync(HistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> ListAsync(int take = 100, CancellationToken ct = default);
    Task<HistoryEntry?> GetAsync(Guid id, CancellationToken ct = default);
    Task MarkRolledBackAsync(Guid id, CancellationToken ct = default);
}

public interface IRecommendationEngine
{
    Task<IReadOnlyList<Recommendation>> AnalyzeAsync(SystemSnapshot snapshot, UserProfile profile, CancellationToken ct = default);
}

public interface IHealthScorer
{
    (int Score, IReadOnlyList<HealthFactor> Factors) Score(SystemSnapshot snapshot);
}

public interface IBenchmarkService
{
    Task<BenchmarkResult> RunCpuAsync(CancellationToken ct = default);
    Task<BenchmarkResult> RunMemoryAsync(CancellationToken ct = default);
    Task<BenchmarkResult> RunDiskAsync(string? driveLetter = null, CancellationToken ct = default);
    Task<IReadOnlyList<BenchmarkResult>> ListHistoryAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Detección de launchers/juegos en rutas conocidas (sin escaneo agresivo de disco).</summary>
public interface IGameLibraryService
{
    Task<IReadOnlyList<LauncherInfo>> DetectLaunchersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GameLibraryEntry>> DetectGamesAsync(CancellationToken ct = default);
}

/// <summary>Diagnóstico de cuellos de botella a partir de snapshot + benches (sin inventar %).</summary>
public interface IPerformanceDiagnosis
{
    IReadOnlyList<BottleneckFinding> Analyze(SystemSnapshot snapshot, IReadOnlyList<BenchmarkResult> recentBenches);
}

public interface IAppSettingsStore
{
    Task<UserProfile> LoadProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(UserProfile profile, CancellationToken ct = default);
}

public interface IPrivilegeService
{
    bool IsElevated { get; }
    Task<bool> EnsureElevatedHintAsync(string reason);
}

public interface IEventLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>
/// Control de pantallas: enumeración, brillo HW cuando exista, color por gamma ramp (software),
/// modos oficiales Windows. Sin DDC/WMI inventados ni overlays como “brillo real”.
/// </summary>
public interface IDisplayControlService
{
    Task<IReadOnlyList<DisplayDeviceInfo>> EnumerateAsync(CancellationToken ct = default);
    Task<DisplayCapabilities> GetCapabilitiesAsync(string displayId, CancellationToken ct = default);
    Task<IReadOnlyList<DisplayModeInfo>> GetModesAsync(string displayId, CancellationToken ct = default);

    Task<ActionResult> SetHardwareBrightnessAsync(string displayId, int percent, CancellationToken ct = default);
    Task<int?> ReadHardwareBrightnessAsync(string displayId, CancellationToken ct = default);

    /// <summary>Aplica filtros de color vía SetDeviceGammaRamp. Reversible.</summary>
    Task<ActionResult> ApplySoftColorAsync(string displayId, SoftColorState state, CancellationToken ct = default);
    Task<ActionResult> ResetSoftColorAsync(string displayId, CancellationToken ct = default);
    SoftColorState? GetLastSoftColor(string displayId);
    bool HasSoftColorOverride(string displayId);

    /// <summary>Cambio temporal de modo; el llamador debe confirmar o RevertPendingModeAsync.</summary>
    Task<ActionResult> BeginModeChangeAsync(string displayId, DisplayModeInfo mode, TimeSpan previewWindow, CancellationToken ct = default);
    Task<ActionResult> ConfirmPendingModeAsync(string displayId, CancellationToken ct = default);
    Task<ActionResult> RevertPendingModeAsync(string displayId, CancellationToken ct = default);
    PendingDisplayModeChange? GetPendingMode(string displayId);

    void OpenWindowsDisplaySettings();
    void OpenWindowsHdrSettings();
    void OpenWindowsNightLightSettings();
    void OpenWindowsColorManagement();

    /// <summary>Restaura gamma de todas las pantallas tocadas (p. ej. al salir).</summary>
    void RestoreAllSoftColor();
}
