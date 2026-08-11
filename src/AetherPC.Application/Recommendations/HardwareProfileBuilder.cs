using System.Text;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;

namespace AetherPC.Application.Recommendations;

/// <summary>
/// Perfil de optimización derivado del hardware/uso reales (reutiliza SystemSnapshot).
/// No inventa métricas: si falta un dato, se omite o se marca No disponible.
/// </summary>
public sealed class HardwareOptimizationProfile
{
    public RamTier RamTier { get; init; }
    public double RamGb { get; init; }
    public OptimizationAggressiveness Aggressiveness { get; init; }
    public string PrimaryLimitation { get; init; } = NotDetected.Text;
    public IReadOnlyList<string> Insights { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DoNotRecommend { get; init; } = Array.Empty<string>();
    public bool KeepMemoryCompression { get; init; } = true;
    public bool KeepSystemManagedPageFile { get; init; } = true;
    public bool PreferStartupCleanup { get; init; }
    public bool PreferBackgroundProcessCleanup { get; init; }
    public bool PreferVisualEffectsReduction { get; init; }
    public bool AvoidAggressiveRamCleaning { get; init; }
    public string SummaryText { get; init; } = "";
}

public static class HardwareProfileBuilder
{
    public static HardwareOptimizationProfile Build(SystemSnapshot snapshot, UserProfile profile)
    {
        var ramGb = snapshot.Memory.TotalBytes / (1024.0 * 1024 * 1024);
        var tier = ClassifyRamTier(ramGb);
        var hasSsd = snapshot.Disks.Any(d =>
            d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var hasHdd = snapshot.Disks.Any(d => d.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase));
        var lowDisk = snapshot.Disks.Any(d => d.UsedPercent >= 90 && !string.IsNullOrWhiteSpace(d.DriveLetter));
        var isLaptop = snapshot.IsPortable == true;
        var commitPct = snapshot.Memory.CommitUsagePercent;
        var memPct = snapshot.Memory.UsagePercent;
        var temp = snapshot.Thermals.CpuCelsius ?? snapshot.Cpu.TemperatureCelsius;
        var pressure = (commitPct is >= 85) || (memPct >= 92 && tier <= RamTier.Gb12);
        var processHeavy = snapshot.ProcessCount > (tier switch
        {
            RamTier.Gb4 or RamTier.Gb6 => 140,
            RamTier.Gb8 => 180,
            RamTier.Gb12 => 220,
            _ => 280
        });

        var aggressiveness = DeriveAggressiveness(tier, pressure, lowDisk, temp, profile);
        var limitation = DeriveLimitation(tier, pressure, lowDisk, temp, hasHdd, snapshot);
        var insights = new List<string>();
        var doNot = new List<string>();

        insights.Add(ramGb > 0
            ? Loc.T(hasSsd ? "Insight.RamSsd" : hasHdd ? "Insight.RamHdd" : "Insight.RamPlain", ramGb.ToString("F0"), LabelTier(tier))
            : Loc.T("Insight.RamUnknown"));

        insights.Add(isLaptop ? Loc.T("Insight.FormLaptop")
            : snapshot.IsPortable == false ? Loc.T("Insight.FormDesktop")
            : Loc.T("Insight.FormUnknown"));

        if (snapshot.Memory.CommitUsagePercent is double c)
            insights.Add(Loc.T("Insight.CommitKnown", c));
        else
            insights.Add(Loc.T("Insight.CommitUnknown"));

        if (snapshot.Memory.PageFileSystemManaged is true)
            insights.Add(Loc.T("Insight.PageFileManaged"));
        else if (snapshot.Memory.PageFileSystemManaged is false)
            insights.Add(Loc.T("Insight.PageFileManual", snapshot.Memory.PageFileConfigDetail ?? "").Trim());
        else
            insights.Add(Loc.T("Insight.PageFileUnknown"));

        if (snapshot.Memory.CompressedBytes is ulong cb && cb > 0)
            insights.Add(Loc.T("Insight.MemCompressionActive", cb / (1024.0 * 1024)));
        else
            insights.Add(Loc.T("Insight.MemCompressionUnknown"));

        insights.Add(Loc.T("Insight.LimitationLine", limitation));
        insights.Add(Loc.T("Insight.AggressivenessLine", LabelAgg(aggressiveness)));

        if (snapshot.ProcessCount > 0)
            insights.Add(Loc.T("Insight.ProcessCount", snapshot.ProcessCount));

        if (temp is double t)
            insights.Add(Loc.T("Insight.TempKnown", t));
        else
            insights.Add(Loc.T("Insight.TempUnknown"));

        // Reglas de NO recomendar
        doNot.Add(Loc.T("DoNot.NoDefenderFirewallUpdate"));
        doNot.Add(Loc.T("DoNot.NoPageFileOff"));
        if (tier <= RamTier.Gb12)
            doNot.Add(Loc.T("DoNot.NoMemCompressionLowTier"));
        if (tier >= RamTier.Gb16)
            doNot.Add(Loc.T("DoNot.NoRamCleanForFreeMem"));
        if (tier >= RamTier.Gb24)
            doNot.Add(Loc.T("DoNot.NoCloseAppsHighRam"));

        var keepCompression = tier <= RamTier.Gb16 || aggressiveness != OptimizationAggressiveness.Advanced;
        var preferStartup = tier <= RamTier.Gb12 || processHeavy || aggressiveness >= OptimizationAggressiveness.Balanced;
        var preferBg = pressure || tier <= RamTier.Gb8 || aggressiveness >= OptimizationAggressiveness.Performance;
        var preferVisual = pressure && tier <= RamTier.Gb8;
        var avoidRamClean = tier >= RamTier.Gb16 && !pressure;

        var sb = new StringBuilder();
        foreach (var line in insights)
            sb.AppendLine("• " + line);
        sb.AppendLine();
        sb.AppendLine(Loc.T("Summary.DoNotHeader"));
        foreach (var line in doNot)
            sb.AppendLine("• " + line);

        return new HardwareOptimizationProfile
        {
            RamTier = tier,
            RamGb = ramGb,
            Aggressiveness = aggressiveness,
            PrimaryLimitation = limitation,
            Insights = insights,
            DoNotRecommend = doNot,
            KeepMemoryCompression = keepCompression,
            KeepSystemManagedPageFile = snapshot.Memory.PageFileSystemManaged is not false,
            PreferStartupCleanup = preferStartup,
            PreferBackgroundProcessCleanup = preferBg,
            PreferVisualEffectsReduction = preferVisual,
            AvoidAggressiveRamCleaning = avoidRamClean,
            SummaryText = sb.ToString().TrimEnd()
        };
    }

    public static RamTier ClassifyRamTier(double ramGb)
    {
        if (ramGb <= 0) return RamTier.Unknown;
        if (ramGb < 5) return RamTier.Gb4;
        if (ramGb < 7) return RamTier.Gb6;
        if (ramGb < 10) return RamTier.Gb8;
        if (ramGb < 14) return RamTier.Gb12;
        if (ramGb < 20) return RamTier.Gb16;
        if (ramGb < 28) return RamTier.Gb24;
        if (ramGb < 40) return RamTier.Gb32;
        if (ramGb < 56) return RamTier.Gb48;
        return RamTier.Gb64Plus;
    }

    private static OptimizationAggressiveness DeriveAggressiveness(
        RamTier tier, bool pressure, bool lowDisk, double? temp, UserProfile profile)
    {
        if (temp is >= 90) return OptimizationAggressiveness.Conservative;
        if (tier <= RamTier.Gb6 || (tier <= RamTier.Gb8 && pressure))
            return OptimizationAggressiveness.Performance;
        if (lowDisk || pressure)
            return OptimizationAggressiveness.Balanced;
        if (profile.ActiveProfile == OptimizationProfileKind.MaxPerformance && tier >= RamTier.Gb16)
            return OptimizationAggressiveness.Advanced;
        if (tier >= RamTier.Gb24)
            return OptimizationAggressiveness.Conservative;
        return OptimizationAggressiveness.Balanced;
    }

    private static string DeriveLimitation(
        RamTier tier, bool pressure, bool lowDisk, double? temp, bool hasHdd, SystemSnapshot snap)
    {
        if (temp is >= 90) return Loc.T("Limitation.Thermal");
        if (lowDisk) return Loc.T("Limitation.LowDisk");
        if (pressure && tier <= RamTier.Gb12) return Loc.T("Limitation.MemoryPressure");
        if (hasHdd && snap.Disks.Any(d => d.DriveLetter.StartsWith("C", StringComparison.OrdinalIgnoreCase)
                                          && d.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)))
            return Loc.T("Limitation.HddSystem");
        if (tier <= RamTier.Gb6) return Loc.T("Limitation.LowRam");
        if (snap.ProcessCount > 250) return Loc.T("Limitation.ProcessLoad");
        return Loc.T("Limitation.None");
    }

    private static string LabelTier(RamTier t) => t switch
    {
        RamTier.Gb4 => Loc.T("Tier.Gb4"),
        RamTier.Gb6 => Loc.T("Tier.Gb6"),
        RamTier.Gb8 => Loc.T("Tier.Gb8"),
        RamTier.Gb12 => Loc.T("Tier.Gb12"),
        RamTier.Gb16 => Loc.T("Tier.Gb16"),
        RamTier.Gb24 => Loc.T("Tier.Gb24"),
        RamTier.Gb32 => Loc.T("Tier.Gb32"),
        RamTier.Gb48 => Loc.T("Tier.Gb48"),
        RamTier.Gb64Plus => Loc.T("Tier.Gb64Plus"),
        _ => Loc.T("Tier.Unknown")
    };

    private static string LabelAgg(OptimizationAggressiveness a) => a switch
    {
        OptimizationAggressiveness.Conservative => Loc.T("Agg.Conservative"),
        OptimizationAggressiveness.Balanced => Loc.T("Agg.Balanced"),
        OptimizationAggressiveness.Performance => Loc.T("Agg.Performance"),
        OptimizationAggressiveness.Advanced => Loc.T("Agg.Advanced"),
        _ => Loc.T("Agg.Balanced")
    };
}
