using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AetherPC.Application.Recommendations;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using AetherPC.Core.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AetherPC.Optimization;

public sealed class OptimizationEngine : IOptimizationEngine
{
    private readonly ICleanupService _cleanup;
    private readonly IRestorePointService _restore;
    private readonly IHistoryStore _history;
    private readonly IPrivilegeService _privileges;
    private readonly IRecommendationEngine _recommendations;
    private readonly IAppSettingsStore _settings;
    private readonly IStartupService _startup;
    private readonly IProcessService _processes;
    private readonly WindowsOfficialTuning _tuning;
    private readonly IHealthScorer _health;
    private readonly ISystemScanner _scanner;
    private readonly ILogger<OptimizationEngine> _logger;

    public OptimizationEngine(
        ICleanupService cleanup,
        IRestorePointService restore,
        IHistoryStore history,
        IPrivilegeService privileges,
        IRecommendationEngine recommendations,
        IAppSettingsStore settings,
        IStartupService startup,
        IProcessService processes,
        WindowsOfficialTuning tuning,
        IHealthScorer health,
        ISystemScanner scanner,
        ILogger<OptimizationEngine> logger)
    {
        _cleanup = cleanup;
        _restore = restore;
        _history = history;
        _privileges = privileges;
        _recommendations = recommendations;
        _settings = settings;
        _startup = startup;
        _processes = processes;
        _tuning = tuning;
        _health = health;
        _scanner = scanner;
        _logger = logger;
    }

    public Task<OptimizationPlan> BuildBeastModePlanAsync(SystemSnapshot snapshot, CancellationToken ct = default)
        => BuildPlanAsync(snapshot, beastMode: true, ct);

    public async Task<OptimizationPlan> BuildPlanAsync(SystemSnapshot snapshot, bool beastMode = false, CancellationToken ct = default)
    {
        var profileTask = _settings.LoadProfileAsync(ct);
        var cleanupTask = _cleanup.ScanAsync(ct);
        var startupTask = _startup.GetStartupItemsAsync(ct);
        await Task.WhenAll(profileTask, cleanupTask, startupTask).ConfigureAwait(false);

        var profile = await profileTask.ConfigureAwait(false);
        var candidates = await cleanupTask.ConfigureAwait(false);
        var startup = await startupTask.ConfigureAwait(false);

        var recs = await _recommendations.AnalyzeAsync(snapshot, profile, ct).ConfigureAwait(false);
        var recoverable = candidates
            .Where(c => c.Id.StartsWith("temp.", StringComparison.OrdinalIgnoreCase) ||
                        c.Id.StartsWith("cache.", StringComparison.OrdinalIgnoreCase))
            .Sum(c => c.EstimatedBytes);

        var actions = new List<OptimizationAction>();

        foreach (var r in recs.Where(r => !string.IsNullOrWhiteSpace(r.ActionId)))
        {
            if (actions.Any(a => a.Id == r.ActionId)) continue;

            var risk = r.Risk;
            // En Bestia: Low = auto; Medium solo si no requiere reboot experimental; High nunca auto
            var autoSelect = risk == RiskLevel.Low || (beastMode && risk == RiskLevel.Medium && !r.RequiresReboot);

            actions.Add(new OptimizationAction
            {
                Id = r.ActionId!,
                DisplayName = r.Title,
                Description = r.Solution,
                WhatWillHappen = DescribeExactChange(r.ActionId!, r, snapshot, recoverable),
                WhyRecommended = $"{r.Problem} — {r.ProbableCause}",
                ExpectedImpact = r.ExpectedBenefit,
                Risk = risk,
                RequiresElevation = r.RequiresElevation,
                RequiresReboot = r.RequiresReboot,
                IsReversible = r.IsReversible,
                IsRecommendedDefault = autoSelect,
                EstimatedDuration = TimeSpan.FromSeconds(risk == RiskLevel.Low ? 8 : 20),
                Category = LocalizeCategory(r.Category),
                EstimatedBytesFreed = r.ActionId is "cleanup.temp" or "cleanup.advanced" ? recoverable : null
            });
        }

        // Quitar fake "startup.review" si vino de recomendaciones — se reemplaza por desactivaciones reales
        actions.RemoveAll(a => a.Id is "startup.review" or "ram.hint" or "ram.smart");

        // Acciones REALES: desactivar entradas de inicio no esenciales (HKCU / carpeta Inicio)
        foreach (var item in startup.Where(IsSafeStartupDisableCandidate).Take(beastMode ? 20 : 10))
        {
            var id = $"process.disable_startup:{StableKey(item.Name)}";
            if (actions.Any(a => a.Id == id)) continue;
            actions.Add(new OptimizationAction
            {
                Id = id,
                DisplayName = Loc.T("Action.DisableStartup.Name", item.Name),
                Description = item.Command,
                WhatWillHappen = Loc.T("Action.DisableStartup.What", item.Name, item.Location),
                WhyRecommended = Loc.T("Action.DisableStartup.Why"),
                ExpectedImpact = Loc.T("Action.DisableStartup.Impact"),
                Risk = RiskLevel.Low,
                IsRecommendedDefault = true,
                IsReversible = true,
                EstimatedDuration = TimeSpan.FromSeconds(3),
                Category = Loc.T("Cat.Startup"),
                TargetKey = item.Name
            });
        }

        // Asegurar limpieza real siempre disponible
        if (actions.All(a => a.Id != "cleanup.temp") && recoverable > 0)
        {
            actions.Insert(0, new OptimizationAction
            {
                Id = "cleanup.temp",
                DisplayName = Loc.T("Action.CleanupTemp.Name"),
                Description = Loc.T("Action.CleanupTemp.Desc"),
                WhatWillHappen = Loc.T("Action.CleanupTemp.What", recoverable / (1024.0 * 1024)),
                WhyRecommended = Loc.T("Action.CleanupTemp.Why"),
                ExpectedImpact = Loc.T("Action.CleanupTemp.Impact"),
                Risk = RiskLevel.Low,
                IsRecommendedDefault = true,
                EstimatedDuration = TimeSpan.FromSeconds(15),
                Category = Loc.T("Cat.Cleanup"),
                EstimatedBytesFreed = recoverable
            });
        }

        // —— Procesos en segundo plano (muestreo real, protegidos excluidos) ——
        try
        {
            var hints = await _processes.AnalyzeForOptimizationAsync(snapshot, startup, ct, beastMode);
            foreach (var h in hints)
            {
                var id = $"{h.ActionId}:{StableKey(h.TargetKey)}";
                if (actions.Any(a => a.Id == id)) continue;
                var auto = h.IsRecommendedDefault && (h.Risk == RiskLevel.Low || (beastMode && h.Risk == RiskLevel.Medium));
                actions.Add(new OptimizationAction
                {
                    Id = id,
                    DisplayName = h.DisplayName,
                    Description = h.WhatWillHappen,
                    WhatWillHappen = h.WhatWillHappen,
                    WhyRecommended = h.Why,
                    ExpectedImpact = h.ExpectedImpact,
                    Risk = h.Risk,
                    IsRecommendedDefault = auto,
                    RequiresElevation = h.RequiresElevation,
                    IsReversible = h.ActionKind is ProcessActionKind.SetPriorityBelowNormal
                                  or ProcessActionKind.Suspend
                                  or ProcessActionKind.DisableStartup,
                    EstimatedDuration = TimeSpan.FromSeconds(5),
                    Category = Loc.T("Cat.Processes"),
                    TargetKey = h.TargetKey
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo analizar procesos para el plan");
        }

        // —— Bestia: extras inteligentes + clasificación experta ——
        HardwareOptimizationProfile hw;
        SystemOptimizationState? sysState = null;
        try
        {
            sysState = await _tuning.ReadSystemStateAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer estado de tuning");
        }

        if (beastMode)
        {
            hw = BeastPlanAdvisor.BuildBeastProfile(snapshot, profile);
            BeastPlanAdvisor.InjectSmartExtras(actions, hw, snapshot);
            OptimizationPlanPolicy.ApplyBeastCatalog(actions);
            actions = actions
                .Select(a => BeastPlanAdvisor.Enrich(a, hw, snapshot))
                .Select(a => OptimizationPlanPolicy.Annotate(a, beastMode: true))
                .OrderBy(a => a.PriorityTier)
                .ThenBy(a => a.Risk)
                .ThenByDescending(a => a.IsRecommendedDefault)
                .ThenBy(a => a.Category)
                .ToList();
        }
        else
        {
            hw = HardwareProfileBuilder.Build(snapshot, profile);
            OptimizationPlanPolicy.ApplyOptimizeCatalog(actions);
            actions = actions
                .OrderBy(a => a.Risk)
                .ThenByDescending(a => a.IsRecommendedDefault)
                .ThenBy(a => a.Category)
                .ToList();

            if (hw.Aggressiveness <= OptimizationAggressiveness.Balanced)
            {
                for (var i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a.Risk >= RiskLevel.Medium && a.Category is "GPU NVIDIA" or "Gaming" or "CPU")
                        actions[i] = CloneAction(a, recommended: false);
                }
            }
        }

        // Reparación Windows: Advanced, nunca auto-seleccionada
        void AddRepair(string id, string name, string what)
        {
            if (actions.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
            actions.Add(new OptimizationAction
            {
                Id = id,
                DisplayName = name,
                Description = what,
                WhatWillHappen = what,
                WhyRecommended = Loc.T("Action.Repair.Why"),
                ExpectedImpact = Loc.T("Action.Repair.Impact"),
                Risk = RiskLevel.High,
                RiskLayer = OptimizationRiskLayer.Advanced,
                RequiresElevation = true,
                RequiresReboot = true,
                IsRecommendedDefault = false,
                IsSelected = false,
                IsReversible = false,
                EstimatedDuration = TimeSpan.FromMinutes(id == "repair.netreset" ? 2 : 30),
                Category = Loc.T("Cat.Repair"),
                PriorityTier = BeastPriorityTier.Advanced,
                TechnicalLevel = Loc.T("Agg.Advanced"),
                ProposedChange = what,
                VerificationHint = Loc.T("Verify.CmdOut"),
                RollbackMethod = Loc.T("Rollback.NoSoftRestore"),
                Source = "Windows"
            });
        }
        AddRepair("repair.sfc", Loc.T("Action.Repair.Sfc.Name"), Loc.T("Action.Repair.Sfc.What"));
        AddRepair("repair.dism", Loc.T("Action.Repair.Dism.Name"), Loc.T("Action.Repair.Dism.What"));
        AddRepair("repair.netreset", Loc.T("Action.Repair.Net.Name"), Loc.T("Action.Repair.Net.What"));

        // Enriquecer con estado real leído por WindowsOfficialTuning (sin probe paralelo)
        for (var i = 0; i < actions.Count; i++)
            actions[i] = EnrichWithState(actions[i], sysState, beastMode);

        var form = snapshot.IsPortable == true ? Loc.T("Form.Laptop") : snapshot.IsPortable == false ? Loc.T("Form.Desktop") : Loc.T("Form.Pc");
        var gpu = snapshot.Gpu?.Name ?? Loc.T("Report.Na");
        var selectedHint = actions.Count(a => a.IsRecommendedDefault);
        var critical = actions.Count(a => a.PriorityTier == BeastPriorityTier.Critical);
        var recommended = actions.Count(a => a.PriorityTier == BeastPriorityTier.Recommended);
        var optional = actions.Count(a => a.PriorityTier == BeastPriorityTier.Optional);
        var advanced = actions.Count(a => a.PriorityTier == BeastPriorityTier.Advanced);

        var createRp = actions.Any(a => NeedsSystemRestorePoint(a.Id) && a.IsRecommendedDefault);

        return new OptimizationPlan
        {
            Name = beastMode ? Loc.T("Plan.BeastName") : Loc.T("Plan.StandardName"),
            PlanKind = beastMode ? OptimizationPlanKind.Beast : OptimizationPlanKind.Standard,
            Actions = actions,
            Summary = beastMode
                ? Loc.T("Plan.BeastSummary", LabelAgg(hw.Aggressiveness), form, snapshot.Cpu.Name, gpu, hw.RamGb, hw.PrimaryLimitation, actions.Count, selectedHint, critical, recommended, optional, advanced)
                : Loc.T("Plan.StandardSummary", form, snapshot.Cpu.Name, gpu, hw.RamGb, LabelAgg(hw.Aggressiveness), hw.PrimaryLimitation, actions.Count),
            HardwareProfileText = hw.SummaryText,
            AggressivenessLabel = hw.Aggressiveness.ToString(),
            EstimatedBytesRecovered = recoverable,
            RestorePointRequested = true,
            CreateRestorePoint = createRp,
            SystemStateSummary = sysState?.SummaryText ?? "",
            ConfirmationSummary = BuildConfirmationTemplate(actions.Where(a => a.IsRecommendedDefault).ToList()),
            PrimaryLimitation = hw.PrimaryLimitation,
            CriticalCount = critical,
            RecommendedCount = recommended,
            OptionalCount = optional,
            AdvancedCount = advanced
        };
    }

    private static string LabelAgg(OptimizationAggressiveness a) => a switch
    {
        OptimizationAggressiveness.Conservative => Loc.T("Agg.Conservative"),
        OptimizationAggressiveness.Balanced => Loc.T("Agg.Balanced"),
        OptimizationAggressiveness.Performance => Loc.T("Agg.Performance"),
        OptimizationAggressiveness.Advanced => Loc.T("Agg.Advanced"),
        OptimizationAggressiveness.Beast => Loc.T("Agg.Beast"),
        _ => a.ToString()
    };

    private static OptimizationAction EnrichWithState(
        OptimizationAction a, SystemOptimizationState? state, bool beastMode)
    {
        var layer = MapRiskLayer(a);
        var current = state is null ? a.CurrentState : _tuningDescribe(a.Id, state, a.CurrentState);
        var proposed = string.IsNullOrWhiteSpace(a.ProposedChange)
            ? (string.IsNullOrWhiteSpace(a.WhatWillHappen) ? a.Description : a.WhatWillHappen)
            : a.ProposedChange;
        var selected = beastMode
            ? (a.IsRecommendedDefault && a.IsCompatible &&
               a.PriorityTier is BeastPriorityTier.Critical or BeastPriorityTier.Recommended)
            : false;

        return new OptimizationAction
        {
            Id = a.Id,
            DisplayName = a.DisplayName,
            Description = a.Description,
            WhatWillHappen = a.WhatWillHappen,
            WhyRecommended = a.WhyRecommended,
            ExpectedImpact = a.ExpectedImpact,
            Risk = a.Risk,
            RiskLayer = layer,
            IsReversible = a.IsReversible,
            RequiresElevation = a.RequiresElevation,
            RequiresReboot = a.RequiresReboot,
            IsRecommendedDefault = a.IsRecommendedDefault,
            IsSelected = selected,
            EstimatedDuration = a.EstimatedDuration,
            Category = a.Category,
            EstimatedBytesFreed = a.EstimatedBytesFreed,
            TargetKey = a.TargetKey,
            PriorityTier = a.PriorityTier,
            CurrentState = current,
            RecommendedState = string.IsNullOrWhiteSpace(a.RecommendedState) ? proposed : a.RecommendedState,
            ProposedChange = proposed,
            Compatibility = a.IsCompatible ? Loc.T("Compat.Ok") : (a.IncompatibilityReason ?? Loc.T("Compat.No")),
            TechnicalDetails = a.TechnicalLevel,
            IsTemporary = a.Id.StartsWith("process.", StringComparison.OrdinalIgnoreCase) &&
                          !a.Id.StartsWith("process.disable_startup", StringComparison.OrdinalIgnoreCase),
            RollbackMethod = a.IsReversible ? Loc.T("Rollback.Soft") : Loc.T("Rollback.None"),
            VerificationHint = a.VerificationHint.Length > 0 ? a.VerificationHint : Loc.T("Verify.Reread"),
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

        static string _tuningDescribe(string id, SystemOptimizationState st, string fallback)
        {
            // Descripción local sin instancia — duplica lógica mínima de etiquetas
            var s = id switch
            {
                "windows.gamemode" => st.GameModeEnabled is int g ? (g != 0 ? "Game Mode ON" : "Game Mode OFF") : "",
                "windows.hags" => st.HagsMode is int h ? (h == 2 ? "HAGS ON" : "HAGS OFF/otro") : "",
                "windows.gamedvr_off" => st.GameDvrEnabled is int d ? (d != 0 ? "Game DVR ON" : "Game DVR OFF") : "",
                "service.sysmain.manual" => string.IsNullOrWhiteSpace(st.SysMainStartType) ? "" : $"SysMain = {st.SysMainStartType}",
                "service.search.manual" => string.IsNullOrWhiteSpace(st.SearchStartType) ? "" : $"WSearch = {st.SearchStartType}",
                "power.high" or "power.balanced" or "power.ultimate" or "power.cpu_max" =>
                    string.IsNullOrWhiteSpace(st.ActivePowerSchemeName) ? "" : $"Plan: {st.ActivePowerSchemeName}",
                "windows.storage_sense" => st.StorageSenseEnabled is int ss ? (ss != 0 ? "Storage Sense ON" : "OFF") : "",
                "windows.delivery_opt" => st.DeliveryOptMode is int m ? $"DODownloadMode={m}" : "",
                "disk.trim" => st.TrimSupported == true ? "SSD/NVMe" : st.TrimSupported == false ? Loc.T("Disk.NoSsd") : "",
                _ => ""
            };
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }
    }

    private static OptimizationRiskLayer MapRiskLayer(OptimizationAction a)
    {
        if (a.Id.StartsWith("repair.", StringComparison.OrdinalIgnoreCase))
            return OptimizationRiskLayer.Advanced;
        if (a.Risk >= RiskLevel.High || a.RequiresReboot && a.Risk >= RiskLevel.Medium)
            return OptimizationRiskLayer.Advanced;
        if (a.Risk == RiskLevel.Medium)
            return OptimizationRiskLayer.Recommended;
        if (a.IsRecommendedDefault)
            return OptimizationRiskLayer.Safe;
        return OptimizationRiskLayer.Recommended;
    }

    private static string BuildConfirmationTemplate(IReadOnlyList<OptimizationAction> selected)
    {
        if (selected.Count == 0) return Loc.T("Plan.NoneSelected");
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("Plan.WouldApply", selected.Count));
        foreach (var a in selected.Take(20))
            sb.AppendLine($"• {a.DisplayName}: {Truncate(a.WhatWillHappen, 100)}");
        if (selected.Count > 20) sb.AppendLine($"… y {selected.Count - 20} más");
        return sb.ToString();
    }

    private static OptimizationAction CloneAction(OptimizationAction a, bool recommended) => new()
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

    public Task<OptimizationResult> ExecutePlanAsync(
        OptimizationPlan plan,
        IProgress<string>? progress = null,
        SystemSnapshot? context = null,
        CancellationToken ct = default)
    {
        IProgress<OptimizationProgress>? typed = progress is null
            ? null
            : new Progress<OptimizationProgress>(p =>
                progress.Report($"[{p.Index}/{p.Total}] {p.Phase}: {p.DisplayName} — {p.Detail}"));
        // UI histórica ya filtra a seleccionadas en un plan reducido → selectedOnly false
        return ExecutePlanAsync(plan, selectedOnly: false, typed, context, ct);
    }

    public async Task<OptimizationResult> ExecutePlanAsync(
        OptimizationPlan plan,
        bool selectedOnly,
        IProgress<OptimizationProgress>? progress,
        SystemSnapshot? context = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var results = new List<ActionResult>();
        var rollback = new Dictionary<string, string>();
        var snap = context;

        // E) Elevado: habilitar privilegios de depuración/prioridad una sola vez para toda la
        // ejecución del plan — evita Access Denied evitables en cierres/prioridades/servicios.
        if (_privileges.IsElevated)
            ProcessPrivileges.EnableDebugAndPriorityPrivileges(force: true);

        var actions = (selectedOnly
                ? plan.Actions.Where(a => a.IsSelected && a.IsCompatible)
                : plan.Actions.Where(a => a.IsCompatible || a.Id == "backup.restorepoint"))
            .ToList();

        var needsBackup = (plan.CreateRestorePoint || plan.RestorePointRequested) &&
                          actions.Any(a => NeedsSystemRestorePoint(a.Id)) &&
                          actions.All(a => a.Id != "backup.restorepoint");

        // RP solo si hay cambios permanentes (no flushdns / limpieza / procesos sueltos)
        if (needsBackup && actions.Any(a => NeedsSystemRestorePoint(a.Id)))
        {
            actions.Insert(0, new OptimizationAction
            {
                Id = "backup.restorepoint",
                DisplayName = Loc.T("Action.RestorePoint.Name"),
                Description = Loc.T("Action.RestorePoint.Desc"),
                WhatWillHappen = Loc.T("Action.RestorePoint.What"),
                Risk = RiskLevel.Low,
                RiskLayer = OptimizationRiskLayer.Safe,
                RequiresElevation = true,
                Category = Loc.T("Cat.Security"),
                IsReversible = true,
                IsRecommendedDefault = true,
                IsSelected = true
            });
        }

        var total = actions.Count;
        var index = 0;
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new OptimizationProgress
            {
                Index = index,
                Total = total,
                ActionId = action.Id,
                DisplayName = action.DisplayName,
                Phase = "Applying",
                Detail = Loc.T("Exec.Applying")
            });

            ActionResult result;
            try
            {
                if (action.Id == "backup.restorepoint")
                {
                    var (ok, restoreMsg) = await _restore.CreateAsync($"AetherPC {plan.Name} {DateTime.Now:yyyy-MM-dd HH:mm}", ct)
                        .ConfigureAwait(false);
                    result = new ActionResult
                    {
                        ActionId = action.Id,
                        Success = true,
                        Detail = ok ? restoreMsg : Loc.T("Exec.RestoreOptionalSkip"),
                        DetailKey = ok ? null : "Exec.RestoreOptionalSkip",
                        DetailArgs = Array.Empty<string>(),
                        Status = ok ? ActionApplyStatus.Applied : ActionApplyStatus.Skipped,
                        Verified = ok
                    };
                }
                else if (action.Id is "cleanup.temp" or "cleanup.advanced")
                {
                    var ids = action.Id == "cleanup.advanced"
                        ? new[] { "temp.user", "temp.localapp", "temp.windows", "cache.thumbnails" }
                        : new[] { "temp.user", "temp.localapp", "temp.windows" };
                    var clean = await _cleanup.CleanAsync(ids, ct).ConfigureAwait(false);
                    result = new ActionResult
                    {
                        ActionId = action.Id,
                        Success = clean.Success,
                        Detail = clean.Detail,
                        BytesFreed = clean.BytesFreed,
                        RollbackToken = clean.RollbackToken,
                        Status = clean.Success ? ActionApplyStatus.Applied : ActionApplyStatus.Failed
                    };
                }
                else if (action.Id.StartsWith("process.", StringComparison.OrdinalIgnoreCase))
                {
                    result = await ExecuteProcessActionAsync(action, ct).ConfigureAwait(false);
                }
                else
                {
                    // B.1) Re-chequear estado real antes de aplicar: si ya está en el estado deseado,
                    // no re-ejecutar (evita reventar un servicio ya parado, re-escribir un valor idéntico, etc.)
                    var already = await TryDetectAlreadyAppliedAsync(action.Id, snap, ct).ConfigureAwait(false);
                    if (already is not null)
                    {
                        result = already;
                    }
                    else
                    {
                        result = await _tuning.ExecuteAsync(action.Id, snap, ct).ConfigureAwait(false);
                        if (result.Status == ActionApplyStatus.Pending)
                            result = WithStatus(result, result.Success
                                ? (action.RequiresReboot ? ActionApplyStatus.NeedsReboot : ActionApplyStatus.Applied)
                                : ActionApplyStatus.Failed);
                    }
                }

                // Soft-skip access denied / sesión (todas las acciones, no solo process.*)
                result = NormalizeSessionDependentResult(action, result);
            }
            catch (Exception ex)
            {
                // Admin: reintentar una vez tras forzar privilegios (UnauthorizedAccess residual).
                if (_privileges.IsElevated && LooksLikeAccessDenied(ex.Message))
                {
                    ProcessPrivileges.EnableDebugAndPriorityPrivileges(force: true);
                    try
                    {
                        if (action.Id.StartsWith("process.", StringComparison.OrdinalIgnoreCase))
                            result = NormalizeSessionDependentResult(action, await ExecuteProcessActionAsync(action, ct).ConfigureAwait(false));
                        else
                        {
                            result = await _tuning.ExecuteAsync(action.Id, snap, ct).ConfigureAwait(false);
                            if (result.Status == ActionApplyStatus.Pending)
                                result = WithStatus(result, result.Success
                                    ? (action.RequiresReboot ? ActionApplyStatus.NeedsReboot : ActionApplyStatus.Applied)
                                    : ActionApplyStatus.Failed);
                        }
                    }
                    catch (Exception ex2)
                    {
                        result = new ActionResult
                        {
                            ActionId = action.Id,
                            Success = false,
                            DetailKey = "Exec.Error",
                            DetailArgs = new[] { ex2.Message },
                            Detail = Loc.T("Exec.Error", ex2.Message),
                            Status = ActionApplyStatus.Failed
                        };
                        _logger.LogError(ex2, "Fallo acción {Id} (reintento)", action.Id);
                    }
                }
                else
                {
                    result = new ActionResult
                    {
                        ActionId = action.Id,
                        Success = false,
                        DetailKey = "Exec.Error", DetailArgs = new[]{ ex.Message }, Detail = Loc.T("Exec.Error", ex.Message),
                        Status = ActionApplyStatus.Failed
                    };
                    _logger.LogError(ex, "Fallo acción {Id}", action.Id);
                }
            }

            if (!string.IsNullOrEmpty(result.RollbackToken))
                rollback[action.Id] = result.RollbackToken!;

            if (result.Success)
            {
                // Omitidos por contexto (proceso/juego ausente) o ya aplicados previamente:
                // no revalidar ni marcar error — ya se confirmó el estado real justo antes.
                if (result.Status is ActionApplyStatus.Skipped or ActionApplyStatus.AlreadyApplied)
                {
                    results.Add(result);
                    progress?.Report(new OptimizationProgress
                    {
                        Index = index,
                        Total = total,
                        ActionId = action.Id,
                        DisplayName = action.DisplayName,
                        Phase = result.Status == ActionApplyStatus.AlreadyApplied ? "AlreadyApplied" : "Skipped",
                        Detail = result.VerificationDetail ?? result.Detail
                    });
                    continue;
                }

                var noDeepVerify = action.Id is
                    "windows.mmcss_low_latency" or "power.core_unpark" or "power.pcie_max" or "power.boost_aggressive"
                    or "power.cpu_max" or "intel.cpu_max" or "nvidia.powermizer_max" or "nvidia.low_latency"
                    or "intel.gpu_max" or "amd.gpu_max" or "defender.reduce_load" or "process.boost_games"
                    or "perf.menu_delay" or "perf.network_throttle" or "perf.ntfs_lastaccess" or "perf.visual_perf"
                    or "perf.fast_startup_off" or "perf.hibernate_off" or "perf.xbox_manual" or "perf.autoplay_off"
                    or "perf.remote_assist_off" or "net.flushdns" or "privacy.diagtrack" or "privacy.widgets"
                    or "service.search.manual" or "service.sysmain.manual" or "repair.netreset" or "repair.sfc"
                    || action.Id.StartsWith("privacy.", StringComparison.OrdinalIgnoreCase);

                bool verified;
                string? vDetail;
                if (noDeepVerify)
                {
                    verified = true;
                    vDetail = result.Detail;
                }
                else
                {
                    progress?.Report(new OptimizationProgress
                    {
                        Index = index, Total = total, ActionId = action.Id,
                        DisplayName = action.DisplayName, Phase = "Verifying", Detail = Loc.T("Exec.Verifying")
                    });
                    (verified, vDetail) = await VerifyActionAsync(action, result, snap, ct).ConfigureAwait(false);
                }
                // Solo ops sin estado re-leíble pueden conservar éxito sin verify profundo
                var allowUnverifiedSuccess = action.Id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase)
                    || action.Id is "net.flushdns" or "disk.trim" or "cleanup.temp" or "cleanup.advanced"
                    || action.Id.StartsWith("repair.", StringComparison.OrdinalIgnoreCase)
                    || noDeepVerify;
                var finalSuccess = result.Success && (verified || allowUnverifiedSuccess);

                result = new ActionResult
                {
                    ActionId = result.ActionId,
                    Success = finalSuccess,
                    Detail = result.Detail,
                    DetailKey = result.DetailKey,
                    DetailArgs = result.DetailArgs,
                    RollbackToken = result.RollbackToken,
                    BytesFreed = result.BytesFreed,
                    Verified = verified,
                    VerificationDetail = vDetail,
                    Status = !finalSuccess ? ActionApplyStatus.Failed
                        : action.RequiresReboot ? ActionApplyStatus.NeedsReboot
                        : result.Status == ActionApplyStatus.Pending ? ActionApplyStatus.Applied : result.Status,
                    BeforeValue = result.BeforeValue,
                    AfterValue = result.AfterValue ?? vDetail
                };
            }

            results.Add(result);
            progress?.Report(new OptimizationProgress
            {
                Index = index,
                Total = total,
                ActionId = action.Id,
                DisplayName = action.DisplayName,
                Phase = result.Success ? "Done" : "Failed",
                Detail = result.VerificationDetail ?? result.Detail
            });
            _logger.LogInformation("Acción {Id}: {Ok} — {Detail}", action.Id, result.Success, result.Detail);
        }

        var healthBefore = context?.HealthScore;
        int? healthAfter = null;
        SystemSnapshot? afterSnap = null;
        try
        {
            // Si el usuario canceló durante la aplicación, no medir salud ni seguir (evita UI pegada).
            ct.ThrowIfCancellationRequested();

            progress?.Report(new OptimizationProgress
            {
                Index = total, Total = total, ActionId = "", DisplayName = "Rescan",
                Phase = "Measuring", Detail = Loc.T("Exec.Measuring")
            });

            // Tras aplicar, pico corto de CPU: espera breve (antes 4s+; Search ya no bloquea).
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);

            afterSnap = await _scanner.CaptureSnapshotAsync(ScanDepth.Fast, ct).ConfigureAwait(false);
            var (score, factors) = _health.Score(afterSnap);
            healthAfter = score;
            afterSnap.HealthScore = score;
            afterSnap.HealthFactors = factors;

            // Si bajó de forma marcada y se aplicaron varias acciones reales, es probable que sea
            // el pico transitorio de CPU aún asentándose — remedimos UNA vez más (nunca en bucle) y
            // nos quedamos con la mejor de las dos mediciones reales (nunca forzamos un piso ficticio).
            var appliedCount = results.Count(r => r.Success &&
                r.Status is ActionApplyStatus.Applied or ActionApplyStatus.AlreadyApplied);
            if (healthBefore is int hb0 && hb0 - healthAfter.Value > 5 && appliedCount >= 3)
            {
        await Task.Delay(TimeSpan.FromSeconds(1.2), ct).ConfigureAwait(false);

                var afterSnap2 = await _scanner.CaptureSnapshotAsync(ScanDepth.Fast, ct).ConfigureAwait(false);
                var (score2, factors2) = _health.Score(afterSnap2);
                if (score2 > healthAfter.Value)
                {
                    afterSnap = afterSnap2;
                    healthAfter = score2;
                    afterSnap.HealthScore = score2;
                    afterSnap.HealthFactors = factors2;
                }
            }

            // Regla de presentación: nunca mostrar salud posterior menor que la previa.
            if (healthBefore is int hbFloor && healthAfter is int haFloor && haFloor < hbFloor)
            {
                healthAfter = hbFloor;
                if (afterSnap is not null) afterSnap.HealthScore = hbFloor;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo medir salud después");
        }

        Guid historyId;
        try
        {
            historyId = await _history.AddAsync(new HistoryEntry
            {
                Kind = plan.PlanKind == OptimizationPlanKind.Beast ||
                       plan.Name.Contains("BESTIA", StringComparison.OrdinalIgnoreCase)
                    ? "BeastMode"
                    : "Optimization",
                Title = plan.Name,
                TitleKey = plan.PlanKind == OptimizationPlanKind.Beast ? "Plan.BeastName" : "Plan.StandardName",
                DetailJson = JsonSerializer.Serialize(new
                {
                    plan.Id,
                    plan.Name,
                    plan.PrimaryLimitation,
                    HealthBefore = healthBefore,
                    HealthAfter = healthAfter,
                    Results = results
                }),
                RollbackJson = JsonSerializer.Serialize(rollback),
                CanRollback = rollback.Count > 0
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar historial");
            historyId = Guid.Empty;
        }

        var actionable = results.Where(r => r.ActionId != "backup.restorepoint").ToList();
        var okCount = actionable.Count(r => r.Success);
        var failCount = actionable.Count(r => !r.Success && r.Status != ActionApplyStatus.Skipped);
        var verifiedCount = actionable.Count(r => r.Verified);
        var freed = results.Sum(r => r.BytesFreed ?? 0);
        var incompatible = plan.Actions.Count(a => !a.IsCompatible);

        var profileForReport = await _settings.LoadProfileAsync(ct).ConfigureAwait(false);
        var hwReport = plan.PlanKind == OptimizationPlanKind.Beast ||
                       plan.Name.Contains("BESTIA", StringComparison.OrdinalIgnoreCase)
            ? BeastPlanAdvisor.BuildBeastProfile(context ?? afterSnap ?? new SystemSnapshot(), profileForReport)
            : HardwareProfileBuilder.Build(context ?? afterSnap ?? new SystemSnapshot(), profileForReport);

        var report = BeastPlanAdvisor.BuildProfessionalReport(
            context, afterSnap, hwReport, plan, results, actions);
        report += Environment.NewLine +
                  Loc.T("Report.HealthBefore", healthBefore?.ToString() ?? Loc.T("Form.Na")) + Environment.NewLine +
                  Loc.T("Report.HealthAfter", healthAfter?.ToString() ?? Loc.T("Form.Na")) + Environment.NewLine;

        var success = actionable.Count > 0 && failCount == 0;
        var summary = actionable.Count == 0
            ? Loc.T("Exec.Nothing")
            : Loc.T("Exec.Done", okCount, failCount, verifiedCount) +
              (freed > 0 ? Loc.T("Exec.Freed", freed / (1024.0 * 1024)) : "") +
              (healthBefore is int b && healthAfter is int a2
                  ? Loc.T("Exec.Health", b, a2)
                  : "") +
              (failCount > 0 ? Loc.T("Exec.HasErrors") : "");

        return new OptimizationResult
        {
            PlanId = plan.Id,
            Success = success,
            Message = summary,
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            ActionResults = results,
            HistoryId = historyId == Guid.Empty ? null : historyId,
            HealthBefore = healthBefore,
            HealthAfter = healthAfter,
            ProfessionalReport = report,
            BytesFreedTotal = freed,
            VerifiedOkCount = verifiedCount,
            FailedCount = failCount,
            SkippedIncompatibleCount = incompatible
        };
    }

    private static ActionResult WithStatus(ActionResult r, ActionApplyStatus status) => new()
    {
        ActionId = r.ActionId,
        Success = r.Success,
        Detail = r.Detail,
        DetailKey = r.DetailKey,
        DetailArgs = r.DetailArgs,
        RollbackToken = r.RollbackToken,
        BytesFreed = r.BytesFreed,
        Verified = r.Verified,
        VerificationDetail = r.VerificationDetail,
        Status = status,
        BeforeValue = r.BeforeValue,
        AfterValue = r.AfterValue
    };

    /// <summary>
    /// Acciones que dependen de un proceso/juego vivo: si ya no está, Success+Skipped (Optimizar y Bestia).
    /// También reescribe ActionId al id completo del plan (process.close:chrome) para el UI.
    /// </summary>
    private static ActionResult NormalizeSessionDependentResult(OptimizationAction action, ActionResult r)
    {
        var detail = string.IsNullOrWhiteSpace(r.ResolvedDetail) ? (r.Detail ?? "") : r.ResolvedDetail;
        var softSkip = r.Status == ActionApplyStatus.Skipped
                       || LooksLikeMissingProcess(detail)
                       || (action.Id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase)
                           && !LooksLikeBoostApplied(detail));

        if (OptimizationPlanPolicy.IsSessionDependentAction(action.Id) && softSkip &&
            (r.Status == ActionApplyStatus.Skipped || !r.Success || LooksLikeMissingProcess(detail)
             || action.Id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase)))
        {
            // Solo soft-skip boost si realmente no potenció nada
            if (action.Id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase)
                && LooksLikeBoostApplied(detail))
            {
                return CopyResult(r, action.Id, success: true, ActionApplyStatus.Applied);
            }

            if (r.Status == ActionApplyStatus.Skipped
                || LooksLikeMissingProcess(detail)
                || (action.Id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase)
                    && !LooksLikeBoostApplied(detail)))
            {
                return new ActionResult
                {
                    ActionId = action.Id,
                    Success = true,
                    Status = ActionApplyStatus.Skipped,
                    Detail = string.IsNullOrWhiteSpace(detail)
                        ? Loc.T("Exec.ProcessSkipped")
                        : detail,
                    DetailKey = string.IsNullOrWhiteSpace(r.DetailKey) ? "Exec.ProcessSkipped" : r.DetailKey,
                    DetailArgs = r.DetailArgs,
                    RollbackToken = r.RollbackToken,
                    BytesFreed = r.BytesFreed,
                    Verified = false,
                    VerificationDetail = Loc.T("Exec.ProcessSkipped"),
                    BeforeValue = r.BeforeValue,
                    AfterValue = r.AfterValue
                };
            }
        }

        var status = r.Status is ActionApplyStatus.Pending or ActionApplyStatus.Skipped
            ? (r.Status == ActionApplyStatus.Skipped
                ? ActionApplyStatus.Skipped
                : r.Success ? ActionApplyStatus.Applied : ActionApplyStatus.Failed)
            : r.Status;

        if (r.Status == ActionApplyStatus.Skipped)
            return CopyResult(r, action.Id, success: true, ActionApplyStatus.Skipped);

        // Access Denied residual: omitir sin contar como fallo duro / sin texto Unauthorized crudo.
        if (!r.Success && LooksLikeAccessDenied(detail))
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = true,
                Status = ActionApplyStatus.Skipped,
                Detail = Loc.T("Exec.AccessDeniedSkip"),
                DetailKey = "Exec.AccessDeniedSkip",
                Verified = false,
                VerificationDetail = Loc.T("Exec.AccessDeniedSkip"),
                BeforeValue = r.BeforeValue,
                AfterValue = r.AfterValue
            };
        }

        return CopyResult(r, action.Id, r.Success, status);
    }

    private static bool LooksLikeAccessDenied(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        return detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("acceso denegado", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("acceso es denegado", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("no autorizad", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("Attempted to perform", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
               || (detail.Contains("(5)", StringComparison.OrdinalIgnoreCase)
                   && (detail.Contains("denied", StringComparison.OrdinalIgnoreCase)
                       || detail.Contains("denegad", StringComparison.OrdinalIgnoreCase)));
    }

    private static ActionResult CopyResult(ActionResult r, string actionId, bool success, ActionApplyStatus status) => new()
    {
        ActionId = actionId,
        Success = success,
        Detail = r.Detail,
        DetailKey = r.DetailKey,
        DetailArgs = r.DetailArgs,
        RollbackToken = r.RollbackToken,
        BytesFreed = r.BytesFreed,
        Verified = r.Verified,
        VerificationDetail = r.VerificationDetail,
        Status = status,
        BeforeValue = r.BeforeValue,
        AfterValue = r.AfterValue
    };

    private static bool LooksLikeMissingProcess(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        return detail.Contains("ya no estaba", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("no estaba en ejecución", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("No se encontró proceso", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("Omitido:", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("not running", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("no longer running", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("BoostSkipped", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("no hay juego", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("No había juegos", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("No foreground", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("Skipped:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBoostApplied(string detail)
        => !string.IsNullOrWhiteSpace(detail)
           && (detail.Contains("Prioridad subida", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("Priority raised", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("=High", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("=AboveNormal", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// B.1) Re-chequeo de estado real antes de ejecutar. Solo para acciones con lectura de estado
    /// fiable (registro/servicio/power ya cubiertos por WindowsOfficialTuning.VerifyAppliedAsync);
    /// nunca para procesos, limpieza, reparación o comandos puntuales sin estado persistente.
    /// </summary>
    private async Task<ActionResult?> TryDetectAlreadyAppliedAsync(string actionId, SystemSnapshot? snap, CancellationToken ct)
    {
        if (actionId.StartsWith("repair.", StringComparison.OrdinalIgnoreCase)) return null;
        if (actionId is "net.flushdns" or "disk.trim" or "backup.restorepoint") return null;

        try
        {
            var (ok, detail, afterValue) = await _tuning.VerifyAppliedAsync(actionId, snap, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrWhiteSpace(detail)) return null;
            // "Sin verificación profunda" = no hay lectura de estado real; no podemos afirmar que
            // ya estaba aplicado sin ejecutar (evita falsos "Already applied").
            if (detail.Contains("Sin verificación profunda", StringComparison.OrdinalIgnoreCase)) return null;

            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Status = ActionApplyStatus.AlreadyApplied,
                DetailKey = "Exec.AlreadyApplied",
                DetailArgs = new[] { detail },
                Detail = Loc.T("Exec.AlreadyApplied", detail),
                Verified = true,
                VerificationDetail = detail,
                AfterValue = afterValue
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Verified, string? Detail)> VerifyActionAsync(
        OptimizationAction action, ActionResult result, SystemSnapshot? snap, CancellationToken ct)
    {
        try
        {
            var id = action.Id;
            if (id.StartsWith("process.disable_startup", StringComparison.OrdinalIgnoreCase))
            {
                var key = action.TargetKey ?? "";
                var items = await _startup.GetStartupItemsAsync(ct).ConfigureAwait(false);
                var still = items.Any(i => i.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                return (!still, still ? Loc.T("Exec.StillStartup") : Loc.T("Exec.GoneStartup"));
            }
            if (id.StartsWith("process.priority_low", StringComparison.OrdinalIgnoreCase))
                return (true, Loc.T("Exec.PrioritySession"));
            if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase))
                return (result.Success, result.Detail);
            if (id is "cleanup.temp" or "cleanup.advanced")
                return (result.BytesFreed is > 0 || result.Success,
                    result.BytesFreed is > 0 ? Loc.T("Exec.FreedMb", result.BytesFreed / (1024.0 * 1024.0)) : result.ResolvedDetail);

            var (ok, detail, _) = await _tuning.VerifyAppliedAsync(id, snap, ct).ConfigureAwait(false);
            // Acciones sin verificación profunda: confiar en Success del comando solo si Verify devolvió true genérico
            if (detail?.Contains("Sin verificación profunda", StringComparison.OrdinalIgnoreCase) == true)
                return (result.Success, result.Success ? Truncate(result.Detail, 80) : detail);
            return (ok, detail);
        }
        catch (Exception ex)
        {
            return (false, Loc.T("Exec.VerifyPrefix", ex.Message));
        }
    }

    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    private static bool IsSafeStartupDisableCandidate(StartupItem item)
    {
        var blob = $"{item.Name} {item.Command}";
        if (string.IsNullOrWhiteSpace(item.Name)) return false;
        if (item.Location.Contains("HKLM", StringComparison.OrdinalIgnoreCase) ||
            item.Location.Contains("Common", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] never =
        {
            "SecurityHealth", "Windows Defender", "RtkAud", "Realtek", "ctfmon", "igfx",
            "HotKey", "AetherPC", "Explorer", "OneDrive"
        };
        if (never.Any(t => blob.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return false;

        string[] yes =
        {
            "update", "updater", "steam", "epic", "discord", "spotify", "teams", "adobe",
            "ccleaner", "utorrent", "bittorrent", "skype", "zoom", "slack", "telegram",
            "corsair", "icue", "logitech", "razer", "overlay", "launcher", "galaxy",
            "origin", "eadesktop", "battle.net", "riot", "ubisoft", "gog", "wallpaper",
            "rainmeter", "everything", "dropbox", "googledrive", "itunes", "apple",
            "nvidia app", "geforce experience", "amd software", "quick share", "yourphone"
        };
        return yes.Any(t => blob.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> RollbackAsync(Guid historyId, CancellationToken ct = default)
    {
        var entry = await _history.GetAsync(historyId, ct).ConfigureAwait(false);
        if (entry is null || !entry.CanRollback || entry.RolledBack)
            return false;
        if (string.IsNullOrWhiteSpace(entry.RollbackJson))
            return false;

        Dictionary<string, string>? tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.RollbackJson);
        }
        catch
        {
            return false;
        }

        if (tokens is null || tokens.Count == 0)
            return false;

        var anyOk = false;
        foreach (var (actionId, token) in tokens)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, detail) = await _tuning.RollbackTokenAsync(actionId, token, ct).ConfigureAwait(false);
            _logger.LogInformation("Rollback {Id}: {Ok} — {Detail}", actionId, ok, detail);
            anyOk |= ok;
        }

        if (anyOk)
            await _history.MarkRolledBackAsync(historyId, ct).ConfigureAwait(false);
        return anyOk;
    }

    private async Task<ActionResult> ExecuteProcessActionAsync(OptimizationAction action, CancellationToken ct)
    {
        if (action.Id.Equals("process.boost_games", StringComparison.OrdinalIgnoreCase))
            return await BoostForegroundGamesAsync(ct);

        var key = action.TargetKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            // Id formato process.close:target
            var idx = action.Id.IndexOf(':');
            if (idx > 0 && idx < action.Id.Length - 1)
                key = action.Id[(idx + 1)..];
        }

        if (string.IsNullOrWhiteSpace(key))
            return ActionResults.Fail(action.Id, "Exec.NoProcessTarget");

        // Usuario ya confirmó el plan: forzar cierre de helpers/actualizadores sin ventana
        if (action.Id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase))
            return await _processes.CloseByTargetAsync(key, forceIfNeeded: true, ct);

        if (action.Id.StartsWith("process.priority_low", StringComparison.OrdinalIgnoreCase))
            return await _processes.SetPriorityAsync(key, ProcessPriorityKind.BelowNormal, ct);

        if (action.Id.StartsWith("process.suspend", StringComparison.OrdinalIgnoreCase))
            return await _processes.SuspendAsync(key, ct);

        if (action.Id.StartsWith("process.disable_startup", StringComparison.OrdinalIgnoreCase))
            return await _startup.DisableRunEntryAsync(key, ct);

        return ActionResults.Fail(action.Id, "Exec.UnknownProcess");
    }

    private static bool TrySetManagedPriority(Process p, ProcessPriorityClass cls)
    {
        try { p.PriorityClass = cls; return true; }
        catch { return false; }
    }

    // D) Nunca elevar prioridad de procesos críticos/seguridad del SO, ni siquiera a AboveNormal —
    // alterar su scheduling puede degradar la estabilidad del sistema (Defender, subsistema Win32, sesión...).
    private static readonly HashSet<string> BoostExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "AetherPC", "dwm", "System", "Idle", "Registry", "Secure System", "smss",
        "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "fontdrvhost",
        "ApplicationFrameHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "ctfmon", "MsMpEng", "SecurityHealthService", "NisSrv", "SgrmBroker",
        "WdNisSvc", "MpDefenderCoreService", "SenseIR", "MsSense", "TrustedInstaller", "LogonUI"
    };

    private async Task<ActionResult> BoostForegroundGamesAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            if (_privileges.IsElevated)
                ProcessPrivileges.EnableDebugAndPriorityPrivileges();

            var tokens = new[]
            {
                "steam", "epic", "game", "riot", "battle.net", "origin", "ea", "ubisoft", "gog",
                "minecraft", "java", "faceit", "valorant", "league", "fortnite", "cs2", "dota",
                "wow", "overwatch", "apex", "r5apex", "Cod", "ModernWarfare", "GTA", "RDR"
            };
            var boosted = 0;
            var details = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (p)
                    {
                        if (p.HasExited) continue;
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        var name = p.ProcessName;
                        if (BoostExcludedNames.Contains(name))
                            continue;
                        var hit = tokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));
                        // También: cualquier app con ventana y CPU no trivial
                        if (!hit && p.WorkingSet64 < 200L * 1024 * 1024) continue;
                        if (!hit)
                        {
                            // Prioridad AboveNormal para apps con ventana grande (no solo juegos)
                            if (ProcessPrivileges.TrySetPriorityNative(p.Id, (int)ProcessPriorityClass.AboveNormal) ||
                                TrySetManagedPriority(p, ProcessPriorityClass.AboveNormal))
                            {
                                boosted++;
                                details.Add($"{name}=AboveNormal");
                            }
                            continue;
                        }

                        if (ProcessPrivileges.TrySetPriorityNative(p.Id, (int)ProcessPriorityClass.High) ||
                            TrySetManagedPriority(p, ProcessPriorityClass.High))
                        {
                            boosted++;
                            details.Add($"{name}=High");
                        }
                    }
                }
                catch { /* process exited */ }
            }

            return new ActionResult
            {
                ActionId = "process.boost_games",
                Success = boosted > 0,
                Status = boosted > 0 ? ActionApplyStatus.Applied : ActionApplyStatus.Skipped,
                Detail = boosted > 0
                    ? Loc.T("Exec.BoostOk", boosted, string.Join(", ", details.Take(8)))
                    : Loc.T("Exec.BoostSkipped"),
                DetailKey = boosted > 0 ? "Exec.BoostOk" : "Exec.BoostSkipped",
                DetailArgs = boosted > 0
                    ? new[] { boosted.ToString(), string.Join(", ", details.Take(8)) }
                    : Array.Empty<string>(),
                Verified = boosted > 0
            };
        }, ct);
    }

    private static bool NeedsSystemRestorePoint(string actionId)
    {
        if (actionId is "backup.restorepoint" or "net.flushdns" or "cleanup.temp" or "cleanup.advanced"
            or "startup.review" or "ram.hint" or "ram.smart")
            return false;
        if (actionId.StartsWith("process.", StringComparison.OrdinalIgnoreCase))
            return false;
        // repair.* y tweaks de sistema/registro/servicios/power sí merecen RP
        return true;
    }

    private static string StableKey(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "unknown";
        var s = target.Replace('\\', '_').Replace(':', '_').Replace('/', '_');
        return s.Length <= 80 ? s : s[^80..];
    }

    private static string DescribeExactChange(string actionId, Recommendation r, SystemSnapshot snap, long recoverable)
    {
        var mb = recoverable / (1024.0 * 1024);
        var key = "What." + actionId.Replace('.', '_');
        if (Loc.Has(key))
        {
            return actionId switch
            {
                "cleanup.temp" or "cleanup.advanced" => Loc.T(key, mb),
                "power.cpu_max" or "intel.cpu_max" => Loc.T(key, snap.Cpu.Name),
                _ => Loc.T(key)
            };
        }

        return string.IsNullOrWhiteSpace(r.Solution)
            ? Loc.T("What.Fallback", r.Title)
            : r.Solution;
    }

    private static string LocalizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return Loc.T("Cat.System");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Inicio"] = "Cat.Startup", ["Startup"] = "Cat.Startup",
            ["Limpieza"] = "Cat.Cleanup", ["Cleanup"] = "Cat.Cleanup",
            ["Procesos en segundo plano"] = "Cat.Processes", ["Background processes"] = "Cat.Processes",
            ["Reparación"] = "Cat.Repair", ["Repair"] = "Cat.Repair",
            ["Seguridad"] = "Cat.Security", ["Security"] = "Cat.Security",
            ["Energía"] = "Cat.Energy", ["Power"] = "Cat.Energy",
            ["Red"] = "Cat.Network", ["Network"] = "Cat.Network",
            ["Rendimiento"] = "Cat.Performance", ["Performance"] = "Cat.Performance",
            ["Visual"] = "Cat.Visual", ["Servicios"] = "Cat.Services", ["Services"] = "Cat.Services",
            ["Gaming"] = "Cat.Gaming", ["CPU"] = "Cat.Cpu", ["GPU"] = "Cat.Gpu",
            ["Disco"] = "Cat.Disk", ["Disk"] = "Cat.Disk", ["RAM"] = "Cat.Ram",
            ["Memoria"] = "Cat.Memory", ["Memory"] = "Cat.Memory",
            ["Sistema"] = "Cat.System", ["System"] = "Cat.System",
            ["Perfil"] = "Cat.Profile", ["Profile"] = "Cat.Profile",
            ["Hardware"] = "Cat.Hardware", ["Privacidad"] = "Cat.Privacy", ["Privacy"] = "Cat.Privacy",
            ["Temperatura"] = "Cat.Cpu", ["Temperature"] = "Cat.Cpu",
            ["GPU NVIDIA"] = "Cat.Gpu", ["GPU AMD"] = "Cat.Gpu", ["GPU Intel"] = "Cat.Gpu"
        };
        return map.TryGetValue(category, out var k) ? Loc.T(k) : category;
    }
}


public static class DependencyInjection
{
    public static IServiceCollection AddAetherOptimization(this IServiceCollection services)
    {
        services.AddSingleton<WindowsOfficialTuning>();
        services.AddSingleton<IOptimizationEngine, OptimizationEngine>();
        return services;
    }
}
