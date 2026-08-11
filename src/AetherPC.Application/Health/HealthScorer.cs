using AetherPC.Core.Localization;
using AetherPC.Core.Models;

namespace AetherPC.Application.Health;

/// <summary>
/// Puntuación de salud solo con métricas reales del snapshot.
/// Si un dato no está disponible, el factor se marca N/D (peso 0) — nunca se inventa un score.
/// </summary>
public sealed class HealthScorer : AetherPC.Core.Abstractions.IHealthScorer
{
    public (int Score, IReadOnlyList<HealthFactor> Factors) Score(SystemSnapshot snapshot)
    {
        var factors = new List<HealthFactor>();

        if (snapshot.Memory.TotalBytes > 0)
        {
            var used = snapshot.Memory.UsagePercent;
            var memScore = used switch
            {
                < 70 => 100,
                < 85 => 75,
                < 95 => 45,
                _ => 20
            };
            factors.Add(new HealthFactor
            {
                Name = "RAM",
                Score = memScore,
                Weight = 20,
                IsAvailable = true,
                Detail = Loc.T("Health.RamDetail", used, FormatBytes(snapshot.Memory.UsedBytes), FormatBytes(snapshot.Memory.TotalBytes))
            });
        }
        else
        {
            factors.Add(Unavailable("RAM", Loc.T("Health.RamMissing")));
        }

        var disks = snapshot.Disks.Where(x => x.TotalBytes > 0 && !string.IsNullOrWhiteSpace(x.DriveLetter)).ToList();
        if (disks.Count > 0)
        {
            var diskScore = 100;
            foreach (var d in disks)
            {
                if (d.UsedPercent > 95) diskScore = Math.Min(diskScore, 25);
                else if (d.UsedPercent > 90) diskScore = Math.Min(diskScore, 50);
                else if (d.UsedPercent > 80) diskScore = Math.Min(diskScore, 75);
            }
            factors.Add(new HealthFactor
            {
                Name = "Disks",
                Score = diskScore,
                Weight = 20,
                IsAvailable = true,
                Detail = string.Join(", ", disks.Select(d => Loc.T("Health.DiskUsed", d.DriveLetter, d.UsedPercent)))
            });
        }
        else
        {
            factors.Add(Unavailable("Disks", Loc.T("Health.DiskMissing")));
        }

        {
            var cpu = snapshot.Cpu.UsagePercent;
            var cpuScore = cpu switch
            {
                < 60 => 100,
                < 85 => 70,
                < 95 => 40,
                _ => 25
            };
            factors.Add(new HealthFactor
            {
                Name = "CPU",
                Score = cpuScore,
                Weight = 15,
                IsAvailable = true,
                Detail = Loc.T("Health.CpuDetail", cpu)
            });
        }

        var temp = snapshot.Thermals.CpuCelsius ?? snapshot.Cpu.TemperatureCelsius;
        if (temp is not null)
        {
            var t = temp.Value;
            var tempScore = t switch
            {
                > 95 => 20,
                > 85 => 45,
                > 75 => 70,
                _ => 100
            };
            factors.Add(new HealthFactor
            {
                Name = "Temps",
                Score = tempScore,
                Weight = 15,
                IsAvailable = true,
                Detail = Loc.T("Health.TempDetail", t)
            });
        }
        else
        {
            factors.Add(Unavailable("Temps",
                string.IsNullOrWhiteSpace(snapshot.Thermals.Note)
                    ? Loc.T("Health.TempMissing")
                    : snapshot.Thermals.Note!));
        }

        var known = new List<(string Label, bool Ok)>();
        void AddBit(string label, bool? v)
        {
            if (v is null) return;
            known.Add((label, v.Value));
        }
        AddBit("Defender", snapshot.Security.DefenderEnabled);
        AddBit("Firewall", snapshot.Security.FirewallEnabled);
        AddBit("Secure Boot", snapshot.Security.SecureBootEnabled);
        AddBit("TPM", snapshot.Security.TpmPresent);

        if (known.Count > 0)
        {
            var ok = known.Count(k => k.Ok);
            var secScore = (int)Math.Round(ok * 100.0 / known.Count);
            factors.Add(new HealthFactor
            {
                Name = "Security",
                Score = secScore,
                Weight = 15,
                IsAvailable = true,
                Detail = string.Join(" · ", known.Select(k => $"{k.Label}:{(k.Ok ? "OK" : "Off")}"))
            });
        }
        else
        {
            factors.Add(Unavailable("Security", Loc.T("Health.SecMissing")));
        }

        if (snapshot.ProcessCount > 0)
        {
            var n = snapshot.ProcessCount;
            var procScore = n switch
            {
                < 120 => 100,
                < 200 => 80,
                < 250 => 55,
                < 300 => 45,
                _ => 35
            };
            factors.Add(new HealthFactor
            {
                Name = "Processes",
                Score = procScore,
                Weight = 15,
                IsAvailable = true,
                Detail = Loc.T("Health.ProcDetail", n)
            });
        }
        else
        {
            factors.Add(Unavailable("Processes", Loc.T("Health.ProcMissing")));
        }

        var usable = factors.Where(f => f.IsAvailable && f.Weight > 0).ToList();
        if (usable.Count == 0)
            return (0, factors);

        var weighted = usable.Sum(f => f.Score * f.Weight);
        var totalWeight = usable.Sum(f => f.Weight);
        var score = (int)Math.Round(weighted / (double)totalWeight);
        return (Math.Clamp(score, 0, 100), factors);
    }

    private static HealthFactor Unavailable(string name, string detail) => new()
    {
        Name = name,
        Score = 0,
        Weight = 0,
        IsAvailable = false,
        Detail = detail
    };

    private static string FormatBytes(ulong b)
        => b >= 1024UL * 1024 * 1024
            ? $"{b / (1024.0 * 1024 * 1024):F1} GB"
            : $"{b / (1024.0 * 1024):F0} MB";
}
