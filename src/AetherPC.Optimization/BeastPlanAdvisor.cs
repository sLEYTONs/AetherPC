using AetherPC.Application.Recommendations;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;

namespace AetherPC.Optimization;

/// <summary>
/// Asesor experto del Modo Bestia: decide prioridad, compatibilidad y selección
/// según hardware/estado real. No es un segundo motor — solo clasifica acciones
/// del plan ya construido por OptimizationEngine.
/// </summary>
internal static class BeastPlanAdvisor
{
    public static HardwareOptimizationProfile BuildBeastProfile(SystemSnapshot snapshot, UserProfile profile)
    {
        var baseProfile = HardwareProfileBuilder.Build(snapshot, profile);
        var ramGb = baseProfile.RamGb;
        var tier = baseProfile.RamTier;
        var hasSsd = snapshot.Disks.Any(d =>
            d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var hasHdd = snapshot.Disks.Any(d => d.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase));
        var hasNvme = snapshot.Disks.Any(d => d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var isLaptop = snapshot.IsPortable == true;
        var isDesktop = snapshot.IsPortable == false;
        var pressure = (snapshot.Memory.CommitUsagePercent is >= 85) ||
                       (snapshot.Memory.UsagePercent >= 90 && tier <= RamTier.Gb12);
        var processHeavy = snapshot.ProcessCount > 200;
        var cool = snapshot.Thermals.CpuCelsius is null or < 85;

        // Bestia: perfil calculado (no botón fijo)
        var beastAgg = DeriveBeastAggressiveness(tier, pressure, isLaptop, isDesktop, cool, hasSsd);

        var preferVisual = tier <= RamTier.Gb16 || pressure || processHeavy;
        var preferStartup = true; // Bestia siempre revisa inicio
        var preferBg = tier <= RamTier.Gb24 || pressure || processHeavy;
        var avoidRam = tier >= RamTier.Gb16 && !pressure;

        var insights = baseProfile.Insights.ToList();
        insights.Insert(0, Loc.T("Beast.Insight.Profile", LabelBeast(beastAgg)));
        if (hasNvme) insights.Add(Loc.T("Beast.Insight.Nvme"));
        else if (hasHdd && !hasSsd) insights.Add(Loc.T("Beast.Insight.Hdd"));
        if (isLaptop) insights.Add(Loc.T("Beast.Insight.Laptop"));
        if (isDesktop && cool) insights.Add(Loc.T("Beast.Insight.DesktopCool"));

        return new HardwareOptimizationProfile
        {
            RamTier = tier,
            RamGb = ramGb,
            Aggressiveness = beastAgg,
            PrimaryLimitation = baseProfile.PrimaryLimitation,
            Insights = insights,
            DoNotRecommend = baseProfile.DoNotRecommend,
            KeepMemoryCompression = baseProfile.KeepMemoryCompression,
            KeepSystemManagedPageFile = baseProfile.KeepSystemManagedPageFile,
            PreferStartupCleanup = preferStartup,
            PreferBackgroundProcessCleanup = preferBg,
            PreferVisualEffectsReduction = preferVisual,
            AvoidAggressiveRamCleaning = avoidRam,
            SummaryText = string.Join(Environment.NewLine, insights.Select(i => "• " + i)) +
                          Environment.NewLine + Environment.NewLine +
                          Loc.T("Beast.DoNotAuto") + Environment.NewLine +
                          string.Join(Environment.NewLine, baseProfile.DoNotRecommend.Select(d => "• " + d))
        };
    }

    public static OptimizationAction Enrich(
        OptimizationAction a,
        HardwareOptimizationProfile hw,
        SystemSnapshot snap)
    {
        var (tier, selected, compatible, reason, current, recommended, tech) =
            Classify(a, hw, snap);

        return new OptimizationAction
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
            IsRecommendedDefault = selected && compatible,
            EstimatedDuration = a.EstimatedDuration,
            Category = a.Category,
            EstimatedBytesFreed = a.EstimatedBytesFreed,
            TargetKey = a.TargetKey,
            PriorityTier = compatible ? tier : BeastPriorityTier.Incompatible,
            CurrentState = current,
            RecommendedState = recommended,
            ProposedChange = a.ProposedChange,
            Compatibility = a.Compatibility,
            TechnicalDetails = a.TechnicalDetails,
            IsTemporary = a.IsTemporary,
            RollbackMethod = a.RollbackMethod,
            VerificationHint = a.VerificationHint,
            Source = a.Source,
            IsCompatible = compatible,
            IncompatibilityReason = reason,
            TechnicalLevel = tech,
            ModeAffinity = a.ModeAffinity,
            ImpactLevel = a.ImpactLevel,
            PersistenceType = a.PersistenceType,
            AffectsVisuals = a.AffectsVisuals,
            AffectsBackgroundApps = a.AffectsBackgroundApps,
            AffectsConvenience = a.AffectsConvenience
        };
    }

    private static (BeastPriorityTier Tier, bool Selected, bool Compatible, string? Reason,
        string Current, string Recommended, string Tech)
        Classify(OptimizationAction a, HardwareOptimizationProfile hw, SystemSnapshot snap)
    {
        var id = a.Id ?? "";
        var isLaptop = snap.IsPortable == true;
        var hasSsd = snap.Disks.Any(d =>
            d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var hasHddOnly = snap.Disks.Any(d => d.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)) && !hasSsd;
        var hasNvidia = HasGpu(snap, "NVIDIA") || HasGpu(snap, "GeForce");
        var hasAmd = HasGpu(snap, "AMD") || HasGpu(snap, "Radeon");
        var hasIntelGpu = HasGpu(snap, "Intel");

        // —— Incompatibilidades ——
        if (id.StartsWith("nvidia.", StringComparison.OrdinalIgnoreCase) && !hasNvidia)
            return (BeastPriorityTier.Incompatible, false, false, Loc.T("Beast.Incompat.NoNvidia"),
                Loc.T("Beast.Na"), Loc.T("Beast.Skip"), Loc.T("Beast.Advanced"));
        if (id.StartsWith("amd.", StringComparison.OrdinalIgnoreCase) && !hasAmd)
            return (BeastPriorityTier.Incompatible, false, false, Loc.T("Beast.Incompat.NoAmd"),
                Loc.T("Beast.Na"), Loc.T("Beast.Skip"), Loc.T("Beast.Advanced"));
        if (id.StartsWith("intel.gpu", StringComparison.OrdinalIgnoreCase) && !hasIntelGpu)
            return (BeastPriorityTier.Incompatible, false, false, Loc.T("Beast.Incompat.NoIntel"),
                Loc.T("Beast.Na"), Loc.T("Beast.Skip"), Loc.T("Beast.Advanced"));
        if (id is "power.ultimate" && isLaptop)
            return (BeastPriorityTier.Advanced, false, true, null,
                Loc.T("Beast.State.Plan"), Loc.T("Beast.Classify.UltimateLaptop"), Loc.T("Beast.Advanced"));
        if (id is "perf.hibernate_off" && isLaptop)
            return (BeastPriorityTier.Incompatible, false, false,
                Loc.T("Beast.Classify.HibernateLaptopWhy"),
                Loc.T("Beast.Classify.HibernateAvail"), Loc.T("Beast.State.Keep"), Loc.T("Beast.Advanced"));
        if (id is "service.sysmain.manual")
            return (BeastPriorityTier.Optional, false, true, null,
                "SysMain", Loc.T("Beast.Classify.SysMainManual"), Loc.T("Beast.Intermediate"));
        if (id is "service.search.manual")
            return (BeastPriorityTier.Optional, false, true, null,
                Loc.T("Beast.Classify.SearchIdx"), Loc.T("Beast.Classify.SearchManual"), Loc.T("Beast.Intermediate"));
        if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase) &&
            hw.AvoidAggressiveRamCleaning && a.Risk >= RiskLevel.Medium)
            return (BeastPriorityTier.Optional, false, true, null,
                Loc.T("Beast.Classify.BgProcess"), Loc.T("Beast.Classify.KeepRam"), Loc.T("Beast.Basic"));

        // —— Críticas / muy recomendadas según limitación ——
        if (id is "cleanup.temp" or "cleanup.advanced")
            return (BeastPriorityTier.Critical, true, true, null,
                Loc.T("Beast.Classify.TempsPresent"), Loc.T("Beast.Classify.CleanSafe"), Loc.T("Beast.Basic"));
        if (id.StartsWith("process.disable_startup", StringComparison.OrdinalIgnoreCase) && hw.PreferStartupCleanup)
            return (BeastPriorityTier.Critical, true, true, null,
                Loc.T("Beast.Classify.StartsWithWin"), Loc.T("Beast.Classify.RemoveStartup"), Loc.T("Beast.Basic"));
        if (id.StartsWith("process.priority_low", StringComparison.OrdinalIgnoreCase) && hw.PreferBackgroundProcessCleanup)
            return (BeastPriorityTier.Critical, true, true, null,
                Loc.T("Beast.Classify.PrioNormal"), "BelowNormal", Loc.T("Beast.Basic"));
        if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase) &&
            (hw.RamTier <= RamTier.Gb12 || !hw.AvoidAggressiveRamCleaning))
            return (BeastPriorityTier.Recommended, true, true, null,
                Loc.T("Beast.Classify.IdleProc"), Loc.T("Beast.Classify.CloseNoWindow"), Loc.T("Beast.Basic"));
        if ((id is "perf.visual_perf" or "perf.transparency_off" or "perf.animations_off"
             or "privacy.widgets" or "privacy.tips" or "windows.gamedvr_off") &&
            OptimizationPlanPolicy.IsVisualSurfaceAction(id))
            return (BeastPriorityTier.Critical, true, true, null,
                Loc.T("Beast.Classify.FxActive"), Loc.T("Beast.Classify.MinVisual"), Loc.T("Beast.Basic"));
        if (id is "process.boost_games")
            return (BeastPriorityTier.Recommended, true, true, null,
                Loc.T("Beast.Classify.GameDetected"), Loc.T("Beast.Classify.PrioHigh"), Loc.T("Beast.Basic"));
        if (id is "defender.reduce_load")
            return (BeastPriorityTier.Recommended, true, true, null,
                Loc.T("Beast.Classify.DefenderFull"), Loc.T("Beast.Classify.DefenderLight"), Loc.T("Beast.Intermediate"));
        if (id is "windows.gamemode" or "windows.gamedvr_off")
            return (BeastPriorityTier.Recommended, true, true, null,
                Loc.T("Beast.Classify.WinState"), Loc.T("Beast.Classify.GamingOpt"), Loc.T("Beast.Basic"));
        if (id is "power.high" or "power.cpu_max" or "intel.cpu_max" or "power.core_unpark")
        {
            var sel = !isLaptop || hw.Aggressiveness >= OptimizationAggressiveness.Performance;
            return (sel ? BeastPriorityTier.Recommended : BeastPriorityTier.Optional, sel, true, null,
                Loc.T("Beast.Classify.PlanCpu"), Loc.T("Beast.Classify.HighPerf"), Loc.T("Beast.Intermediate"));
        }
        if (id is "disk.trim" && hasSsd)
            return (BeastPriorityTier.Recommended, true, true, null,
                "SSD/NVMe", Loc.T("Beast.Classify.RunTrim"), Loc.T("Beast.Basic"));
        if (id is "disk.trim" && hasHddOnly)
            return (BeastPriorityTier.Incompatible, false, false, Loc.T("Beast.Classify.TrimHddWhy"),
                "HDD", Loc.T("Beast.Classify.SkipTrim"), Loc.T("Beast.Basic"));
        if (id.StartsWith("privacy.", StringComparison.OrdinalIgnoreCase))
            return (a.Risk == RiskLevel.Low ? BeastPriorityTier.Recommended : BeastPriorityTier.Optional,
                a.Risk == RiskLevel.Low, true, null,
                Loc.T("Beast.Classify.TelemetryOn"), Loc.T("Beast.Classify.ReduceNoise"), Loc.T("Beast.Basic"));
        if (id is "windows.hags")
            return (BeastPriorityTier.Advanced, false, true, null,
                Loc.T("Beast.Classify.Hags"), Loc.T("Beast.Classify.HagsOn"), Loc.T("Beast.Advanced"));
        if (a.RequiresReboot || a.Risk >= RiskLevel.High)
            return (BeastPriorityTier.Advanced, false, true, null,
                Loc.T("Beast.State.Current"), a.RecommendedState.Length > 0 ? a.RecommendedState : Loc.T("Beast.State.AdvChange"),
                Loc.T("Beast.Advanced"));
        if (a.Risk == RiskLevel.Medium)
            return (BeastPriorityTier.Optional, hw.Aggressiveness >= OptimizationAggressiveness.Performance, true, null,
                Loc.T("Beast.State.Current"), Loc.T("Beast.State.ApplyCompat"), Loc.T("Beast.Intermediate"));

        // Low risk default
        return (BeastPriorityTier.Recommended, true, true, null,
            string.IsNullOrWhiteSpace(a.CurrentState) ? Loc.T("Beast.State.Detected") : a.CurrentState,
            string.IsNullOrWhiteSpace(a.RecommendedState) ? Loc.T("Beast.State.Optimized") : a.RecommendedState,
            a.RequiresElevation ? Loc.T("Beast.Intermediate") : Loc.T("Beast.Basic"));
    }

    public static IReadOnlyList<OptimizationAction> InjectSmartExtras(
        List<OptimizationAction> actions,
        HardwareOptimizationProfile hw,
        SystemSnapshot snap)
    {
        void Add(string id, string name, string what, string why, string impact, string category,
            RiskLevel risk = RiskLevel.Low, bool elev = false, bool reboot = false, bool recommended = true)
        {
            if (actions.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
            actions.Add(new OptimizationAction
            {
                Id = id,
                DisplayName = name,
                Description = what,
                WhatWillHappen = what,
                WhyRecommended = why,
                ExpectedImpact = impact,
                Risk = risk,
                RequiresElevation = elev,
                RequiresReboot = reboot,
                IsReversible = true,
                IsRecommendedDefault = recommended,
                EstimatedDuration = TimeSpan.FromSeconds(8),
                Category = category
            });
        }

        // Bestia: superficie visual al mínimo (Rendimiento visual)
        {
            Add("perf.visual_perf", Loc.T("Extra.VisualPerf.Name"),
                Loc.T("Extra.VisualPerf.What"),
                Loc.T("Beast.VisualGroupWhy"),
                Loc.T("Extra.VisualPerf.Impact"), Loc.T("Cat.VisualBeast"));
            Add("perf.transparency_off", Loc.T("Extra.Transp.Name"),
                Loc.T("Extra.Transp.What"), Loc.T("Beast.VisualGroupWhy"),
                Loc.T("Extra.Transp.Impact"), Loc.T("Cat.VisualBeast"));
            Add("perf.animations_off", Loc.T("Extra.Anim.Name"),
                Loc.T("Extra.Anim.What"), Loc.T("Beast.VisualGroupWhy"),
                Loc.T("Extra.Anim.Impact"), Loc.T("Cat.VisualBeast"));
            Add("privacy.widgets", Loc.T("Rec.privacy.widgets.Title"),
                Loc.T("What.privacy_widgets"),
                Loc.T("Beast.VisualGroupWhy"),
                Loc.T("Extra.VisualPerf.Impact"), Loc.T("Cat.VisualBeast"));
            Add("privacy.tips", Loc.T("Rec.privacy.tips.Title"),
                Loc.T("What.privacy_tips"),
                Loc.T("Beast.VisualGroupWhy"),
                Loc.T("Extra.VisualPerf.Impact"), Loc.T("Cat.VisualBeast"));
            Add("windows.gamedvr_off", Loc.T("Rec.windows.gamedvr_off.Title"),
                Loc.T("What.windows_gamedvr_off"),
                Loc.T("Beast.VisualGroupWhy"),
                Loc.T("Extra.VisualPerf.Impact"), Loc.T("Cat.VisualBeast"));
        }

        Add("defender.reduce_load", Loc.T("Extra.Defender.Name"),
            Loc.T("Extra.Defender.What"),
            Loc.T("Extra.Defender.Why"),
            Loc.T("Extra.Defender.Impact"), Loc.T("Cat.Security"), RiskLevel.Low, elev: true);

        var hasSsd = snap.Disks.Any(d =>
            d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        if (hasSsd && hw.RamTier >= RamTier.Gb16)
            Add("service.sysmain.manual", Loc.T("Extra.SysMain.Name"),
                Loc.T("Extra.SysMain.What"),
                Loc.T("Extra.SysMain.Why"),
                Loc.T("Extra.SysMain.Impact"), Loc.T("Cat.Services"), RiskLevel.Medium, elev: true,
                recommended: false); // opcional Bestia

        if (snap.ProcessCount > 180 || hw.RamTier <= RamTier.Gb12 ||
            snap.Disks.Any(d => d.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)))
            Add("service.search.manual", Loc.T("Extra.Search.Name"),
                Loc.T("Beast.SearchWhy"),
                Loc.T("Beast.SearchWhy"),
                Loc.T("Extra.Search.Impact"), Loc.T("Cat.Services"), RiskLevel.Medium, elev: true,
                recommended: false); // seleccionable, no por defecto

        if (OptimizationPlanPolicy.HasForegroundBoostTargets())
        {
            Add("process.boost_games", Loc.T("Extra.Boost.Name"),
                Loc.T("Extra.Boost.What"),
                Loc.T("Extra.Boost.Why"),
                Loc.T("Extra.Boost.Impact"), Loc.T("Cat.ProcessesBeast"));
        }

        if (hw.Aggressiveness >= OptimizationAggressiveness.Performance)
            Add("perf.network_throttle", Loc.T("Extra.NetThrottle.Name"),
                Loc.T("Extra.NetThrottle.What"),
                Loc.T("Extra.NetThrottle.Why"),
                Loc.T("Extra.NetThrottle.Impact"), Loc.T("Cat.Performance"), RiskLevel.Medium, elev: true);

        return actions;
    }

    public static string BuildProfessionalReport(
        SystemSnapshot? before,
        SystemSnapshot? after,
        HardwareOptimizationProfile hw,
        OptimizationPlan plan,
        IReadOnlyList<ActionResult> results,
        IReadOnlyList<OptimizationAction> applied)
    {
        var ok = results.Where(r => r.Success).ToList();
        var fail = results.Where(r => !r.Success).ToList();
        var verified = results.Count(r => r.Verified);
        var bytes = results.Sum(r => r.BytesFreed ?? 0);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Loc.T("Report.Title"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Report.Hardware"));
        if (before is not null)
        {
            sb.AppendLine(Loc.T("Report.Cpu", before.Cpu.Name));
            sb.AppendLine(Loc.T("Report.Gpu", before.Gpu?.Name ?? Loc.T("Report.Na")));
            sb.AppendLine(Loc.T("Report.Ram", hw.RamGb, before.Memory.UsagePercent));
            var form = before.IsPortable == true ? Loc.T("Form.LaptopCap")
                : before.IsPortable == false ? Loc.T("Form.DesktopCap") : Loc.T("Form.Na");
            sb.AppendLine(Loc.T("Report.Form", form));
            sb.AppendLine(Loc.T("Report.Processes", before.ProcessCount));
        }
        sb.AppendLine(Loc.T("Report.Limitation", hw.PrimaryLimitation));
        sb.AppendLine(Loc.T("Report.Profile", LabelBeast(hw.Aggressiveness)));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Report.Plan"));
        sb.AppendLine(Loc.T("Report.PlanActions", plan.Actions.Count));
        sb.AppendLine(Loc.T("Report.PlanTiers", plan.CriticalCount, plan.RecommendedCount));
        sb.AppendLine(Loc.T("Report.PlanOptional", plan.OptionalCount, plan.AdvancedCount));
        sb.AppendLine(Loc.T("Report.PlanApplied", applied.Count));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Report.Results"));
        sb.AppendLine(Loc.T("Report.Ok", ok.Count));
        sb.AppendLine(Loc.T("Report.Fail", fail.Count));
        sb.AppendLine(Loc.T("Report.Verified", verified));
        if (bytes > 0)
            sb.AppendLine(Loc.T("Report.Bytes", bytes / (1024.0 * 1024)));
        sb.AppendLine();
        if (before?.HealthScore is int hb and > 0)
            sb.AppendLine(Loc.T("Report.HealthBefore", hb));
        if (after?.HealthScore is int ha and > 0)
            sb.AppendLine(Loc.T("Report.HealthAfter", ha));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Report.AppliedSection"));
        foreach (var r in ok.Take(15))
            sb.AppendLine($"  ✓ {r.ActionId}: {(r.Verified ? Loc.T("Report.VerifiedPrefix") : "")}{Truncate(r.Detail, 90)}");
        if (ok.Count > 15) sb.AppendLine(Loc.T("Report.AndMore", ok.Count - 15));
        sb.AppendLine();
        if (fail.Count > 0)
        {
            sb.AppendLine(Loc.T("Report.FailedSection"));
            foreach (var r in fail.Take(10))
                sb.AppendLine($"  ✗ {r.ActionId}: {Truncate(r.Detail, 100)}");
            sb.AppendLine();
        }
        var incompatible = plan.Actions.Where(a => !a.IsCompatible).ToList();
        if (incompatible.Count > 0)
        {
            sb.AppendLine(Loc.T("Report.IncompatSection"));
            foreach (var a in incompatible.Take(8))
                sb.AppendLine($"  · {a.DisplayName}: {a.IncompatibilityReason}");
            sb.AppendLine();
        }
        sb.AppendLine(Loc.T("Report.Security"));
        sb.AppendLine(Loc.T("Report.SecurityDefender"));
        sb.AppendLine(Loc.T("Report.SecurityFiles"));
        sb.AppendLine();
        sb.AppendLine(fail.Count == 0 ? Loc.T("Report.OkState") : Loc.T("Report.ErrState"));
        return sb.ToString();
    }

    private static OptimizationAggressiveness DeriveBeastAggressiveness(
        RamTier tier, bool pressure, bool laptop, bool desktop, bool cool, bool ssd)
    {
        if (!cool) return OptimizationAggressiveness.Conservative;
        if (tier <= RamTier.Gb8 || pressure)
            return OptimizationAggressiveness.Beast;
        if (desktop && ssd && tier >= RamTier.Gb16 && cool)
            return OptimizationAggressiveness.Beast;
        if (laptop && tier >= RamTier.Gb16)
            return OptimizationAggressiveness.Performance;
        return OptimizationAggressiveness.Performance;
    }

    private static string LabelBeast(OptimizationAggressiveness a) => a switch
    {
        OptimizationAggressiveness.Conservative => Loc.T("Agg.Conservative"),
        OptimizationAggressiveness.Balanced => Loc.T("Agg.Balanced"),
        OptimizationAggressiveness.Performance => Loc.T("Agg.Performance"),
        OptimizationAggressiveness.Advanced => Loc.T("Agg.Advanced"),
        OptimizationAggressiveness.Beast => Loc.T("Agg.Beast"),
        _ => Loc.T("Agg.Balanced")
    };

    private static bool HasGpu(SystemSnapshot snap, string token) =>
        snap.Gpu?.Name.Contains(token, StringComparison.OrdinalIgnoreCase) == true ||
        snap.Gpus.Any(g => g.Name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
