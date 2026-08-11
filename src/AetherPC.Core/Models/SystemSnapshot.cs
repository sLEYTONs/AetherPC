using AetherPC.Core.Enums;
using AetherPC.Core.Localization;

namespace AetherPC.Core.Models;

public sealed class SystemSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public ScanDepthUsed Depth { get; init; } = ScanDepthUsed.Fast;
    public OsInfo Os { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public GpuInfo? Gpu { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public MemoryInfo Memory { get; init; } = new();
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();
    public MotherboardInfo Motherboard { get; init; } = new();
    public BiosInfo Bios { get; init; } = new();
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = Array.Empty<MonitorInfo>();
    public NetworkInfo Network { get; init; } = new();
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = Array.Empty<NetworkAdapterInfo>();
    public SecurityInfo Security { get; set; } = new();
    public ThermalInfo Thermals { get; init; } = new();
    public int ProcessCount { get; init; }
    public TimeSpan Uptime { get; init; }
    public int HealthScore { get; set; }
    public IReadOnlyList<HealthFactor> HealthFactors { get; set; } = Array.Empty<HealthFactor>();
    public IReadOnlyList<string> DetectionNotes { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, double>? StageTimingsMs { get; init; }
    /// <summary>true = portátil (batería detectada), false = escritorio, null = no determinado.</summary>
    public bool? IsPortable { get; init; }
}

public enum ScanDepthUsed
{
    Live = 0,
    Fast = 1,
    Deep = 2
}

public sealed class OsInfo
{
    public string MachineName { get; init; } = Environment.MachineName;
    public string UserName { get; init; } = Environment.UserName;
    public string Caption { get; init; } = NotDetected.Text;
    public string Version { get; init; } = NotDetected.Text;
    public string Build { get; init; } = NotDetected.Text;
    public string Edition { get; init; } = NotDetected.Text;
    public string Architecture { get; init; } = Environment.Is64BitOperatingSystem ? "x64" : "x86";
    public bool IsElevated { get; init; }
    public string Source { get; init; } = "";
}

public sealed record CpuInfo
{
    public string Name { get; init; } = NotDetected.Text;
    public string Manufacturer { get; init; } = NotDetected.Text;
    public string Architecture { get; init; } = NotDetected.Text;
    public int Cores { get; init; }
    public int LogicalProcessors { get; init; }
    public double? MaxClockMhz { get; init; }
    public double? CurrentClockMhz { get; init; }
    public double UsagePercent { get; init; }
    public double? TemperatureCelsius { get; init; }
    public FeatureAvailability TemperatureAvailability { get; init; } = FeatureAvailability.Unavailable;
    public string Source { get; init; } = "";
}

public sealed record GpuInfo
{
    public string Name { get; init; } = NotDetected.Text;
    public string? DriverVersion { get; init; }
    public ulong? AdapterRamBytes { get; init; }
    public double? UsagePercent { get; init; }
    public double? TemperatureCelsius { get; init; }
    public bool IsLikelyIntegrated { get; init; }
    public FeatureAvailability TemperatureAvailability { get; init; } = FeatureAvailability.Unavailable;
    public FeatureAvailability UsageAvailability { get; init; } = FeatureAvailability.Limited;
    public string Source { get; init; } = "";
}

public sealed record MemoryModuleInfo
{
    public string BankLabel { get; init; } = "";
    public string DeviceLocator { get; init; } = "";
    public ulong CapacityBytes { get; init; }
    public double? SpeedMhz { get; init; }
    public string? MemoryType { get; init; }
    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
    public string? SerialNumber { get; init; }
}

public sealed record MemoryInfo
{
    public ulong TotalBytes { get; init; }
    public ulong AvailableBytes { get; init; }
    public ulong UsedBytes => TotalBytes > AvailableBytes ? TotalBytes - AvailableBytes : 0;
    public double UsagePercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
    /// <summary>Carga de memoria 0–100 desde GlobalMemoryStatusEx.</summary>
    public uint? MemoryLoadPercent { get; init; }
    /// <summary>Límite de commit (RAM + pagefile) — ullTotalPageFile.</summary>
    public ulong? CommitLimitBytes { get; init; }
    /// <summary>Commit disponible — ullAvailPageFile.</summary>
    public ulong? CommitAvailableBytes { get; init; }
    public ulong? CommitUsedBytes =>
        CommitLimitBytes is null || CommitAvailableBytes is null || CommitLimitBytes < CommitAvailableBytes
            ? null
            : CommitLimitBytes - CommitAvailableBytes;
    public double? CommitUsagePercent =>
        CommitLimitBytes is null or 0 || CommitUsedBytes is null
            ? null
            : CommitUsedBytes.Value * 100.0 / CommitLimitBytes.Value;
    /// <summary>true = System managed, false = manual, null = no detectado.</summary>
    public bool? PageFileSystemManaged { get; init; }
    public string? PageFileConfigDetail { get; init; }
    /// <summary>Bytes comprimidos si el contador está disponible.</summary>
    public ulong? CompressedBytes { get; init; }
    public double? SpeedMhz { get; init; }
    public string? MemoryType { get; init; }
    public int? SlotCount { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = Array.Empty<MemoryModuleInfo>();
    public string Source { get; init; } = "";
}

public sealed record DiskInfo
{
    public string Name { get; init; } = NotDetected.Text;
    public string DriveLetter { get; init; } = "";
    public string DriveType { get; init; } = NotDetected.Text;
    public string MediaType { get; init; } = NotDetected.Text;
    public string? Model { get; init; }
    public string? BusType { get; init; }
    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }
    public double UsedPercent => TotalBytes == 0 ? 0 : (TotalBytes - FreeBytes) * 100.0 / TotalBytes;
    public double? TemperatureCelsius { get; init; }
    public FeatureAvailability SmartAvailability { get; init; } = FeatureAvailability.Limited;
    public string? HealthStatus { get; init; }
    public string Source { get; init; } = "";
}

public sealed record MotherboardInfo
{
    public string Manufacturer { get; init; } = NotDetected.Text;
    public string Product { get; init; } = NotDetected.Text;
    public string? SerialNumber { get; init; }
    public string? Version { get; init; }
    public string Source { get; init; } = "";
}

public sealed record BiosInfo
{
    public string Vendor { get; init; } = NotDetected.Text;
    public string Version { get; init; } = NotDetected.Text;
    public string? ReleaseDate { get; init; }
    public string? SmbiosVersion { get; init; }
    public string Source { get; init; } = "";
}

public sealed class MonitorInfo
{
    public string Name { get; init; } = NotDetected.Text;
    public string? Manufacturer { get; init; }
    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }
    public double? RefreshHz { get; init; }
    public bool IsPrimary { get; init; }
    public string Source { get; init; } = "";
}

public sealed class NetworkInfo
{
    public string? PrimaryAdapter { get; init; }
    public string? IPv4 { get; init; }
    public bool IsConnected { get; init; }
    public long BytesSentPerSec { get; init; }
    public long BytesReceivedPerSec { get; init; }
    public string Source { get; init; } = "";
}

public sealed class NetworkAdapterInfo
{
    public string Name { get; init; } = NotDetected.Text;
    public string Type { get; init; } = NotDetected.Text;
    public string Status { get; init; } = NotDetected.Text;
    public string? Speed { get; init; }
    public string? Mac { get; init; }
    public string? IPv4 { get; init; }
    public string? Gateway { get; init; }
    public bool IsVirtual { get; init; }
    public string Source { get; init; } = "NetworkInterface";
}

public sealed class SecurityInfo
{
    public bool? DefenderEnabled { get; init; }
    public bool? FirewallEnabled { get; init; }
    public bool? FirewallDomainOn { get; init; }
    public bool? FirewallPrivateOn { get; init; }
    public bool? FirewallPublicOn { get; init; }
    public bool? SecureBootEnabled { get; init; }
    public bool? BitLockerActive { get; init; }
    public string? BitLockerDetail { get; init; }
    public bool? TpmPresent { get; init; }
    public string? TpmVersion { get; init; }
    public string? TpmManufacturer { get; init; }
    public bool? SmartScreenEnabled { get; init; }
    public bool? MemoryIntegrityEnabled { get; init; }
    public bool? UacEnabled { get; init; }
    public string? UacLevel { get; init; }
    public bool? CredentialGuardEnabled { get; init; }
    public bool? VirtualizationBasedSecurity { get; init; }
    public string Source { get; init; } = "";
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;
}

public sealed class ThermalInfo
{
    public double? CpuCelsius { get; init; }
    public double? GpuCelsius { get; init; }
    public FeatureAvailability Availability { get; init; } = FeatureAvailability.Unavailable;
    public string? Note { get; init; }
    public string Source { get; init; } = "";
}

public sealed class HealthFactor
{
    public string Name { get; init; } = string.Empty;
    public int Score { get; init; }
    public int Weight { get; init; }
    public string Detail { get; init; } = string.Empty;
    /// <summary>false = sin dato fiable; no entra en la media ni se inventa puntuación.</summary>
    public bool IsAvailable { get; init; } = true;
    public string ScoreText => IsAvailable ? Score.ToString() : "N/D";
}

/// <summary>Texto canónico cuando no hay fuente fiable (sigue el idioma de la app).</summary>
public static class NotDetected
{
    public static string Text => Loc.T("Common.NotDetected");
}
