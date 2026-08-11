using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;

namespace AetherPC.Application.Diagnostics;

/// <summary>
/// Diagnóstico de cuellos de botella con evidencia del snapshot y benches reales.
/// No inventa porcentajes ni compara con hardware externo.
/// </summary>
public sealed class PerformanceDiagnosis : IPerformanceDiagnosis
{
    public IReadOnlyList<BottleneckFinding> Analyze(SystemSnapshot snapshot, IReadOnlyList<BenchmarkResult> recentBenches)
    {
        var list = new List<BottleneckFinding>();
        var ramPct = snapshot.Memory.UsagePercent;
        var ramGb = snapshot.Memory.TotalBytes / (1024.0 * 1024 * 1024);
        var commitPct = snapshot.Memory.CommitUsagePercent ?? 0;

        if (ramGb > 0 && ramGb < 8 && ramPct >= 75)
        {
            list.Add(new BottleneckFinding
            {
                Area = "RAM",
                TitleKey = "Diag.Bottle.RamLow",
                Evidence = $"{ramGb:F0} GB · uso {ramPct:F0}% · commit {commitPct:F0}%",
                RecommendationKey = "Diag.Rec.Ram",
                Severity = ramPct >= 90 ? "High" : "Warn",
                Confidence = "High",
                NavigateTo = "optimize"
            });
        }
        else if (ramPct >= 90)
        {
            list.Add(new BottleneckFinding
            {
                Area = "RAM",
                TitleKey = "Diag.Bottle.RamPressure",
                Evidence = $"Uso RAM {ramPct:F0}% · {snapshot.Memory.UsedBytes / (1024.0 * 1024 * 1024):F1} / {ramGb:F1} GB",
                RecommendationKey = "Diag.Rec.Processes",
                Severity = "Warn",
                Confidence = "High",
                NavigateTo = "processes"
            });
        }

        foreach (var d in snapshot.Disks.Take(3))
        {
            if (d.TotalBytes <= 0) continue;
            var freePct = d.FreeBytes * 100.0 / d.TotalBytes;
            if (freePct < 12)
            {
                list.Add(new BottleneckFinding
                {
                    Area = "Space",
                    TitleKey = "Diag.Bottle.DiskSpace",
                    Evidence = $"{d.Name} libre {freePct:F0}% ({d.FreeBytes / (1024.0 * 1024 * 1024):F1} GB)",
                    RecommendationKey = "Diag.Rec.Cleanup",
                    Severity = freePct < 5 ? "High" : "Warn",
                    Confidence = "High",
                    NavigateTo = "cleanup"
                });
            }

            var media = (d.MediaType + " " + d.DriveType).ToLowerInvariant();
            if (media.Contains("hdd") || media.Contains("rotational") || media.Contains("fixed hard"))
            {
                list.Add(new BottleneckFinding
                {
                    Area = "Disk",
                    TitleKey = "Diag.Bottle.Hdd",
                    Evidence = $"{d.Name} · {d.MediaType}/{d.DriveType}",
                    RecommendationKey = "Diag.Rec.Disk",
                    Severity = "Info",
                    Confidence = "Medium",
                    NavigateTo = "hardware"
                });
            }
        }

        var lastDisk = recentBenches.FirstOrDefault(b => b.Kind.Equals("Disk", StringComparison.OrdinalIgnoreCase));
        if (lastDisk is not null && lastDisk.Score > 0 && lastDisk.Score < 80)
        {
            list.Add(new BottleneckFinding
            {
                Area = "Disk",
                TitleKey = "Diag.Bottle.DiskSlow",
                Evidence = $"Bench Disco {lastDisk.Score} {lastDisk.Unit} ({lastDisk.CreatedAt:g})",
                RecommendationKey = "Diag.Rec.Disk",
                Severity = lastDisk.Score < 40 ? "Warn" : "Info",
                Confidence = "Medium",
                NavigateTo = "hardware"
            });
        }

        var lastCpu = recentBenches.FirstOrDefault(b => b.Kind.Equals("CPU", StringComparison.OrdinalIgnoreCase));
        if (snapshot.Cpu.UsagePercent >= 90 && lastCpu is null)
        {
            list.Add(new BottleneckFinding
            {
                Area = "CPU",
                TitleKey = "Diag.Bottle.CpuLoad",
                Evidence = $"CPU live {snapshot.Cpu.UsagePercent:F0}% · {snapshot.ProcessCount} procesos",
                RecommendationKey = "Diag.Rec.Processes",
                Severity = "Warn",
                Confidence = "Medium",
                NavigateTo = "processes"
            });
        }

        if (snapshot.Thermals.CpuCelsius is double t && t >= 90)
        {
            list.Add(new BottleneckFinding
            {
                Area = "Temp",
                TitleKey = "Diag.Bottle.Temp",
                Evidence = $"CPU {t:F0} °C",
                RecommendationKey = "Diag.Rec.Temp",
                Severity = t >= 95 ? "High" : "Warn",
                Confidence = "Medium",
                NavigateTo = "hardware"
            });
        }

        if (snapshot.Gpu is null || string.IsNullOrWhiteSpace(snapshot.Gpu.Name))
        {
            list.Add(new BottleneckFinding
            {
                Area = "GPU",
                TitleKey = "Diag.Bottle.GpuUnknown",
                Evidence = "GPU no reportada por el sistema",
                RecommendationKey = "Diag.Rec.Drivers",
                Severity = "Info",
                Confidence = "Low",
                NavigateTo = "drivers"
            });
        }

        if (list.Count == 0)
        {
            list.Add(new BottleneckFinding
            {
                Area = "Unknown",
                TitleKey = "Diag.Bottle.None",
                Evidence = "Sin señales claras en snapshot/benches recientes",
                RecommendationKey = "Diag.Rec.RunTests",
                Severity = "Info",
                Confidence = "Low",
                NavigateTo = null
            });
        }

        return list;
    }
}
