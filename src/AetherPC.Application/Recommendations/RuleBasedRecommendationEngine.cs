using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;

namespace AetherPC.Application.Recommendations;

/// <summary>
/// Recomendaciones según hardware real. No emite acciones NVIDIA en AMD/Intel, etc.
/// </summary>
public sealed class RuleBasedRecommendationEngine : IRecommendationEngine
{
    public Task<IReadOnlyList<Recommendation>> AnalyzeAsync(SystemSnapshot snapshot, UserProfile profile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var list = new List<Recommendation>();
        var hw = HardwareProfileBuilder.Build(snapshot, profile);
        var ramGb = hw.RamGb;
        var tier = hw.RamTier;
        var hasSsd = snapshot.Disks.Any(d =>
            d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var discreteGpu = snapshot.Gpus.FirstOrDefault(g => !g.IsLikelyIntegrated) ?? snapshot.Gpu;
        var isGaming = profile.UsageType.Contains("Gaming", StringComparison.OrdinalIgnoreCase) ||
                       profile.ActiveProfile is OptimizationProfileKind.Gaming or OptimizationProfileKind.MaxPerformance;
        var isLaptop = snapshot.IsPortable == true;
        var isDesktop = snapshot.IsPortable == false;
        var cpuName = snapshot.Cpu.Name ?? "";
        var isIntelCpu = cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        var isAmdCpu = cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                       cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        var hasNvidia = HasGpu(snapshot, "NVIDIA") || HasGpu(snapshot, "GeForce");
        var hasAmdGpu = (HasGpu(snapshot, "AMD") || HasGpu(snapshot, "Radeon")) &&
                        !(discreteGpu?.IsLikelyIntegrated ?? true);
        var hasIntelGpu = HasGpu(snapshot, "Intel");
        // En poca RAM no forzar “máximo” energético: estabilidad primero
        var wantMax = (isGaming || profile.ActiveProfile == OptimizationProfileKind.MaxPerformance || isDesktop)
                      && tier >= RamTier.Gb12
                      && hw.Aggressiveness != OptimizationAggressiveness.Conservative;

        // —— Perfil personalizado (informativo) ——
        list.Add(Rec(
            Loc.T("RecRB.Profile.Title", LabelAgg(hw.Aggressiveness)),
            hw.PrimaryLimitation,
            Loc.T("RecRB.Profile.Cause"),
            hw.SummaryText.Length > 400 ? hw.SummaryText[..400] + "…" : hw.SummaryText,
            Loc.T("RecRB.Profile.Benefit"), RiskLevel.Low, 99, Loc.T("Cat.Profile"), null));

        if (hw.KeepSystemManagedPageFile)
        {
            list.Add(Rec(
                Loc.T("RecRB.PageFileKeep.Title"),
                snapshot.Memory.PageFileConfigDetail ?? Loc.T("RecRB.PageFileKeep.ProblemFallback"),
                Loc.T("RecRB.PageFileKeep.Cause"),
                Loc.T("RecRB.PageFileKeep.Solution"),
                Loc.T("RecRB.PageFileKeep.Benefit"), RiskLevel.Low, 94, Loc.T("Cat.MemoryPressure"), null));
        }

        if (hw.KeepMemoryCompression)
        {
            list.Add(Rec(
                Loc.T("RecRB.MemCompressionKeep.Title"),
                tier <= RamTier.Gb12
                    ? Loc.T("RecRB.MemCompressionKeep.ProblemLowTier", LabelTier(tier))
                    : Loc.T("RecRB.MemCompressionKeep.ProblemElse"),
                Loc.T("RecRB.MemCompressionKeep.Cause"),
                Loc.T("RecRB.MemCompressionKeep.Solution"),
                Loc.T("RecRB.MemCompressionKeep.Benefit"), RiskLevel.Low, 93, Loc.T("Cat.MemoryPressure"), null));
        }

        if (tier <= RamTier.Gb8 && (snapshot.Memory.UsagePercent >= 85 || snapshot.Memory.CommitUsagePercent is >= 80))
        {
            list.Add(Rec(
                Loc.T("RecRB.RamLimit.Title"),
                Loc.T("RecRB.RamLimit.Problem", ramGb),
                Loc.T("RecRB.RamLimit.Cause"),
                Loc.T("RecRB.RamLimit.Solution"),
                Loc.T("RecRB.RamLimit.Benefit"), RiskLevel.Low, 97, Loc.T("Cat.Hardware"), null));
        }

        // —— Energía / CPU (según agresividad y factor de forma) ——
        var temp = snapshot.Thermals.CpuCelsius ?? snapshot.Cpu.TemperatureCelsius;
        if (temp is >= 90)
        {
            list.Add(Rec(
                Loc.T("RecRB.CpuTempCritical.Title"),
                Loc.T("RecRB.CpuTempCritical.Problem", temp, snapshot.Cpu.Name ?? ""),
                Loc.T("RecRB.CpuTempCritical.Cause"),
                Loc.T("RecRB.CpuTempCritical.Solution"),
                Loc.T("RecRB.CpuTempCritical.Benefit"), RiskLevel.Medium, 95, Loc.T("Cat.Temperature"), "power.balanced",
                requiresElevation: true));
        }
        else if (isLaptop && !wantMax)
        {
            list.Add(Rec(
                Loc.T("RecRB.LaptopBalanced.Title"),
                Loc.T("RecRB.LaptopBalanced.Problem"),
                Loc.T("RecRB.LaptopBalanced.Cause"),
                Loc.T("RecRB.LaptopBalanced.Solution"),
                Loc.T("RecRB.LaptopBalanced.Benefit"), RiskLevel.Low, 80, Loc.T("Cat.Energy"), "power.balanced",
                requiresElevation: true));
        }
        else if (isDesktop || wantMax)
        {
            if (isDesktop)
            {
                list.Add(Rec(
                    Loc.T("RecRB.DesktopUltimate.Title"),
                    Loc.T("RecRB.DesktopUltimate.Problem", snapshot.Cpu.Name ?? ""),
                    Loc.T("RecRB.DesktopUltimate.Cause"),
                    Loc.T("RecRB.DesktopUltimate.Solution"),
                    Loc.T("RecRB.DesktopUltimate.Benefit"), RiskLevel.Low, 90, Loc.T("Cat.Energy"), "power.ultimate",
                    requiresElevation: true));
            }
            else
            {
                list.Add(Rec(
                    Loc.T("RecRB.LaptopGamingHigh.Title"),
                    Loc.T("RecRB.LaptopGamingHigh.Problem"),
                    Loc.T("RecRB.LaptopGamingHigh.Cause"),
                    Loc.T("RecRB.LaptopGamingHigh.Solution"),
                    Loc.T("RecRB.LaptopGamingHigh.Benefit"), RiskLevel.Medium, 82, Loc.T("Cat.Energy"), "power.high",
                    requiresElevation: true));
            }

            list.Add(Rec(
                isIntelCpu ? Loc.T("RecRB.CpuStatesMax.TitleIntel")
                    : isAmdCpu ? Loc.T("RecRB.CpuStatesMax.TitleAmd")
                    : Loc.T("RecRB.CpuStatesMax.TitleGeneric"),
                Loc.T("RecRB.CpuStatesMax.Problem", snapshot.Cpu.Name ?? "", snapshot.Cpu.Cores, snapshot.Cpu.LogicalProcessors),
                Loc.T("RecRB.CpuStatesMax.Cause"),
                Loc.T("RecRB.CpuStatesMax.Solution"),
                Loc.T("RecRB.CpuStatesMax.Benefit"), RiskLevel.Low, 88, Loc.T("Cat.Cpu"),
                isIntelCpu ? "intel.cpu_max" : "power.cpu_max",
                requiresElevation: true));

            list.Add(Rec(
                Loc.T("RecRB.CoreUnpark.Title"),
                Loc.T("RecRB.CoreUnpark.Problem", snapshot.Cpu.LogicalProcessors),
                Loc.T("RecRB.CoreUnpark.Cause"),
                Loc.T("RecRB.CoreUnpark.Solution"),
                Loc.T("RecRB.CoreUnpark.Benefit"), RiskLevel.Low, 84, Loc.T("Cat.Cpu"), "power.core_unpark",
                requiresElevation: true));

            list.Add(Rec(
                Loc.T("RecRB.TurboAggressive.Title"),
                snapshot.Cpu.Name ?? Loc.T("RecRB.TurboAggressive.ProblemFallback"),
                Loc.T("RecRB.TurboAggressive.Cause"),
                Loc.T("RecRB.TurboAggressive.Solution"),
                Loc.T("RecRB.TurboAggressive.Benefit"), RiskLevel.Medium, 78, Loc.T("Cat.Cpu"), "power.boost_aggressive",
                requiresElevation: true));
        }

        // —— GPU NVIDIA ——
        if (hasNvidia)
        {
            var name = discreteGpu?.Name
                       ?? snapshot.Gpus.FirstOrDefault(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))?.Name
                       ?? "NVIDIA";
            list.Add(Rec(
                Loc.T("RecRB.NvidiaMax.Title"),
                name,
                Loc.T("RecRB.NvidiaMax.Cause"),
                Loc.T("RecRB.NvidiaMax.Solution"),
                Loc.T("RecRB.NvidiaMax.Benefit"), RiskLevel.Medium, 92, Loc.T("Cat.GpuNvidia"), "nvidia.powermizer_max",
                requiresElevation: true));

            list.Add(Rec(
                Loc.T("RecRB.NvidiaLowLatency.Title"),
                name,
                Loc.T("RecRB.NvidiaLowLatency.Cause"),
                Loc.T("RecRB.NvidiaLowLatency.Solution"),
                Loc.T("RecRB.NvidiaLowLatency.Benefit"), RiskLevel.Low, 80, Loc.T("Cat.GpuNvidia"), "nvidia.low_latency"));

            list.Add(Rec(
                Loc.T("RecRB.PcieMax.Title"),
                Loc.T("RecRB.PcieMax.Problem", name),
                Loc.T("RecRB.PcieMax.Cause"),
                Loc.T("RecRB.PcieMax.Solution"),
                Loc.T("RecRB.PcieMax.Benefit"), RiskLevel.Low, 85, Loc.T("Cat.Gpu"), "power.pcie_max",
                requiresElevation: true));
        }

        // —— GPU AMD ——
        if (hasAmdGpu && !hasNvidia)
        {
            list.Add(Rec(
                Loc.T("RecRB.AmdPerf.Title"),
                discreteGpu?.Name ?? "AMD",
                Loc.T("RecRB.AmdPerf.Cause"),
                Loc.T("RecRB.AmdPerf.Solution"),
                Loc.T("RecRB.AmdPerf.Benefit"), RiskLevel.Low, 86, Loc.T("Cat.GpuAmd"), "amd.gpu_max",
                requiresElevation: true));

            list.Add(Rec(
                Loc.T("RecRB.PcieAmd.Title"),
                discreteGpu?.Name ?? "AMD",
                Loc.T("RecRB.PcieAmd.Cause"),
                Loc.T("RecRB.PcieAmd.Solution"),
                Loc.T("RecRB.PcieAmd.Benefit"), RiskLevel.Low, 84, Loc.T("Cat.Gpu"), "power.pcie_max",
                requiresElevation: true));
        }

        // —— GPU Intel (iGPU o Arc) ——
        if (hasIntelGpu)
        {
            list.Add(Rec(
                Loc.T("RecRB.IntelGpuMax.Title"),
                snapshot.Gpus.FirstOrDefault(g => g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))?.Name ?? "Intel GPU",
                Loc.T("RecRB.IntelGpuMax.Cause"),
                Loc.T("RecRB.IntelGpuMax.Solution"),
                Loc.T("RecRB.IntelGpuMax.Benefit"), RiskLevel.Low, 83, Loc.T("Cat.GpuIntel"), "intel.gpu_max"));
        }

        // —— Gaming Windows ——
        if (wantMax || hasNvidia || hasAmdGpu)
        {
            list.Add(Rec(
                Loc.T("RecRB.GameMode.Title"),
                discreteGpu?.Name ?? snapshot.Cpu.Name ?? "PC",
                Loc.T("RecRB.GameMode.Cause"),
                Loc.T("RecRB.GameMode.Solution"),
                Loc.T("RecRB.GameMode.Benefit"), RiskLevel.Low, 87, Loc.T("Cat.Gaming"), "windows.gamemode"));

            if (discreteGpu is { IsLikelyIntegrated: false } || hasNvidia || hasAmdGpu)
            {
                list.Add(Rec(
                    Loc.T("RecRB.Hags.Title"),
                    discreteGpu?.Name ?? Loc.T("RecRB.Hags.ProblemFallback"),
                    Loc.T("RecRB.Hags.Cause"),
                    Loc.T("RecRB.Hags.Solution"),
                    Loc.T("RecRB.Hags.Benefit"), RiskLevel.Medium, 81, Loc.T("Cat.Gaming"), "windows.hags",
                    requiresElevation: true, requiresReboot: true));
            }

            list.Add(Rec(
                Loc.T("RecRB.GameDvrOff.Title"),
                Loc.T("RecRB.GameDvrOff.Problem"),
                Loc.T("RecRB.GameDvrOff.Cause"),
                Loc.T("RecRB.GameDvrOff.Solution"),
                Loc.T("RecRB.GameDvrOff.Benefit"), RiskLevel.Low, 79, Loc.T("Cat.Gaming"), "windows.gamedvr_off"));

            list.Add(Rec(
                Loc.T("RecRB.MmcssGaming.Title"),
                Loc.T("RecRB.MmcssGaming.Problem"),
                Loc.T("RecRB.MmcssGaming.Cause"),
                Loc.T("RecRB.MmcssGaming.Solution"),
                Loc.T("RecRB.MmcssGaming.Benefit"), RiskLevel.Medium, 77, Loc.T("Cat.Gaming"), "windows.mmcss_low_latency",
                requiresElevation: true));
        }

        // —— Disco ——
        foreach (var disk in snapshot.Disks.Where(d => !string.IsNullOrWhiteSpace(d.DriveLetter)))
        {
            if (disk.UsedPercent >= 90)
            {
                list.Add(Rec(
                    Loc.T("RecRB.DiskLowSpace.Title", disk.DriveLetter),
                    Loc.T("RecRB.DiskLowSpace.Problem", disk.UsedPercent, disk.MediaType),
                    Loc.T("RecRB.DiskLowSpace.Cause"),
                    Loc.T("RecRB.DiskLowSpace.Solution"),
                    Loc.T("RecRB.DiskLowSpace.Benefit"), RiskLevel.Low, 93, Loc.T("Cat.Disk"), "cleanup.advanced"));
            }

            if (disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                disk.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(Rec(
                    Loc.T("RecRB.DiskTrim.Title", disk.DriveLetter),
                    Loc.T("RecRB.DiskTrim.Problem", disk.MediaType),
                    Loc.T("RecRB.DiskTrim.Cause"),
                    Loc.T("RecRB.DiskTrim.Solution", disk.DriveLetter),
                    Loc.T("RecRB.DiskTrim.Benefit"), RiskLevel.Low, 70, Loc.T("Cat.Disk"), "disk.trim",
                    requiresElevation: true));
            }
        }

        if (hasSsd)
        {
            list.Add(Rec(
                Loc.T("RecRB.StorageSense.Title"),
                Loc.T("RecRB.StorageSense.Problem"),
                Loc.T("RecRB.StorageSense.Cause"),
                Loc.T("RecRB.StorageSense.Solution"),
                Loc.T("RecRB.StorageSense.Benefit"), RiskLevel.Low, 68, Loc.T("Cat.Disk"), "windows.storage_sense"));
        }

        // —— RAM / SysMain / inicio (umbrales por tramo) ——
        var ramPressureThreshold = tier switch
        {
            RamTier.Gb4 or RamTier.Gb6 => 80,
            RamTier.Gb8 => 88,
            RamTier.Gb12 => 92,
            _ => 96
        };
        var processCountThreshold = tier switch
        {
            RamTier.Gb4 or RamTier.Gb6 => 140,
            RamTier.Gb8 => 180,
            RamTier.Gb12 => 220,
            _ => 280
        };

        if (!hw.AvoidAggressiveRamCleaning && snapshot.Memory.UsagePercent >= ramPressureThreshold)
        {
            list.Add(Rec(
                Loc.T("RecRB.RamPressure.Title"),
                Loc.T("RecRB.RamPressure.Problem", snapshot.Memory.UsagePercent, ramGb, ramPressureThreshold),
                Loc.T("RecRB.RamPressure.Cause"),
                hw.PreferStartupCleanup
                    ? Loc.T("RecRB.RamPressure.SolutionStartup")
                    : Loc.T("RecRB.RamPressure.SolutionSafe"),
                Loc.T("RecRB.RamPressure.Benefit"), RiskLevel.Low, 91, Loc.T("Cat.Ram"), "cleanup.temp"));
        }

        // SysMain: solo opcional con mucha RAM + SSD + agresividad rendimiento/avanzado
        if (ramGb >= 16 && hasSsd && hw.Aggressiveness >= OptimizationAggressiveness.Performance && wantMax)
        {
            list.Add(Rec(
                Loc.T("RecRB.SysMainManual.Title"),
                Loc.T("RecRB.SysMainManual.Problem", ramGb, LabelAgg(hw.Aggressiveness)),
                Loc.T("RecRB.SysMainManual.Cause"),
                Loc.T("RecRB.SysMainManual.Solution"),
                Loc.T("RecRB.SysMainManual.Benefit"), RiskLevel.Medium, 72, Loc.T("Cat.Services"), "service.sysmain.manual",
                requiresElevation: true));
        }

        if (hw.PreferVisualEffectsReduction)
        {
            list.Add(Rec(
                Loc.T("RecRB.VisualEffectsMax.Title"),
                Loc.T("RecRB.VisualEffectsMax.Problem", LabelTier(tier)),
                Loc.T("RecRB.VisualEffectsMax.Cause"),
                Loc.T("RecRB.VisualEffectsMax.Solution"),
                Loc.T("RecRB.VisualEffectsMax.Benefit"), RiskLevel.Low, 76, Loc.T("Cat.Visual"), "perf.visual_perf"));
            list.Add(Rec(
                Loc.T("RecRB.TransparencyOff.Title"),
                Loc.T("RecRB.TransparencyOff.Problem"),
                Loc.T("RecRB.TransparencyOff.Cause"),
                Loc.T("RecRB.TransparencyOff.Solution"),
                Loc.T("RecRB.TransparencyOff.Benefit"), RiskLevel.Low, 70, Loc.T("Cat.Visual"), "perf.transparency_off"));
        }

        // —— Pack máximo (estilo Optimizer): telemetría / privacidad / rendimiento ——
        // Nunca Defender, nunca Windows Update
        list.Add(Rec(
            Loc.T("RecRB.TelemetryLimit.Title"),
            Loc.T("RecRB.TelemetryLimit.Problem"),
            Loc.T("RecRB.TelemetryLimit.Cause"),
            Loc.T("RecRB.TelemetryLimit.Solution"),
            Loc.T("RecRB.TelemetryLimit.Benefit"), RiskLevel.Low, 88, Loc.T("Cat.Privacy"), "privacy.telemetry",
            requiresElevation: true));
        list.Add(Rec(
            Loc.T("RecRB.DiagTrackServices.Title"),
            Loc.T("RecRB.DiagTrackServices.Problem"),
            Loc.T("RecRB.DiagTrackServices.Cause"),
            Loc.T("RecRB.DiagTrackServices.Solution"),
            Loc.T("RecRB.DiagTrackServices.Benefit"), RiskLevel.Medium, 86, Loc.T("Cat.Privacy"), "privacy.diagtrack",
            requiresElevation: true));
        list.Add(Rec(
            Loc.T("RecRB.AdvertisingIdOff.Title"),
            Loc.T("RecRB.AdvertisingIdOff.Problem"),
            Loc.T("RecRB.AdvertisingIdOff.Cause"),
            Loc.T("RecRB.AdvertisingIdOff.Solution"),
            Loc.T("RecRB.AdvertisingIdOff.Benefit"), RiskLevel.Low, 75, Loc.T("Cat.Privacy"), "privacy.advertising"));
        list.Add(Rec(
            Loc.T("RecRB.TipsOff.Title"),
            Loc.T("RecRB.TipsOff.Problem"),
            Loc.T("RecRB.TipsOff.Cause"),
            Loc.T("RecRB.TipsOff.Solution"),
            Loc.T("RecRB.TipsOff.Benefit"), RiskLevel.Low, 80, Loc.T("Cat.Privacy"), "privacy.tips"));
        list.Add(Rec(
            Loc.T("RecRB.ActivityHistoryLimited.Title"),
            Loc.T("RecRB.ActivityHistoryLimited.Problem"),
            Loc.T("RecRB.ActivityHistoryLimited.Cause"),
            Loc.T("RecRB.ActivityHistoryLimited.Solution"),
            Loc.T("RecRB.ActivityHistoryLimited.Benefit"), RiskLevel.Low, 74, Loc.T("Cat.Privacy"), "privacy.activity",
            requiresElevation: true));
        list.Add(Rec(
            Loc.T("RecRB.FeedbackOff.Title"),
            Loc.T("RecRB.FeedbackOff.Problem"),
            Loc.T("RecRB.FeedbackOff.Cause"),
            Loc.T("RecRB.FeedbackOff.Solution"),
            Loc.T("RecRB.FeedbackOff.Benefit"), RiskLevel.Low, 65, Loc.T("Cat.Privacy"), "privacy.feedback"));
        list.Add(Rec(
            Loc.T("RecRB.CopilotLimit.Title"),
            Loc.T("RecRB.CopilotLimit.Problem"),
            Loc.T("RecRB.CopilotLimit.Cause"),
            Loc.T("RecRB.CopilotLimit.Solution"),
            Loc.T("RecRB.CopilotLimit.Benefit"), RiskLevel.Low, 78, Loc.T("Cat.Privacy"), "privacy.copilot",
            requiresElevation: true));
        list.Add(Rec(
            Loc.T("RecRB.WidgetsOff.Title"),
            Loc.T("RecRB.WidgetsOff.Problem"),
            Loc.T("RecRB.WidgetsOff.Cause"),
            Loc.T("RecRB.WidgetsOff.Solution"),
            Loc.T("RecRB.WidgetsOff.Benefit"), RiskLevel.Low, 82, Loc.T("Cat.Privacy"), "privacy.widgets"));
        list.Add(Rec(
            Loc.T("RecRB.BackgroundAppsOff.Title"),
            Loc.T("RecRB.BackgroundAppsOff.Problem"),
            Loc.T("RecRB.BackgroundAppsOff.Cause"),
            Loc.T("RecRB.BackgroundAppsOff.Solution"),
            Loc.T("RecRB.BackgroundAppsOff.Benefit"), RiskLevel.Low, 77, Loc.T("Cat.Privacy"), "privacy.background_apps"));

        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office")) ||
            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office")) ||
            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Office")))
        {
            list.Add(Rec(
                Loc.T("RecRB.OfficeTelemetry.Title"),
                Loc.T("RecRB.OfficeTelemetry.Problem"),
                Loc.T("RecRB.OfficeTelemetry.Cause"),
                Loc.T("RecRB.OfficeTelemetry.Solution"),
                Loc.T("RecRB.OfficeTelemetry.Benefit"), RiskLevel.Low, 71, Loc.T("Cat.Privacy"), "privacy.office_telemetry"));
        }

        list.Add(Rec(
            Loc.T("RecRB.MenuDelay.Title"),
            Loc.T("RecRB.MenuDelay.Problem"),
            Loc.T("RecRB.MenuDelay.Cause"),
            Loc.T("RecRB.MenuDelay.Solution"),
            Loc.T("RecRB.MenuDelay.Benefit"), RiskLevel.Low, 62, Loc.T("Cat.Performance"), "perf.menu_delay"));
        list.Add(Rec(
            Loc.T("RecRB.NetworkThrottleOff.Title"),
            Loc.T("RecRB.NetworkThrottleOff.Problem"),
            Loc.T("RecRB.NetworkThrottleOff.Cause"),
            Loc.T("RecRB.NetworkThrottleOff.Solution"),
            Loc.T("RecRB.NetworkThrottleOff.Benefit"), RiskLevel.Medium, 84, Loc.T("Cat.Performance"), "perf.network_throttle",
            requiresElevation: true));
        list.Add(Rec(
            Loc.T("RecRB.NtfsLastAccessOff.Title"),
            Loc.T("RecRB.NtfsLastAccessOff.Problem"),
            Loc.T("RecRB.NtfsLastAccessOff.Cause"),
            Loc.T("RecRB.NtfsLastAccessOff.Solution"),
            Loc.T("RecRB.NtfsLastAccessOff.Benefit"), RiskLevel.Low, 73, Loc.T("Cat.Performance"), "perf.ntfs_lastaccess",
            requiresElevation: true));
        list.Add(Rec(
            Loc.T("RecRB.FastStartupOff.Title"),
            Loc.T("RecRB.FastStartupOff.Problem"),
            Loc.T("RecRB.FastStartupOff.Cause"),
            Loc.T("RecRB.FastStartupOff.Solution"),
            Loc.T("RecRB.FastStartupOff.Benefit"), RiskLevel.Medium, 69, Loc.T("Cat.Performance"), "perf.fast_startup_off",
            requiresElevation: true));
        // Delivery Optimization: preferente en portátil; en escritorio no auto-recomendar con tanta fuerza
        if (isLaptop || profile.ActiveProfile is OptimizationProfileKind.Work or OptimizationProfileKind.Balanced)
        {
            list.Add(Rec(
                Loc.T("RecRB.DeliveryOptLan.Title"),
                isLaptop ? Loc.T("RecRB.DeliveryOptLan.ProblemLaptop") : Loc.T("RecRB.DeliveryOptLan.ProblemProfile"),
                Loc.T("RecRB.DeliveryOptLan.Cause"),
                Loc.T("RecRB.DeliveryOptLan.Solution"),
                Loc.T("RecRB.DeliveryOptLan.Benefit"), RiskLevel.Low, 72, Loc.T("Cat.Network"), "windows.delivery_opt",
                requiresElevation: true));
        }
        else
        {
            list.Add(Rec(
                Loc.T("RecRB.DeliveryOptLanOptional.Title"),
                Loc.T("RecRB.DeliveryOptLanOptional.Problem"),
                Loc.T("RecRB.DeliveryOptLanOptional.Cause"),
                Loc.T("RecRB.DeliveryOptLanOptional.Solution"),
                Loc.T("RecRB.DeliveryOptLanOptional.Benefit"), RiskLevel.Low, 55, Loc.T("Cat.Network"), "windows.delivery_opt",
                requiresElevation: true));
        }

        // Bestia / max: más agresivo (desmarcado en Conservador)
        if (wantMax || hw.Aggressiveness >= OptimizationAggressiveness.Performance)
        {
            list.Add(Rec(
                Loc.T("RecRB.AnimationsOff.Title"),
                Loc.T("RecRB.AnimationsOff.Problem"),
                Loc.T("RecRB.AnimationsOff.Cause"),
                Loc.T("RecRB.AnimationsOff.Solution"),
                Loc.T("RecRB.AnimationsOff.Benefit"), RiskLevel.Low, 64, Loc.T("Cat.Visual"), "perf.animations_off"));
            list.Add(Rec(
                Loc.T("RecRB.XboxServicesManual.Title"),
                Loc.T("RecRB.XboxServicesManual.Problem"),
                Loc.T("RecRB.XboxServicesManual.Cause"),
                Loc.T("RecRB.XboxServicesManual.Solution"),
                Loc.T("RecRB.XboxServicesManual.Benefit"), RiskLevel.Medium, 76, Loc.T("Cat.Services"), "perf.xbox_manual",
                requiresElevation: true));
            list.Add(Rec(
                Loc.T("RecRB.AutoplayOff.Title"),
                Loc.T("RecRB.AutoplayOff.Problem"),
                Loc.T("RecRB.AutoplayOff.Cause"),
                Loc.T("RecRB.AutoplayOff.Solution"),
                Loc.T("RecRB.AutoplayOff.Benefit"), RiskLevel.Low, 58, Loc.T("Cat.System"), "perf.autoplay_off"));
            list.Add(Rec(
                Loc.T("RecRB.RemoteAssistOff.Title"),
                Loc.T("RecRB.RemoteAssistOff.Problem"),
                Loc.T("RecRB.RemoteAssistOff.Cause"),
                Loc.T("RecRB.RemoteAssistOff.Solution"),
                Loc.T("RecRB.RemoteAssistOff.Benefit"), RiskLevel.Low, 60, Loc.T("Cat.System"), "perf.remote_assist_off",
                requiresElevation: true));
        }

        if (wantMax && hw.Aggressiveness == OptimizationAggressiveness.Advanced)
        {
            list.Add(Rec(
                Loc.T("RecRB.HibernateOff.Title"),
                Loc.T("RecRB.HibernateOff.Problem"),
                Loc.T("RecRB.HibernateOff.Cause"),
                Loc.T("RecRB.HibernateOff.Solution"),
                Loc.T("RecRB.HibernateOff.Benefit"), RiskLevel.Medium, 70, Loc.T("Cat.Disk"), "perf.hibernate_off",
                requiresElevation: true));
        }

        // —— Red / limpieza baseline ——
        list.Add(Rec(
            Loc.T("RecRB.FlushDns.Title"),
            Loc.T("RecRB.FlushDns.Problem"),
            Loc.T("RecRB.FlushDns.Cause"),
            Loc.T("RecRB.FlushDns.Solution"),
            Loc.T("RecRB.FlushDns.Benefit"), RiskLevel.Low, 55, Loc.T("Cat.Network"), "net.flushdns"));

        if (hw.Aggressiveness != OptimizationAggressiveness.Conservative || snapshot.Disks.Any(d => d.UsedPercent >= 85))
        {
            list.Add(Rec(
                Loc.T("RecRB.TempCleanup.Title"),
                Loc.T("RecRB.TempCleanup.Problem"),
                Loc.T("RecRB.TempCleanup.Cause"),
                Loc.T("RecRB.TempCleanup.Solution"),
                Loc.T("RecRB.TempCleanup.Benefit"), RiskLevel.Low, 60, Loc.T("Cat.Cleanup"), "cleanup.temp"));
        }

        if (hw.PreferStartupCleanup || snapshot.ProcessCount > processCountThreshold)
        {
            list.Add(Rec(
                Loc.T("RecRB.StartupCleanup.Title"),
                Loc.T("RecRB.StartupCleanup.Problem", snapshot.ProcessCount, processCountThreshold, ramGb),
                Loc.T("RecRB.StartupCleanup.Cause"),
                Loc.T("RecRB.StartupCleanup.Solution"),
                Loc.T("RecRB.StartupCleanup.Benefit"), RiskLevel.Low, 74, Loc.T("Cat.Startup"), null));
        }

        if (snapshot.Security.DefenderEnabled == false)
        {
            list.Add(Rec(
                Loc.T("RecRB.DefenderInactive.Title"),
                Loc.T("RecRB.DefenderInactive.Problem"),
                Loc.T("RecRB.DefenderInactive.Cause"),
                Loc.T("RecRB.DefenderInactive.Solution"),
                Loc.T("RecRB.DefenderInactive.Benefit"), RiskLevel.High, 96, Loc.T("Cat.Security"), null));
        }

        foreach (var app in profile.ImportantApps)
            list.RemoveAll(r => r.Solution.Contains(app, StringComparison.OrdinalIgnoreCase));

        var dedup = list
            .GroupBy(r => r.ActionId ?? r.Title)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Risk)
            .ToList();

        return Task.FromResult<IReadOnlyList<Recommendation>>(dedup);
    }

    private static bool HasGpu(SystemSnapshot snapshot, string token)
        => snapshot.Gpu?.Name.Contains(token, StringComparison.OrdinalIgnoreCase) == true
           || snapshot.Gpus.Any(g => g.Name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string LabelAgg(OptimizationAggressiveness a) => a switch
    {
        OptimizationAggressiveness.Conservative => Loc.T("Agg.Conservative"),
        OptimizationAggressiveness.Balanced => Loc.T("Agg.Balanced"),
        OptimizationAggressiveness.Performance => Loc.T("Agg.Performance"),
        OptimizationAggressiveness.Advanced => Loc.T("Agg.Advanced"),
        OptimizationAggressiveness.Beast => Loc.T("Agg.Beast"),
        _ => Loc.T("Agg.Balanced")
    };

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

    private static Recommendation Rec(
        string title, string problem, string cause, string solution, string benefit,
        RiskLevel risk, int score, string category, string? actionId,
        bool requiresElevation = false, bool requiresReboot = false)
        => new()
        {
            Title = title,
            Problem = problem,
            ProbableCause = cause,
            Solution = solution,
            ExpectedBenefit = benefit,
            Risk = risk,
            Score = score,
            Category = category,
            ActionId = actionId,
            RequiresElevation = requiresElevation,
            RequiresReboot = requiresReboot,
            IsReversible = true
        };
}
