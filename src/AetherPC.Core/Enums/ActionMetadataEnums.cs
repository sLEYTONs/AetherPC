namespace AetherPC.Core.Enums;

/// <summary>En qué modo(s) puede aparecer la acción (misma ActionId compartida).</summary>
public enum OptimizationModeAffinity
{
    Both = 0,
    OptimizeOnly = 1,
    BeastOnly = 2
}

/// <summary>Nivel de impacto percibido para el usuario.</summary>
public enum ActionImpactLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>Persistencia del cambio en el sistema.</summary>
public enum ActionPersistenceType
{
    /// <summary>Solo mientras dura la sesión / proceso (p. ej. prioridad, cierre).</summary>
    Temporary = 0,
    /// <summary>Escrito en registro/servicio; reversible con token.</summary>
    PersistentReversible = 1,
    /// <summary>Necesita reinicio para aplicarse del todo.</summary>
    RequiresReboot = 2,
    /// <summary>No aplica / solo informativo.</summary>
    NotApplicable = 3
}
