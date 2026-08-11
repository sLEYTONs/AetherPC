using AetherPC.Application.Health;
using AetherPC.Core.Models;

namespace AetherPC.Tests;

public class HealthScorerTests
{
    [Fact]
    public void Score_HealthySystem_IsHigh()
    {
        var scorer = new HealthScorer();
        var snap = new SystemSnapshot
        {
            Memory = new MemoryInfo { TotalBytes = 16UL * 1024 * 1024 * 1024, AvailableBytes = 8UL * 1024 * 1024 * 1024 },
            Cpu = new CpuInfo { UsagePercent = 20 },
            Disks = new[]
            {
                new DiskInfo { DriveLetter = "C:", TotalBytes = 500UL * 1024 * 1024 * 1024, FreeBytes = 200UL * 1024 * 1024 * 1024 }
            },
            ProcessCount = 80,
            Security = new SecurityInfo
            {
                DefenderEnabled = true,
                FirewallEnabled = true,
                SecureBootEnabled = true,
                TpmPresent = true
            },
            Thermals = new ThermalInfo { CpuCelsius = 55 }
        };

        var (score, factors) = scorer.Score(snap);
        Assert.True(score >= 80);
        Assert.All(factors, f => Assert.True(f.IsAvailable));
    }

    [Fact]
    public void Score_MissingTemp_IsMarkedUnavailable_NotInvented()
    {
        var scorer = new HealthScorer();
        var snap = new SystemSnapshot
        {
            Memory = new MemoryInfo { TotalBytes = 8UL * 1024 * 1024 * 1024, AvailableBytes = 4UL * 1024 * 1024 * 1024 },
            Cpu = new CpuInfo { UsagePercent = 10 },
            Disks = new[]
            {
                new DiskInfo { DriveLetter = "C:", TotalBytes = 100UL * 1024 * 1024 * 1024, FreeBytes = 40UL * 1024 * 1024 * 1024 }
            },
            ProcessCount = 50,
            Security = new SecurityInfo { DefenderEnabled = true, FirewallEnabled = true },
            Thermals = new ThermalInfo()
        };

        var (_, factors) = scorer.Score(snap);
        var temps = factors.First(f => f.Name == "Temps");
        Assert.False(temps.IsAvailable);
        Assert.Equal(0, temps.Weight);
        Assert.Equal("N/D", temps.ScoreText);
    }

    [Fact]
    public void Score_UnknownSecurity_DoesNotInventFifty()
    {
        var scorer = new HealthScorer();
        var snap = new SystemSnapshot
        {
            Memory = new MemoryInfo { TotalBytes = 8UL * 1024 * 1024 * 1024, AvailableBytes = 4UL * 1024 * 1024 * 1024 },
            Cpu = new CpuInfo { UsagePercent = 10 },
            Disks = Array.Empty<DiskInfo>(),
            ProcessCount = 50,
            Security = new SecurityInfo(),
            Thermals = new ThermalInfo { CpuCelsius = 60 }
        };

        var (_, factors) = scorer.Score(snap);
        var sec = factors.First(f => f.Name == "Security");
        Assert.False(sec.IsAvailable);
        Assert.DoesNotContain(factors, f => f.Name == "Security" && f.IsAvailable && f.Score == 50);
    }

    [Fact]
    public void Score_LowDisk_ReducesStorageFactor()
    {
        var scorer = new HealthScorer();
        var snap = new SystemSnapshot
        {
            Memory = new MemoryInfo { TotalBytes = 8UL * 1024 * 1024 * 1024, AvailableBytes = 4UL * 1024 * 1024 * 1024 },
            Cpu = new CpuInfo { UsagePercent = 10 },
            Disks = new[]
            {
                new DiskInfo { DriveLetter = "C:", TotalBytes = 100UL * 1024 * 1024 * 1024, FreeBytes = 2UL * 1024 * 1024 * 1024 }
            },
            ProcessCount = 50,
            Security = new SecurityInfo { DefenderEnabled = true },
            Thermals = new ThermalInfo { CpuCelsius = 50 }
        };

        var (_, factors) = scorer.Score(snap);
        var storage = factors.First(f => f.Name == "Disks");
        Assert.True(storage.IsAvailable);
        Assert.True(storage.Score <= 50);
    }
}
