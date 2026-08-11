using AetherPC.Core.Enums;
using AetherPC.Core.Models;

namespace AetherPC.Optimization;

/// <summary>
/// Política de catálogo Optimize vs Bestia (mismo ActionId, distinta afinidad/selección).
/// </summary>
public static class OptimizationPlanPolicy
{
    public static bool IsVisualSurfaceAction(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return id is
            "perf.visual_perf" or
            "perf.transparency_off" or
            "perf.animations_off" or
            "perf.menu_delay" or
            "privacy.widgets" or
            "privacy.tips" or
            "privacy.copilot" or
            "windows.gamedvr_off";
    }

    public static bool IsBeastOnlyAction(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (IsVisualSurfaceAction(id)) return true;
        if (id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("defender.reduce_load", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("service.search.manual", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("power.ultimate", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("power.boost_aggressive", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("process.priority", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("process.suspend", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsSessionDependentAction(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("process.priority", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("process.suspend", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Optimizar: mantenimiento seguro — sin visuales, sin cierres, sin Search/Defender agresivo.</summary>
    public static void ApplyOptimizeCatalog(List<OptimizationAction> actions)
    {
        actions.RemoveAll(a => IsBeastOnlyAction(a.Id));

        for (var i = 0; i < actions.Count; i++)
        {
            var a = Annotate(actions[i], beastMode: false);
            // Optimizar: solo Low auto-recomendable; Medium queda opcional desmarcado
            if (a.Risk >= RiskLevel.Medium && a.IsRecommendedDefault)
                a = WithRecommended(a, false);
            if (a.RequiresReboot && a.IsRecommendedDefault)
                a = WithRecommended(a, false);
            actions[i] = a;
        }
    }

    /// <summary>Bestia: anota metadatos; Search no auto-seleccionado.</summary>
    public static void ApplyBeastCatalog(List<OptimizationAction> actions)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            var a = Annotate(actions[i], beastMode: true);
            if (a.Id.Equals("service.search.manual", StringComparison.OrdinalIgnoreCase))
                a = WithRecommended(a, false); // seleccionable, no por defecto
            actions[i] = a;
        }
    }

    public static OptimizationAction Annotate(OptimizationAction a, bool beastMode)
    {
        var id = a.Id ?? "";
        var visual = IsVisualSurfaceAction(id);
        var session = IsSessionDependentAction(id);
        var beastOnly = IsBeastOnlyAction(id);
        var affinity = beastOnly
            ? OptimizationModeAffinity.BeastOnly
            : OptimizationModeAffinity.Both;

        var persistence = a.RequiresReboot
            ? ActionPersistenceType.RequiresReboot
            : session
                ? ActionPersistenceType.Temporary
                : a.IsReversible
                    ? ActionPersistenceType.PersistentReversible
                    : ActionPersistenceType.NotApplicable;

        var impact = a.Risk switch
        {
            RiskLevel.High => ActionImpactLevel.High,
            RiskLevel.Medium => ActionImpactLevel.Medium,
            _ when visual || session || id is "defender.reduce_load" or "service.search.manual"
                => ActionImpactLevel.Medium,
            _ => ActionImpactLevel.Low
        };

        var affectsBg = session
            || id.StartsWith("privacy.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("service.", StringComparison.OrdinalIgnoreCase)
            || id is "defender.reduce_load";

        var affectsConv = visual
            || id is "service.search.manual" or "defender.reduce_load"
            || id.StartsWith("privacy.widgets", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("privacy.tips", StringComparison.OrdinalIgnoreCase);

        // Categoría UI Bestia: rendimiento visual agrupado
        var category = a.Category;
        if (beastMode && visual)
            category = string.IsNullOrWhiteSpace(category) || category.Contains("Visual", StringComparison.OrdinalIgnoreCase)
                ? category
                : category; // BeastPlanAdvisor ya usa Cat.VisualBeast

        return new OptimizationAction
        {
            Id = a.Id ?? "",
            DisplayName = a.DisplayName,
            Description = a.Description,
            WhatWillHappen = a.WhatWillHappen,
            WhyRecommended = a.WhyRecommended,
            ExpectedImpact = a.ExpectedImpact,
            Risk = a.Risk,
            RiskLayer = a.RiskLayer,
            IsReversible = a.IsReversible,
            RequiresElevation = a.RequiresElevation,
            RequiresReboot = a.RequiresReboot,
            IsRecommendedDefault = a.IsRecommendedDefault,
            IsSelected = a.IsSelected,
            EstimatedDuration = a.EstimatedDuration,
            Category = category,
            EstimatedBytesFreed = a.EstimatedBytesFreed,
            TargetKey = a.TargetKey,
            PriorityTier = a.PriorityTier,
            CurrentState = a.CurrentState,
            RecommendedState = a.RecommendedState,
            ProposedChange = a.ProposedChange,
            Compatibility = a.Compatibility,
            TechnicalDetails = a.TechnicalDetails,
            IsTemporary = session || persistence == ActionPersistenceType.Temporary,
            RollbackMethod = a.RollbackMethod,
            VerificationHint = a.VerificationHint,
            Source = a.Source,
            IsCompatible = a.IsCompatible,
            IncompatibilityReason = a.IncompatibilityReason,
            TechnicalLevel = a.TechnicalLevel,
            ModeAffinity = affinity,
            ImpactLevel = impact,
            PersistenceType = persistence,
            AffectsVisuals = visual,
            AffectsBackgroundApps = affectsBg,
            AffectsConvenience = affectsConv
        };
    }

    // D) Mismo criterio de exclusión que OptimizationEngine.BoostForegroundGamesAsync — un proceso
    // crítico/de seguridad del SO nunca debe considerarse candidato a boost de prioridad.
    private static readonly HashSet<string> BoostExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "AetherPC", "dwm", "ApplicationFrameHost", "ShellExperienceHost",
        "System", "Idle", "Registry", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "MsMpEng", "SecurityHealthService", "NisSrv", "SgrmBroker",
        "TrustedInstaller", "LogonUI"
    };

    public static bool HasForegroundBoostTargets()
    {
        var tokens = new[]
        {
            "steam", "epic", "game", "riot", "battle.net", "origin", "ea", "ubisoft", "gog",
            "minecraft", "java", "faceit", "valorant", "league", "fortnite", "cs2", "dota",
            "wow", "overwatch", "apex", "r5apex", "Cod", "ModernWarfare", "GTA", "RDR"
        };

        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    using (p)
                    {
                        if (p.HasExited) continue;
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        var name = p.ProcessName;
                        if (BoostExcludedNames.Contains(name))
                            continue;

                        if (tokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                            return true;
                        if (p.WorkingSet64 >= 350L * 1024 * 1024)
                            return true;
                    }
                }
                catch { /* */ }
            }
        }
        catch { /* */ }

        return false;
    }

    private static OptimizationAction WithRecommended(OptimizationAction a, bool recommended) => new()
    {
        Id = a.Id,
        DisplayName = a.DisplayName,
        Description = a.Description,
        WhatWillHappen = a.WhatWillHappen,
        WhyRecommended = a.WhyRecommended,
        ExpectedImpact = a.ExpectedImpact,
        Risk = a.Risk,
        RiskLayer = a.RiskLayer,
        IsReversible = a.IsReversible,
        RequiresElevation = a.RequiresElevation,
        RequiresReboot = a.RequiresReboot,
        IsRecommendedDefault = recommended,
        IsSelected = a.IsSelected,
        EstimatedDuration = a.EstimatedDuration,
        Category = a.Category,
        EstimatedBytesFreed = a.EstimatedBytesFreed,
        TargetKey = a.TargetKey,
        PriorityTier = a.PriorityTier,
        CurrentState = a.CurrentState,
        RecommendedState = a.RecommendedState,
        ProposedChange = a.ProposedChange,
        Compatibility = a.Compatibility,
        TechnicalDetails = a.TechnicalDetails,
        IsTemporary = a.IsTemporary,
        RollbackMethod = a.RollbackMethod,
        VerificationHint = a.VerificationHint,
        Source = a.Source,
        IsCompatible = a.IsCompatible,
        IncompatibilityReason = a.IncompatibilityReason,
        TechnicalLevel = a.TechnicalLevel,
        ModeAffinity = a.ModeAffinity,
        ImpactLevel = a.ImpactLevel,
        PersistenceType = a.PersistenceType,
        AffectsVisuals = a.AffectsVisuals,
        AffectsBackgroundApps = a.AffectsBackgroundApps,
        AffectsConvenience = a.AffectsConvenience
    };
}
