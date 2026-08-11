namespace AetherPC.Core.Enums;

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>Capa UI de riesgo (mapeada desde RiskLevel + metadatos).</summary>
public enum OptimizationRiskLayer
{
    Safe = 0,
    Recommended = 1,
    Advanced = 2,
    Experimental = 3
}

public enum ActionApplyStatus
{
    Pending = 0,
    Applied = 1,
    Failed = 2,
    Skipped = 3,
    NeedsReboot = 4,
    AlreadyApplied = 5,
    NotCompatible = 6,
    Cancelled = 7
}

public enum OptimizationPlanKind
{
    Standard = 0,
    Beast = 1
}

public enum AlertSeverity
{
    Info = 0,
    Recommendation = 1,
    Warning = 2,
    Critical = 3
}

public enum ProcessCategory
{
    System,
    WindowsComponent,
    Security,
    HardwareService,
    User,
    Background,
    Launcher,
    Updater,
    Telemetry,
    Helper,
    Disposable,
    Unknown
}

public enum ProcessPriorityKind
{
    BelowNormal,
    Normal,
    AboveNormal,
    High
}

public enum ProcessActionKind
{
    None,
    CloseGraceful,
    Suspend,
    Resume,
    SetPriorityBelowNormal,
    RestorePriority,
    DisableStartup
}

public enum FeatureAvailability
{
    Available,
    Limited,
    Incompatible,
    RequiresElevation,
    Unavailable
}

public enum OptimizationProfileKind
{
    Balanced,
    MaxPerformance,
    PowerSaver,
    Gaming,
    Work,
    Streaming,
    Development,
    Editing,
    Custom
}

/// <summary>Nivel de prioridad en el plan Bestia (orden de lista y selección).</summary>
public enum BeastPriorityTier
{
    /// <summary>Muy recomendada — marcada por defecto.</summary>
    Critical = 0,
    /// <summary>Recomendada — marcada por defecto.</summary>
    Recommended = 1,
    /// <summary>Opcional — desmarcada.</summary>
    Optional = 2,
    /// <summary>Avanzada / riesgo — desmarcada.</summary>
    Advanced = 3,
    /// <summary>Incompatible con este hardware — no aplicar.</summary>
    Incompatible = 4
}

/// <summary>Nivel de agresividad derivado del análisis de hardware (no un perfil genérico de marketing).</summary>
public enum OptimizationAggressiveness
{
    Conservative = 0,
    Balanced = 1,
    Performance = 2,
    Advanced = 3,
    /// <summary>Perfil máximo seguro calculado solo para Modo Bestia.</summary>
    Beast = 4
}

/// <summary>Tramo de RAM instalada usado para adaptar umbrales (no inventa GB).</summary>
public enum RamTier
{
    Unknown = 0,
    Gb4 = 4,
    Gb6 = 6,
    Gb8 = 8,
    Gb12 = 12,
    Gb16 = 16,
    Gb24 = 24,
    Gb32 = 32,
    Gb48 = 48,
    Gb64Plus = 64
}
