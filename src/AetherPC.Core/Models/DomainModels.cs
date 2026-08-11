using AetherPC.Core.Enums;
using AetherPC.Core.Localization;

namespace AetherPC.Core.Models;

public sealed class Recommendation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = string.Empty;
    public string Problem { get; init; } = string.Empty;
    public string ProbableCause { get; init; } = string.Empty;
    public string Solution { get; init; } = string.Empty;
    public string ExpectedBenefit { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public bool IsReversible { get; init; } = true;
    public bool RequiresReboot { get; init; }
    public bool RequiresElevation { get; init; }
    public int Score { get; init; }
    public string Category { get; init; } = "General";
    public string? ActionId { get; init; }

    /// <summary>Nivel de riesgo en el idioma activo (Bajo/Medio/Alto).</summary>
    public string RiskLabel => Risk switch
    {
        RiskLevel.Low => Loc.T("Risk.Low"),
        RiskLevel.Medium => Loc.T("Risk.Medium"),
        RiskLevel.High => Loc.T("Risk.High"),
        _ => Risk.ToString()
    };

    /// <summary>Línea completa p. ej. «Riesgo: Bajo» / «Risk: Low».</summary>
    public string RiskDisplay => Loc.T("Home.ImpactLine", RiskLabel);
}

public sealed class OptimizationAction
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Texto exacto de lo que se va a hacer (para el usuario).</summary>
    public string WhatWillHappen { get; init; } = string.Empty;
    public string WhyRecommended { get; init; } = string.Empty;
    public string ExpectedImpact { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public bool IsReversible { get; init; } = true;
    public bool RequiresElevation { get; init; }
    public bool RequiresReboot { get; init; }
    /// <summary>Si el motor recomienda marcarla por defecto (seguro/recomendado).</summary>
    public bool IsRecommendedDefault { get; init; } = true;
    /// <summary>Selección de UI (mutable).</summary>
    public bool IsSelected { get; set; }
    public TimeSpan EstimatedDuration { get; init; }
    public string Category { get; init; } = string.Empty;
    public long? EstimatedBytesFreed { get; init; }
    /// <summary>Payload opcional (ruta/nombre de proceso, etc.).</summary>
    public string? TargetKey { get; init; }

    /// <summary>Prioridad Bestia / plan experto.</summary>
    public BeastPriorityTier PriorityTier { get; init; } = BeastPriorityTier.Recommended;
    public string CurrentState { get; init; } = "";
    public string RecommendedState { get; init; } = "";
    public string ProposedChange { get; init; } = "";
    public string Compatibility { get; init; } = "Compatible";
    public string TechnicalDetails { get; init; } = "";
    public bool IsTemporary { get; init; }
    public string RollbackMethod { get; init; } = "";
    public string VerificationHint { get; init; } = "";
    public string Source { get; init; } = "AetherPC";
    public OptimizationRiskLayer RiskLayer { get; init; } = OptimizationRiskLayer.Recommended;
    public bool IsCompatible { get; init; } = true;
    public string? IncompatibilityReason { get; init; }
    /// <summary>Nivel técnico: Básico / Intermedio / Avanzado.</summary>
    public string TechnicalLevel { get; init; } = "Básico";

    /// <summary>Afinidad Optimize / Bestia / ambos (misma ActionId).</summary>
    public OptimizationModeAffinity ModeAffinity { get; init; } = OptimizationModeAffinity.Both;
    public ActionImpactLevel ImpactLevel { get; init; } = ActionImpactLevel.Low;
    public ActionPersistenceType PersistenceType { get; init; } = ActionPersistenceType.PersistentReversible;
    public bool AffectsVisuals { get; init; }
    public bool AffectsBackgroundApps { get; init; }
    public bool AffectsConvenience { get; init; }
}

public sealed class OptimizationPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<OptimizationAction> Actions { get; init; } = Array.Empty<OptimizationAction>();
    public string Summary { get; init; } = string.Empty;
    /// <summary>Perfil personalizado del equipo (texto multilínea).</summary>
    public string HardwareProfileText { get; init; } = string.Empty;
    public string AggressivenessLabel { get; init; } = string.Empty;
    public long? EstimatedBytesRecovered { get; init; }
    /// <summary>Crear RP solo si hay cambios permanentes de sistema en la selección.</summary>
    public bool RestorePointRequested { get; init; } = true;
    public bool CreateRestorePoint { get; set; }
    public OptimizationPlanKind PlanKind { get; init; } = OptimizationPlanKind.Standard;
    public string ConfirmationSummary { get; init; } = "";
    public string SystemStateSummary { get; init; } = "";
    /// <summary>Limitación principal detectada (Bestia).</summary>
    public string PrimaryLimitation { get; init; } = "";
    public int CriticalCount { get; init; }
    public int RecommendedCount { get; init; }
    public int OptionalCount { get; init; }
    public int AdvancedCount { get; init; }
}

public sealed class OptimizationProgress
{
    public int Index { get; init; }
    public int Total { get; init; }
    public string ActionId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Phase { get; init; } = ""; // Applying | Verifying | Done | Skipped
    public string Detail { get; init; } = "";
    public double Percent => Total <= 0 ? 0 : Math.Clamp(100.0 * Index / Total, 0, 100);
}

public sealed class SystemOptimizationState
{
    public string? ActivePowerScheme { get; init; }
    public string? ActivePowerSchemeName { get; init; }
    public int? GameModeEnabled { get; init; }
    public int? HagsMode { get; init; }
    public int? GameDvrEnabled { get; init; }
    public string? SysMainStartType { get; init; }
    public string? SearchStartType { get; init; }
    public int? StorageSenseEnabled { get; init; }
    public int? DeliveryOptMode { get; init; }
    public bool? TrimSupported { get; init; }
    public string? PageFileDetail { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public string SummaryText { get; init; } = "";
}

public sealed class OptimizationResult
{
    public Guid PlanId { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public IReadOnlyList<ActionResult> ActionResults { get; init; } = Array.Empty<ActionResult>();
    public Guid? HistoryId { get; init; }
    public int? HealthBefore { get; init; }
    public int? HealthAfter { get; init; }
    /// <summary>Informe profesional multilínea (Bestia).</summary>
    public string ProfessionalReport { get; init; } = string.Empty;
    public long BytesFreedTotal { get; init; }
    public int VerifiedOkCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedIncompatibleCount { get; init; }
}

public sealed class ActionResult
{
    public string ActionId { get; init; } = string.Empty;
    public bool Success { get; init; }
    /// <summary>Texto legacy/técnico. Preferir DetailKey + DetailArgs para UI.</summary>
    public string Detail { get; init; } = string.Empty;
    /// <summary>Clave de localización del detalle visible (p. ej. Tune.GameModeOn).</summary>
    public string? DetailKey { get; init; }
    /// <summary>Argumentos de la plantilla DetailKey (valores técnicos, no frases).</summary>
    public string[] DetailArgs { get; init; } = Array.Empty<string>();
    public string? RollbackToken { get; init; }
    public long? BytesFreed { get; init; }
    /// <summary>Nº de archivos/elementos omitidos por estar en uso (no es un error). La UI localiza el mensaje.</summary>
    public int SkippedCount { get; init; }
    public bool Verified { get; init; }
    public string? VerificationDetail { get; init; }
    public string? VerificationDetailKey { get; init; }
    public ActionApplyStatus Status { get; init; } = ActionApplyStatus.Pending;
    public string? BeforeValue { get; init; }
    public string? AfterValue { get; init; }

    /// <summary>Detalle visible en el idioma activo.</summary>
    public string ResolvedDetail =>
        AetherPC.Core.Localization.Loc.ResolveDetail(DetailKey, DetailArgs, Detail);
}

public sealed class ProcessInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string? Description { get; init; }
    public string? Company { get; init; }
    public string? UserName { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public double WorkingSetMb => WorkingSetBytes / (1024.0 * 1024);
    public double PrivateMb => PrivateBytes / (1024.0 * 1024);
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public string Priority { get; init; } = "Normal";
    public DateTime? StartTime { get; init; }
    public bool HasMainWindow { get; init; }
    public bool Responding { get; init; } = true;
    public bool IsCritical { get; init; }
    public bool IsProtected { get; init; }
    public bool IsLikelyStartup { get; init; }
    public ProcessCategory Category { get; init; }
    public string ConsumptionPattern { get; init; } = "Instantáneo";
    public string? RecommendationReason { get; init; }
    public ProcessActionKind SuggestedAction { get; init; }
    public string RiskLabel { get; init; } = "Info";
}

/// <summary>Sugerencia de proceso para el plan de optimización (seleccionable).</summary>
public sealed class ProcessOptimizationHint
{
    public string ActionId { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty; // path o name
    public string DisplayName { get; init; } = string.Empty;
    public string WhatWillHappen { get; init; } = string.Empty;
    public string Why { get; init; } = string.Empty;
    public string ExpectedImpact { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public bool IsRecommendedDefault { get; init; }
    public bool RequiresElevation { get; init; }
    public ProcessActionKind ActionKind { get; init; }
    public int SamplePid { get; init; }
}

public sealed class ServiceInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsCritical { get; init; }
    /// <summary>PID del proceso host (0 si está parado). Varios servicios pueden compartir svchost.</summary>
    public int ProcessId { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public double WorkingSetMb => WorkingSetBytes / (1024.0 * 1024);
    public string? PathName { get; init; }
}

public sealed class StartupItem
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Impact { get; init; } = "Unknown";
    public bool Enabled { get; init; } = true;
}

public sealed class DriverInfo
{
    public string DeviceName { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? DriverVersion { get; init; }
    public DateTime? DriverDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Category { get; init; } = "Other";
    /// <summary>OK | Attention | Unknown — resumen legible para la UI.</summary>
    public string HealthLabel { get; init; } = "OK";
    public bool NeedsAttention => HealthLabel is not ("OK" or "Unknown");
}

public sealed class AlertItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AlertSeverity Severity { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string Category { get; init; } = "System";
}

public sealed class HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Código estable: BeastMode | Optimization | Cleanup | Scan …</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>Fallback legacy. Preferir TitleKey.</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>Clave de título (p. ej. Plan.BeastName). Se traduce al visualizar.</summary>
    public string TitleKey { get; init; } = string.Empty;
    public string[] TitleArgs { get; init; } = Array.Empty<string>();
    public string DetailJson { get; init; } = "{}";
    public string? RollbackJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string UserName { get; init; } = Environment.UserName;
    public bool CanRollback { get; init; }
    public bool RolledBack { get; init; }
    /// <summary>Badge «Revertible» solo si aún se puede revertir.</summary>
    public bool ShowReversible => CanRollback && !RolledBack;
    public bool IsBeastKind => Kind.Equals("BeastMode", StringComparison.OrdinalIgnoreCase);
    public bool IsOptimizeKind => Kind.Equals("Optimization", StringComparison.OrdinalIgnoreCase);

    public string ResolvedTitle
    {
        get
        {
            var key = !string.IsNullOrWhiteSpace(TitleKey) ? TitleKey : Title;
            if (AetherPC.Core.Localization.Loc.Has(key))
                return TitleArgs.Length == 0
                    ? AetherPC.Core.Localization.Loc.T(key)
                    : AetherPC.Core.Localization.Loc.T(key, TitleArgs.Cast<object>().ToArray());
            return string.IsNullOrWhiteSpace(Title) ? key : Title;
        }
    }

    public string ResolvedKind
    {
        get
        {
            var k = $"History.Kind.{Kind}";
            return AetherPC.Core.Localization.Loc.Has(k)
                ? AetherPC.Core.Localization.Loc.T(k)
                : Kind;
        }
    }
}

public sealed class CleanupCandidate
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long EstimatedBytes { get; init; }
    public RiskLevel Risk { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class BenchmarkResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Kind { get; init; } = string.Empty;
    public double Score { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class UserProfile
{
    public string UsageType { get; set; } = "General";
    public OptimizationProfileKind ActiveProfile { get; set; } = OptimizationProfileKind.Balanced;
    public bool OnboardingCompleted { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public bool TelemetryEnabled { get; set; }
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "es";
    public IList<string> ImportantApps { get; set; } = new List<string>();

    // —— Preferencias AetherPC (solo app; no tocan Windows) ——
    public string RecommendationDetail { get; set; } = "Intermediate"; // Basic | Intermediate | Full
    public bool ShowAdvancedRecommendations { get; set; } = true;
    public bool ShowSafeRecommendationsOnly { get; set; }
    public bool ShowExperimentalActions { get; set; }
    public bool ShowTechnicalExplanations { get; set; } = true;
    public bool AutoRefreshOnLaunch { get; set; }
    public bool PreferCachedAnalysis { get; set; } = true;
    public int AnalysisFreshMinutes { get; set; } = 30;
    public bool SaveAnalysisHistory { get; set; } = true;
    public bool AutoRestorePointWhenNeeded { get; set; } = true;
    public bool ConfirmBeforeApply { get; set; } = true;
    public bool ShowAdvancedWarnings { get; set; } = true;
    public bool ShowIncompatibleActions { get; set; } = true;
    public bool ShowFinishSummary { get; set; } = true;
    public bool LiveLogHiddenByDefault { get; set; } = true;
    public bool OpenLiveLogOnOptimize { get; set; }
    public bool NotifyOnOptimizeDone { get; set; } = true;
    public bool NotifyRestartPending { get; set; } = true;
    public bool NotifyNewRecommendations { get; set; } = true;
    public bool DeveloperMode { get; set; }
    public List<SecurityScoreSample> SecurityScoreHistory { get; set; } = new();

    /// <summary>Perfiles visuales de la página Monitor (solo app + gamma/brillo HW).</summary>
    public List<VisualDisplayProfile> VisualProfiles { get; set; } = new();
    public string? ActiveVisualProfileId { get; set; }
    public Dictionary<string, SoftColorState> SoftColorByDisplay { get; set; } = new();
    public DisplayAutomationSettings DisplayAutomation { get; set; } = new();

    /// <summary>Perfiles de preparación gaming por juego (solo app).</summary>
    public List<GamePrepProfile> GamePrepProfiles { get; set; } = new();
    public List<string> GamingAlwaysKeep { get; set; } = new() { "Discord", "Spotify", "chrome", "msedge", "firefox" };

    /// <summary>Historial de la última sesión Bestia activa (restaurar sin buscar en UI).</summary>
    public Guid? ActiveBeastSessionHistoryId { get; set; }
    public DateTimeOffset? BeastSessionStartedAt { get; set; }
}

public sealed class SecurityScoreSample
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public int Score { get; set; }
    public string Label { get; set; } = "";
}
