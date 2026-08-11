using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AetherPC.Infrastructure.Windows;

public sealed class WindowsSystemScanner : ISystemScanner
{
    private readonly ISensorService _sensors;
    private readonly ILogger<WindowsSystemScanner> _logger;
    private readonly PerformanceCounter? _cpuCounter;
    private readonly object _invLock = new();

    private InventoryCache? _inventory;
    private DateTimeOffset _inventoryAt;
    private static readonly TimeSpan InventoryTtl = TimeSpan.FromMinutes(5);

    private double _lastCpuUsage;
    private DateTimeOffset _cpuPrimedAt = DateTimeOffset.MinValue;

    public WindowsSystemScanner(ISensorService sensors, ILogger<WindowsSystemScanner> logger)
    {
        _sensors = sensors;
        _logger = logger;
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ = _cpuCounter.NextValue();
            _cpuPrimedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PerformanceCounter CPU no disponible");
        }

        // Precalienta sensores fuera del camino crítico
        _ = Task.Run(async () =>
        {
            try { await _sensors.WarmupAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Warmup sensores"); }
        });
    }

    public async Task<SystemSnapshot> CaptureSnapshotAsync(ScanDepth depth = ScanDepth.Fast, CancellationToken ct = default)
    {
        var swTotal = Stopwatch.StartNew();
        var timings = new ConcurrentDictionary<string, double>();
        var notes = new ConcurrentBag<string>();

        if (depth == ScanDepth.Live)
            return await CaptureLiveAsync(timings, notes, ct);

        var inv = await GetOrBuildInventoryAsync(force: depth == ScanDepth.Deep, timings, notes, ct);

        // Métricas volátiles + seguridad ligera; deep añade sensores/placa/monitores/discos
        var liveTask = Task.Run(() => ReadLiveMetrics(timings), ct);
        var securityTask = Task.Run(() =>
        {
            var s = Stopwatch.StartNew();
            var sec = ReadSecurityLight(notes);
            timings["security"] = s.Elapsed.TotalMilliseconds;
            return sec;
        }, ct);

        Task<ThermalInfo>? thermalTask = null;
        Task<(MotherboardInfo Mb, BiosInfo Bios)>? boardTask = null;
        Task<IReadOnlyList<MonitorInfo>>? monitorsTask = null;
        Task<IReadOnlyList<DiskInfo>>? diskEnrichTask = null;

        if (depth == ScanDepth.Deep)
        {
            thermalTask = Task.Run(async () =>
            {
                var s = Stopwatch.StartNew();
                var t = await _sensors.ReadThermalsAsync(ct);
                timings["sensors"] = s.Elapsed.TotalMilliseconds;
                return t;
            }, ct);

            boardTask = Task.Run(() =>
            {
                var s = Stopwatch.StartNew();
                var r = ReadBoardAndBios(notes);
                timings["board_bios"] = s.Elapsed.TotalMilliseconds;
                return r;
            }, ct);

            monitorsTask = Task.Run(() =>
            {
                var s = Stopwatch.StartNew();
                var m = ReadMonitors(notes);
                timings["monitors"] = s.Elapsed.TotalMilliseconds;
                return m;
            }, ct);

            diskEnrichTask = Task.Run(() =>
            {
                var s = Stopwatch.StartNew();
                var d = EnrichDisks(inv.Disks, notes);
                timings["disk_media"] = s.Elapsed.TotalMilliseconds;
                return d;
            }, ct);
        }

        var live = await liveTask;
        var security = await securityTask;
        var disks = inv.Disks;
        var thermals = new ThermalInfo
        {
            Availability = FeatureAvailability.Unavailable,
            Note = depth == ScanDepth.Fast
                ? Loc.T("Sensors.Deferred")
                : Loc.T("Sensors.Loading"),
            Source = "pending"
        };
        var motherboard = inv.Motherboard;
        var bios = inv.Bios;
        var monitors = inv.Monitors;

        if (depth == ScanDepth.Deep)
        {
            if (thermalTask is not null) thermals = await thermalTask;
            if (boardTask is not null)
            {
                var bb = await boardTask;
                motherboard = bb.Mb;
                bios = bb.Bios;
                lock (_invLock)
                {
                    if (_inventory is not null)
                        _inventory = _inventory with { Motherboard = motherboard, Bios = bios };
                }
            }
            if (monitorsTask is not null)
            {
                monitors = await monitorsTask;
                lock (_invLock)
                {
                    if (_inventory is not null)
                        _inventory = _inventory with { Monitors = monitors };
                }
            }
            if (diskEnrichTask is not null)
            {
                disks = await diskEnrichTask;
                lock (_invLock)
                {
                    if (_inventory is not null)
                        _inventory = _inventory with { Disks = disks };
                }
            }
        }

        var cpu = inv.Cpu with
        {
            UsagePercent = live.CpuUsage,
            TemperatureCelsius = thermals.CpuCelsius,
            TemperatureAvailability = thermals.CpuCelsius is null
                ? FeatureAvailability.Unavailable
                : FeatureAvailability.Available
        };

        var gpus = inv.Gpus.Select(g => g with
        {
            TemperatureCelsius = thermals.GpuCelsius,
            TemperatureAvailability = thermals.GpuCelsius is null
                ? FeatureAvailability.Unavailable
                : FeatureAvailability.Available
        }).ToList();

        var primaryGpu = PickPrimaryGpu(gpus);

        timings["total"] = swTotal.Elapsed.TotalMilliseconds;
        _logger.LogInformation("Snapshot {Depth} en {Ms:F0} ms", depth, timings["total"]);

        return new SystemSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Depth = depth == ScanDepth.Deep ? ScanDepthUsed.Deep : ScanDepthUsed.Fast,
            Os = inv.Os,
            Cpu = cpu,
            Gpu = primaryGpu,
            Gpus = gpus,
            Memory = live.Memory with
            {
                SpeedMhz = inv.MemoryMeta.SpeedMhz,
                MemoryType = inv.MemoryMeta.MemoryType,
                SlotCount = inv.MemoryMeta.SlotCount,
                Modules = inv.MemoryMeta.Modules,
                Source = string.IsNullOrEmpty(inv.MemoryMeta.Source) ? live.Memory.Source : $"{live.Memory.Source}+{inv.MemoryMeta.Source}"
            },
            Disks = disks,
            Motherboard = motherboard,
            Bios = bios,
            Monitors = monitors,
            Network = inv.Network,
            NetworkAdapters = inv.Adapters,
            Security = security,
            Thermals = thermals,
            ProcessCount = live.ProcessCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            DetectionNotes = notes.ToArray(),
            StageTimingsMs = new Dictionary<string, double>(timings),
            IsPortable = inv.IsPortable
        };
    }

    private async Task<SystemSnapshot> CaptureLiveAsync(
        ConcurrentDictionary<string, double> timings,
        ConcurrentBag<string> notes,
        CancellationToken ct)
    {
        var inv = await GetOrBuildInventoryAsync(force: false, timings, notes, ct);
        var live = ReadLiveMetrics(timings);
        return new SystemSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Depth = ScanDepthUsed.Live,
            Os = inv.Os,
            Cpu = inv.Cpu with { UsagePercent = live.CpuUsage },
            Gpu = PickPrimaryGpu(inv.Gpus),
            Gpus = inv.Gpus,
            Memory = live.Memory with
            {
                SpeedMhz = inv.MemoryMeta.SpeedMhz,
                MemoryType = inv.MemoryMeta.MemoryType,
                SlotCount = inv.MemoryMeta.SlotCount,
                Modules = inv.MemoryMeta.Modules
            },
            Disks = inv.Disks,
            Motherboard = inv.Motherboard,
            Bios = inv.Bios,
            Monitors = inv.Monitors,
            Network = inv.Network,
            NetworkAdapters = inv.Adapters,
            Security = new SecurityInfo { Source = "live-skip" },
            Thermals = new ThermalInfo
            {
                Availability = FeatureAvailability.Unavailable,
                Note = "Live metrics (sin sensores)",
                Source = "live"
            },
            ProcessCount = live.ProcessCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            DetectionNotes = notes.ToArray(),
            StageTimingsMs = new Dictionary<string, double>(timings),
            IsPortable = inv.IsPortable
        };
    }

    private async Task<InventoryCache> GetOrBuildInventoryAsync(
        bool force,
        ConcurrentDictionary<string, double> timings,
        ConcurrentBag<string> notes,
        CancellationToken ct)
    {
        lock (_invLock)
        {
            if (!force && _inventory is not null && DateTimeOffset.Now - _inventoryAt < InventoryTtl)
                return _inventory;
        }

        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var inv = BuildInventory(notes);
            timings["inventory"] = sw.Elapsed.TotalMilliseconds;
            lock (_invLock)
            {
                _inventory = inv;
                _inventoryAt = DateTimeOffset.Now;
            }
            return inv;
        }, ct);
    }

    private InventoryCache BuildInventory(ConcurrentBag<string> notes)
    {
        // Paralelo seguro: WMI COM suele ir mejor por hilo dedicado; usamos Task.Run separados
        var osTask = Task.Run(() => ReadOs(notes));
        var cpuTask = Task.Run(() => ReadCpuInventory(notes));
        var gpuTask = Task.Run(() => ReadGpus(notes));
        var memMetaTask = Task.Run(() => ReadMemoryMeta(notes));
        var diskTask = Task.Run(() => ReadDisksFast(notes));
        var netTask = Task.Run(() => ReadNetworkAll(notes));
        var portableTask = Task.Run(() => DetectIsPortable(notes));

        Task.WaitAll(osTask, cpuTask, gpuTask, memMetaTask, diskTask, netTask, portableTask);

        return new InventoryCache(
            osTask.Result,
            cpuTask.Result,
            gpuTask.Result,
            memMetaTask.Result,
            diskTask.Result,
            new MotherboardInfo(),
            new BiosInfo(),
            Array.Empty<MonitorInfo>(),
            netTask.Result.Primary,
            netTask.Result.Adapters,
            portableTask.Result);
    }

    private static bool? DetectIsPortable(ConcurrentBag<string> notes)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            foreach (ManagementObject _ in searcher.Get())
                return true;
            return false;
        }
        catch (Exception ex)
        {
            notes.Add($"Chassis/batería: {ex.Message}");
            return null;
        }
    }

    private (double CpuUsage, MemoryInfo Memory, int ProcessCount) ReadLiveMetrics(
        ConcurrentDictionary<string, double> timings)
    {
        var sw = Stopwatch.StartNew();
        var cpu = ReadCpuUsage();
        var mem = ReadMemoryNative(notes: null);
        var proc = 0;
        try { proc = Process.GetProcesses().Length; } catch { /* ignore */ }
        timings["live"] = sw.Elapsed.TotalMilliseconds;
        return (cpu, mem, proc);
    }

    private double ReadCpuUsage()
    {
        try
        {
            if (_cpuCounter is null) return _lastCpuUsage;
            // Sin Sleep: la 1ª tras prime puede ser 0; usamos última válida
            var v = _cpuCounter.NextValue();
            if (DateTimeOffset.Now - _cpuPrimedAt < TimeSpan.FromMilliseconds(200) && v < 0.1)
                return _lastCpuUsage;
            _lastCpuUsage = Math.Clamp(v, 0, 100);
            return _lastCpuUsage;
        }
        catch { return _lastCpuUsage; }
    }

    private OsInfo ReadOs(ConcurrentBag<string> notes)
    {
        var caption = NotDetected.Text;
        var version = Environment.OSVersion.Version.ToString();
        var build = Environment.OSVersion.Version.Build.ToString();
        var edition = NotDetected.Text;
        var source = "Environment";

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                caption = obj["Caption"]?.ToString()?.Trim() ?? caption;
                version = obj["Version"]?.ToString() ?? version;
                build = obj["BuildNumber"]?.ToString() ?? build;
                source = "WMI:Win32_OperatingSystem";
                break;
            }
        }
        catch (Exception ex)
        {
            notes.Add($"OS WMI: {ex.Message}");
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key is not null)
                {
                    caption = key.GetValue("ProductName")?.ToString() ?? caption;
                    build = key.GetValue("CurrentBuildNumber")?.ToString() ?? build;
                    edition = key.GetValue("EditionID")?.ToString() ?? edition;
                    source = "Registry:CurrentVersion";
                }
            }
            catch { /* keep */ }
        }

        if (caption == NotDetected.Text)
            notes.Add("OS: No detectado por el sistema");

        return new OsInfo
        {
            Caption = caption,
            Version = version,
            Build = build,
            Edition = edition,
            IsElevated = IsElevated(),
            Source = source
        };
    }

    private CpuInfo ReadCpuInventory(ConcurrentBag<string> notes)
    {
        string name = NotDetected.Text, manufacturer = NotDetected.Text, arch = NotDetected.Text;
        int cores = 0, logical = Environment.ProcessorCount;
        double? maxMhz = null;
        double? currentMhz = null;
        var sources = new List<string>();

        // Capa OS / env
        try
        {
            var envName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(envName))
            {
                name = envName;
                sources.Add("Env:PROCESSOR_IDENTIFIER");
            }
            arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? arch;
        }
        catch { /* ignore */ }

        // Capa Registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var regName = key?.GetValue("ProcessorNameString")?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(regName))
            {
                name = regName;
                sources.Add("Registry:CentralProcessor");
            }
            if (key?.GetValue("~MHz") is int mhz) maxMhz = mhz;
            var vendor = key?.GetValue("VendorIdentifier")?.ToString();
            if (!string.IsNullOrWhiteSpace(vendor)) manufacturer = vendor;
        }
        catch (Exception ex) { notes.Add($"CPU Registry: {ex.Message}"); }

        // Capa WMI
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, Architecture FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                var wmiName = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(wmiName)) name = wmiName;
                manufacturer = obj["Manufacturer"]?.ToString() ?? manufacturer;
                if (obj["NumberOfCores"] is not null) cores = Convert.ToInt32(obj["NumberOfCores"]);
                if (obj["NumberOfLogicalProcessors"] is not null) logical = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                if (obj["MaxClockSpeed"] is not null) maxMhz = Convert.ToDouble(obj["MaxClockSpeed"]);
                if (obj["CurrentClockSpeed"] is not null) currentMhz = Convert.ToDouble(obj["CurrentClockSpeed"]);
                arch = obj["Architecture"]?.ToString() switch
                {
                    "0" => "x86",
                    "9" => "x64",
                    "12" => "ARM64",
                    _ => arch
                };
                sources.Add("WMI:Win32_Processor");
                break;
            }
        }
        catch (Exception ex) { notes.Add($"CPU WMI: {ex.Message}"); }

        if (cores <= 0) cores = Math.Max(1, logical / 2);
        if (name == NotDetected.Text) notes.Add("CPU: No detectado por el sistema");

        return new CpuInfo
        {
            Name = name,
            Manufacturer = manufacturer,
            Architecture = string.IsNullOrWhiteSpace(arch) ? NotDetected.Text : arch,
            Cores = cores,
            LogicalProcessors = logical,
            MaxClockMhz = maxMhz,
            CurrentClockMhz = currentMhz,
            Source = string.Join("+", sources.Distinct())
        };
    }

    private IReadOnlyList<GpuInfo> ReadGpus(ConcurrentBag<string> notes)
    {
        var list = new List<GpuInfo>();
        var skippedVirtual = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID, VideoProcessor, Status FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pnp = obj["PNPDeviceID"]?.ToString() ?? "";
                if (IsVirtualOrSoftwareGpu(name, pnp))
                {
                    skippedVirtual.Add(name);
                    continue;
                }

                ulong? ram = null;
                try
                {
                    if (obj["AdapterRAM"] is not null)
                    {
                        var raw = Convert.ToInt64(obj["AdapterRAM"]);
                        if (raw > 0) ram = (ulong)raw;
                    }
                }
                catch { /* ignore overflow */ }

                var vendor = DetectGpuVendor(name, pnp);
                var integrated = IsIntegratedGpu(name, pnp, vendor);
                list.Add(new GpuInfo
                {
                    Name = name,
                    DriverVersion = obj["DriverVersion"]?.ToString() ?? NotDetected.Text,
                    AdapterRamBytes = ram,
                    IsLikelyIntegrated = integrated,
                    Source = $"WMI:Win32_VideoController/{vendor}"
                });
            }
        }
        catch (Exception ex)
        {
            notes.Add($"GPU WMI: {ex.Message}");
        }

        if (skippedVirtual.Count > 0)
            notes.Add("GPU virtual omitida: " + string.Join(", ", skippedVirtual.Distinct()));

        if (list.Count == 0)
            notes.Add("GPU: No detectado por el sistema");

        // Discreta PCI primero (NVIDIA/AMD), luego iGPU, luego resto por VRAM
        return list
            .OrderBy(g => GpuSortRank(g))
            .ThenByDescending(g => g.AdapterRamBytes ?? 0)
            .ToList();
    }

    private static int GpuSortRank(GpuInfo g)
    {
        var n = g.Name;
        var discreteNvidia = n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("GTX", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("Quadro", StringComparison.OrdinalIgnoreCase);
        var discreteAmd = (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                          && !g.IsLikelyIntegrated;
        var discreteIntelArc = n.Contains("Arc", StringComparison.OrdinalIgnoreCase);

        if (discreteNvidia) return 0;
        if (discreteAmd) return 1;
        if (discreteIntelArc) return 2;
        if (!g.IsLikelyIntegrated) return 3;
        return 4; // iGPU
    }

    private static bool IsVirtualOrSoftwareGpu(string name, string pnpDeviceId)
    {
        // Bus virtual ROOT\DISPLAY (Parsec, etc.)
        if (pnpDeviceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase))
            return true;
        if (pnpDeviceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase))
            return true;

        ReadOnlySpan<string> needles =
        [
            "Parsec", "Virtual Display", "Virtual Desktop", "Remote Display",
            "Remote Desktop", "Microsoft Basic", "Microsoft Remote",
            "TeamViewer", "AnyDesk", "VMware", "VirtualBox", "VBox",
            "Hyper-V", "Citrix", "Indirect Display", "IddCx",
            "Moonlight", "Sunshine", "Steam Link", "Radmin",
            "USB Display", "Mirage Driver", "DisplayLink Soft",
            "OneScreen", "spacedesk", "Air Display", "Deskreen"
        ];

        foreach (var n in needles)
        {
            if (name.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string DetectGpuVendor(string name, string pnp)
    {
        if (pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA";
        if (pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
            pnp.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        if (pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "Intel";
        return "Unknown";
    }

    private static bool IsIntegratedGpu(string name, string pnp, string vendor)
    {
        if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return false; // Intel Arc = discreta

        if (vendor == "Intel")
            return true; // UHD / Iris / Xe en laptops = iGPU

        if (name.Contains("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase))
            return true;

        // Sin VEN_PCI típico de GPU de bus → sospechoso, ya filtrado ROOT
        return false;
    }

    private static GpuInfo? PickPrimaryGpu(IReadOnlyList<GpuInfo> gpus)
        => gpus.FirstOrDefault(); // ya ordenado: discreta real primero

    private MemoryInfo ReadMemoryNative(ConcurrentBag<string>? notes)
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                var (managed, detail) = ReadPageFileConfig();
                ulong? compressed = TryReadCompressedBytes();
                return new MemoryInfo
                {
                    TotalBytes = status.ullTotalPhys,
                    AvailableBytes = status.ullAvailPhys,
                    MemoryLoadPercent = status.dwMemoryLoad,
                    CommitLimitBytes = status.ullTotalPageFile,
                    CommitAvailableBytes = status.ullAvailPageFile,
                    PageFileSystemManaged = managed,
                    PageFileConfigDetail = detail,
                    CompressedBytes = compressed,
                    Source = "API:GlobalMemoryStatusEx"
                };
            }
        }
        catch (Exception ex) { notes?.Add($"RAM API: {ex.Message}"); }

        // Fallback WMI OS
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                return new MemoryInfo
                {
                    TotalBytes = Convert.ToUInt64(obj["TotalVisibleMemorySize"]) * 1024UL,
                    AvailableBytes = Convert.ToUInt64(obj["FreePhysicalMemory"]) * 1024UL,
                    Source = "WMI:Win32_OperatingSystem"
                };
            }
        }
        catch (Exception ex) { notes?.Add($"RAM WMI: {ex.Message}"); }

        notes?.Add("RAM: No detectado por el sistema");
        return new MemoryInfo { Source = "none" };
    }

    private static (bool? Managed, string? Detail) ReadPageFileConfig()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var values = key?.GetValue("PagingFiles") as string[];
            if (values is null || values.Length == 0)
                return (null, null);

            // Formato típico: "C:\pagefile.sys 0 0" = system managed
            var joined = string.Join(" | ", values);
            var systemManaged = values.Any(v =>
            {
                var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 && parts[1] == "0" && parts[2] == "0";
            });
            return (systemManaged, joined);
        }
        catch
        {
            return (null, null);
        }
    }

    private static ulong? TryReadCompressedBytes()
    {
        try
        {
            // Contador opcional (Win10+); si no existe → null
            using var pc = new PerformanceCounter("Memory", "Compressed Bytes", true);
            var v = pc.NextValue();
            if (v < 0) return null;
            return (ulong)v;
        }
        catch
        {
            return null;
        }
    }

    private MemoryMeta ReadMemoryMeta(ConcurrentBag<string> notes)
    {
        double? speed = null;
        string? type = null;
        var modules = new List<MemoryModuleInfo>();
        var source = "";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT BankLabel, DeviceLocator, Capacity, Speed, SMBIOSMemoryType, Manufacturer, PartNumber, SerialNumber FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in searcher.Get())
            {
                ulong capacity = 0;
                try { capacity = Convert.ToUInt64(obj["Capacity"] ?? 0); } catch { /* */ }
                double? modSpeed = null;
                try { if (obj["Speed"] is not null) modSpeed = Convert.ToDouble(obj["Speed"]); } catch { /* */ }
                string? modType = null;
                try
                {
                    if (obj["SMBIOSMemoryType"] is not null)
                        modType = MapMemoryType(Convert.ToInt32(obj["SMBIOSMemoryType"]));
                }
                catch { /* */ }

                speed ??= modSpeed;
                type ??= modType is null || modType == NotDetected.Text ? null : modType;

                modules.Add(new MemoryModuleInfo
                {
                    BankLabel = obj["BankLabel"]?.ToString()?.Trim() ?? "",
                    DeviceLocator = obj["DeviceLocator"]?.ToString()?.Trim() ?? "",
                    CapacityBytes = capacity,
                    SpeedMhz = modSpeed,
                    MemoryType = modType,
                    Manufacturer = NullIfBlank(obj["Manufacturer"]?.ToString()),
                    PartNumber = NullIfBlank(obj["PartNumber"]?.ToString()),
                    SerialNumber = NullIfBlank(obj["SerialNumber"]?.ToString())
                });
            }
            if (modules.Count > 0) source = "WMI:Win32_PhysicalMemory";
        }
        catch (Exception ex) { notes.Add($"RAM meta: {ex.Message}"); }

        if (modules.Count == 0) type = NotDetected.Text;
        return new MemoryMeta(speed, type, modules.Count == 0 ? null : modules.Count, source, modules);
    }

    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string MapMemoryType(int t) => t switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _ => NotDetected.Text
    };

    private IReadOnlyList<DiskInfo> ReadDisksFast(ConcurrentBag<string> notes)
    {
        var list = new List<DiskInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady &&
                     (d.DriveType is DriveType.Fixed or DriveType.Removable)))
            {
                try
                {
                    list.Add(new DiskInfo
                    {
                        Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : drive.VolumeLabel,
                        DriveLetter = drive.Name.TrimEnd('\\'),
                        DriveType = drive.DriveType.ToString(),
                        MediaType = "Pendiente (análisis profundo)",
                        TotalBytes = (ulong)drive.TotalSize,
                        FreeBytes = (ulong)drive.TotalFreeSpace,
                        SmartAvailability = FeatureAvailability.Limited,
                        HealthStatus = NotDetected.Text,
                        Source = "API:DriveInfo"
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex) { notes.Add($"Disks: {ex.Message}"); }

        if (list.Count == 0) notes.Add("Discos: No detectado por el sistema");
        return list;
    }

    private IReadOnlyList<DiskInfo> EnrichDisks(IReadOnlyList<DiskInfo> volumes, ConcurrentBag<string> notes)
    {
        var physical = new List<(string Model, string Media, string Bus, ulong Size)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");
            foreach (ManagementObject obj in searcher.Get())
            {
                var media = MapStorageMedia(Convert.ToInt32(obj["MediaType"] ?? 0));
                var bus = MapBusType(Convert.ToInt32(obj["BusType"] ?? 0));
                var model = obj["FriendlyName"]?.ToString() ?? NotDetected.Text;
                ulong size = 0;
                try { size = Convert.ToUInt64(obj["Size"] ?? 0); } catch { /* ignore */ }
                physical.Add((model, media, bus, size));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"MSFT_PhysicalDisk: {ex.Message}");
            // Fallback Win32_DiskDrive once
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var model = obj["Model"]?.ToString() ?? NotDetected.Text;
                    var iface = obj["InterfaceType"]?.ToString() ?? "";
                    var media = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe"
                        : model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                        : GuessFromIface(iface);
                    ulong size = 0;
                    try { size = Convert.ToUInt64(obj["Size"] ?? 0); }
                    catch (Exception sizeEx) { _logger.LogDebug(sizeEx, "Win32_DiskDrive Size parse"); }
                    physical.Add((model, media, iface, size));
                }
            }
            catch (Exception ex2) { notes.Add($"Win32_DiskDrive: {ex2.Message}"); }
        }

        if (physical.Count == 0)
            return volumes.Select(v => v with { MediaType = NotDetected.Text }).ToList();

        // Asigna modelo/media al volumen más cercano por tamaño
        return volumes.Select(v =>
        {
            var match = physical
                .OrderBy(p => Math.Abs((long)p.Size - (long)v.TotalBytes))
                .FirstOrDefault();
            if (match.Model is null)
                return v with { MediaType = NotDetected.Text };
            return v with
            {
                MediaType = match.Media,
                Model = match.Model,
                BusType = match.Bus,
                Source = v.Source + "+Storage"
            };
        }).ToList();
    }

    private static string MapStorageMedia(int mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => NotDetected.Text
    };

    private static string MapBusType(int bus) => bus switch
    {
        7 => "USB",
        11 => "SATA",
        17 => "NVMe",
        8 => "RAID",
        _ => NotDetected.Text
    };

    private static string GuessFromIface(string iface)
    {
        if (iface.Contains("SCSI", StringComparison.OrdinalIgnoreCase) ||
            iface.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return "NVMe/SSD";
        if (iface.Contains("IDE", StringComparison.OrdinalIgnoreCase))
            return "HDD";
        return NotDetected.Text;
    }

    private (NetworkInfo Primary, IReadOnlyList<NetworkAdapterInfo> Adapters) ReadNetworkAll(ConcurrentBag<string> notes)
    {
        var adapters = new List<NetworkAdapterInfo>();
        NetworkInfo primary = new() { IsConnected = false, Source = "NetworkInterface", PrimaryAdapter = NotDetected.Text };
        try
        {
            var wlanMbps = ReadWlanLinkMbps();
            var wmiBits = ReadWmiAdapterSpeeds();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                string? ipv4 = null;
                string? gateway = null;
                try
                {
                    var props = nic.GetIPProperties();
                    ipv4 = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.Address.ToString();
                    gateway = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.Address.ToString();
                }
                catch { /* */ }

                var mac = FormatMac(nic.GetPhysicalAddress());
                var macKey = "mac:" + (mac ?? "").Replace(":", "");
                string? speed = null;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    && wlanMbps.TryGetValue(nic.Name, out var wifiMbps))
                    speed = FormatMbps(wifiMbps);
                else if (!string.IsNullOrEmpty(mac) && wmiBits.TryGetValue(macKey, out var wmiSp))
                    speed = FormatLinkBits(wmiSp);
                else if (wmiBits.TryGetValue(nic.Name, out var wmiByName))
                    speed = FormatLinkBits(wmiByName);
                else
                    speed = FormatLinkBits(nic.Speed);

                adapters.Add(new NetworkAdapterInfo
                {
                    Name = nic.Name,
                    Type = nic.NetworkInterfaceType.ToString(),
                    Status = nic.OperationalStatus.ToString(),
                    Speed = speed,
                    Mac = mac,
                    IPv4 = ipv4,
                    Gateway = gateway,
                    IsVirtual = IsVirtualAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType),
                    Source = "API:NetworkInterface"
                });
            }

            var up = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                     !IsVirtualAdapter(n.Name, n.Description, n.NetworkInterfaceType) &&
                                     n.GetIPProperties().UnicastAddresses.Any(a =>
                                         a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
            up ??= NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                     n.GetIPProperties().UnicastAddresses.Any(a =>
                                         a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
            if (up is not null)
            {
                var ip = up.GetIPProperties().UnicastAddresses
                    .First(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Address.ToString();
                primary = new NetworkInfo
                {
                    PrimaryAdapter = up.Name,
                    IPv4 = ip,
                    IsConnected = true,
                    Source = "API:NetworkInterface"
                };
            }
        }
        catch (Exception ex) { notes.Add($"Red: {ex.Message}"); }

        if (adapters.Count == 0) notes.Add("Red: No detectado por el sistema");
        return (primary, adapters);
    }

    /// <summary>Velocidad de enlace Wi‑Fi real (netsh), no el máximo teórico de NetworkInterface.Speed.</summary>
    private static Dictionary<string, double> ReadWlanLinkMbps()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p is null) return map;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2500))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                return map;
            }

            string? name = null;
            double? rx = null;
            void Flush()
            {
                if (!string.IsNullOrWhiteSpace(name) && rx is > 0.4)
                    map[name!] = rx.Value;
                name = null;
                rx = null;
            }

            foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Nombre", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    name = val;
                }
                else if (key.Contains("Receive rate", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("Velocidad de recepción", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("Recepcion", StringComparison.OrdinalIgnoreCase))
                {
                    var num = val.Split(' ')[0].Replace(',', '.');
                    if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0)
                        rx = m;
                }
            }
            Flush();
        }
        catch { /* netsh no disponible */ }
        return map;
    }

    private static Dictionary<string, long> ReadWmiAdapterSpeeds()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NetConnectionID, MACAddress, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True");
            foreach (ManagementObject obj in searcher.Get())
            {
                long speed = 0;
                try { if (obj["Speed"] is not null) speed = Convert.ToInt64(obj["Speed"]); } catch { /* */ }
                if (speed <= 0) continue;
                var conn = obj["NetConnectionID"]?.ToString()?.Trim();
                var name = obj["Name"]?.ToString()?.Trim();
                var mac = (obj["MACAddress"]?.ToString() ?? "").Replace(":", "").Replace("-", "");
                if (!string.IsNullOrWhiteSpace(conn)) map[conn] = speed;
                if (!string.IsNullOrWhiteSpace(name)) map[name] = speed;
                if (mac.Length >= 12) map["mac:" + mac] = speed;
            }
        }
        catch { /* */ }
        return map;
    }

    private static string? FormatLinkBits(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0 || bitsPerSecond >= long.MaxValue / 8) return null;
        return FormatMbps(bitsPerSecond / 1_000_000.0);
    }

    private static string? FormatMbps(double mbps)
    {
        if (mbps is < 0.5 or > 200_000) return null;
        if (mbps >= 1000)
            return $"{mbps / 1000.0:0.#} Gbps";
        return mbps >= 100 ? $"{Math.Round(mbps)} Mbps" : $"{mbps:0.#} Mbps";
    }

    private static bool IsVirtualAdapter(string name, string? description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback) return true;
        var hay = $"{name} {description}";
        return hay.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("VMware", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("WSL", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("VPN", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("Pseudo", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormatMac(PhysicalAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        if (bytes.Length == 0) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private (MotherboardInfo Mb, BiosInfo Bios) ReadBoardAndBios(ConcurrentBag<string> notes)
    {
        var mb = new MotherboardInfo();
        var bios = new BiosInfo();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, SerialNumber, Version FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
            {
                mb = new MotherboardInfo
                {
                    Manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? NotDetected.Text,
                    Product = obj["Product"]?.ToString()?.Trim() ?? NotDetected.Text,
                    SerialNumber = obj["SerialNumber"]?.ToString(),
                    Version = obj["Version"]?.ToString(),
                    Source = "WMI:Win32_BaseBoard"
                };
                break;
            }
        }
        catch (Exception ex) { notes.Add($"Motherboard: {ex.Message}"); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, Version FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? release = null;
                try
                {
                    if (obj["ReleaseDate"] is not null)
                        release = ManagementDateTimeConverter.ToDateTime(obj["ReleaseDate"].ToString()!).ToString("yyyy-MM-dd");
                }
                catch { /* ignore */ }

                bios = new BiosInfo
                {
                    Vendor = obj["Manufacturer"]?.ToString()?.Trim() ?? NotDetected.Text,
                    Version = obj["SMBIOSBIOSVersion"]?.ToString() ?? obj["Version"]?.ToString() ?? NotDetected.Text,
                    ReleaseDate = release ?? NotDetected.Text,
                    SmbiosVersion = obj["Version"]?.ToString(),
                    Source = "WMI:Win32_BIOS"
                };
                break;
            }
        }
        catch (Exception ex) { notes.Add($"BIOS: {ex.Message}"); }

        if (mb.Product == NotDetected.Text) notes.Add("Placa base: No detectado por el sistema");
        if (bios.Version == NotDetected.Text) notes.Add("BIOS: No detectado por el sistema");
        return (mb, bios);
    }

    private IReadOnlyList<MonitorInfo> ReadMonitors(ConcurrentBag<string> notes)
    {
        var list = new List<MonitorInfo>();
        try
        {
            var primaryW = GetSystemMetrics(0);
            var primaryH = GetSystemMetrics(1);
            using var searcher = new ManagementObjectSearcher("SELECT Name, ScreenWidth, ScreenHeight FROM Win32_DesktopMonitor");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? "";
                if (IsPlaceholderMonitorName(name))
                    continue;
                int? w = null, h = null;
                try { if (obj["ScreenWidth"] is not null) w = Convert.ToInt32(obj["ScreenWidth"]); }
                catch (Exception ex) { _logger.LogDebug(ex, "Monitor ScreenWidth"); }
                try { if (obj["ScreenHeight"] is not null) h = Convert.ToInt32(obj["ScreenHeight"]); }
                catch (Exception ex) { _logger.LogDebug(ex, "Monitor ScreenHeight"); }

                // Sin resolución = fila fantasma (sale "×" sin datos). No listar.
                if (w is not > 0 || h is not > 0)
                    continue;

                var isPrimary = w == primaryW && h == primaryH;
                list.Add(new MonitorInfo
                {
                    Name = name,
                    ScreenWidth = w is > 0 ? w : null,
                    ScreenHeight = h is > 0 ? h : null,
                    RefreshHz = isPrimary ? TryPrimaryRefreshHz() : null,
                    IsPrimary = isPrimary,
                    Source = "WMI:Win32_DesktopMonitor"
                });
            }
        }
        catch (Exception ex) { notes.Add($"Monitores WMI: {ex.Message}"); }

        if (list.Count == 0)
        {
            try
            {
                var w = GetSystemMetrics(0);
                var h = GetSystemMetrics(1);
                if (w > 0 && h > 0)
                {
                    list.Add(new MonitorInfo
                    {
                        Name = "__PRIMARY__",
                        ScreenWidth = w,
                        ScreenHeight = h,
                        RefreshHz = TryPrimaryRefreshHz(),
                        IsPrimary = true,
                        Source = "API:GetSystemMetrics"
                    });
                }
            }
            catch (Exception ex) { notes.Add($"Monitores API: {ex.Message}"); }
        }
        else
        {
            // Exactamente un principal: el que coincide con resolución primaria, o el primero con datos.
            var primaryIdx = list.FindIndex(m => m.IsPrimary);
            if (primaryIdx < 0) primaryIdx = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var m = list[i];
                list[i] = new MonitorInfo
                {
                    Name = m.Name,
                    Manufacturer = m.Manufacturer,
                    ScreenWidth = m.ScreenWidth,
                    ScreenHeight = m.ScreenHeight,
                    RefreshHz = i == primaryIdx ? (m.RefreshHz ?? TryPrimaryRefreshHz()) : m.RefreshHz,
                    IsPrimary = i == primaryIdx,
                    Source = m.Source
                };
            }
        }

        if (list.Count == 0) notes.Add(Loc.T("Common.NotDetected"));
        return list;
    }

    private static bool IsPlaceholderMonitorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        if (n.Equals("__PRIMARY__", StringComparison.OrdinalIgnoreCase)) return true;
        return n.Contains("predeterminado", StringComparison.OrdinalIgnoreCase)
               || n.Contains("default monitor", StringComparison.OrdinalIgnoreCase)
               || n.Contains("generic pnp", StringComparison.OrdinalIgnoreCase)
               || n.Contains("pnp gen", StringComparison.OrdinalIgnoreCase)
               || n.Contains("non-pnp", StringComparison.OrdinalIgnoreCase)
               || n.Contains("no pnp", StringComparison.OrdinalIgnoreCase);
    }

    private static double? TryPrimaryRefreshHz()
    {
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var hz = GetDeviceCaps(hdc, 116); // VREFRESH
                return hz is > 1 and < 1000 ? hz : null;
            }
            finally { _ = ReleaseDC(IntPtr.Zero, hdc); }
        }
        catch { return null; }
    }

    public Task<SecurityInfo> CaptureSecurityAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            try { return ReadSecurityLight(new ConcurrentBag<string>()); }
            catch (Exception ex)
            {
                return new SecurityInfo { Source = "error:" + ex.Message, ReadAt = DateTimeOffset.Now };
            }
        }, ct);

    private SecurityInfo ReadSecurityLight(ConcurrentBag<string> notes)
    {
        bool? defender = null, firewall = null, secureBoot = null, tpm = null;
        bool? fwDomain = null, fwPrivate = null, fwPublic = null;
        bool? bitLocker = null, smartScreen = null, hvci = null, uac = null, credGuard = null, vbs = null;
        string? tpmVer = null, tpmMfr = null, bitDetail = null, uacLevel = null;
        var sources = new List<string>();

        try
        {
            using var sc = new ServiceController("WinDefend");
            defender = sc.Status == ServiceControllerStatus.Running;
            sources.Add("Service:WinDefend");
        }
        catch (Exception ex) { notes.Add($"Defender: {ex.Message}"); }

        try
        {
            using var sc = new ServiceController("mpssvc");
            firewall = sc.Status == ServiceControllerStatus.Running;
            sources.Add("Service:mpssvc");
        }
        catch (Exception ex) { notes.Add($"Firewall: {ex.Message}"); }

        try
        {
            fwDomain = ReadFirewallProfileEnabled(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile");
            fwPrivate = ReadFirewallProfileEnabled(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile");
            fwPublic = ReadFirewallProfileEnabled(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile");
            if (fwDomain is not null || fwPrivate is not null || fwPublic is not null)
                sources.Add("Registry:FirewallPolicy");
        }
        catch { /* ignore */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var v = key?.GetValue("UEFISecureBootEnabled");
            if (v is int i) { secureBoot = i == 1; sources.Add("Registry:SecureBoot"); }
        }
        catch { /* ignore */ }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\Security\MicrosoftTpm",
                "SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    tpm = obj["IsEnabled_InitialValue"] is not null && Convert.ToBoolean(obj["IsEnabled_InitialValue"]);
                    tpmVer = obj["SpecVersion"]?.ToString();
                    sources.Add("WMI:Win32_Tpm");
                }
                break;
            }
        }
        catch (Exception ex) { notes.Add($"TPM: {ex.Message}"); }

        // BitLocker WMI (EncryptableVolume) es inestable / admin en muchos equipos: no tumbar el proceso.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\Security\MicrosoftVolumeEncryption",
                "SELECT ProtectionStatus FROM Win32_EncryptableVolume");
            searcher.Options.Timeout = TimeSpan.FromMilliseconds(800);
            searcher.Options.ReturnImmediately = true;
            var protectedCount = 0;
            var total = 0;
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    total++;
                    var status = Convert.ToInt32(obj["ProtectionStatus"] ?? 0);
                    if (status == 1) protectedCount++;
                }
                if (total >= 8) break;
            }
            if (total > 0)
            {
                bitLocker = protectedCount > 0;
                bitDetail = $"{protectedCount}/{total}";
                sources.Add("WMI:EncryptableVolume");
            }
        }
        catch
        {
            bitLocker = null;
            bitDetail = null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer");
            var v = key?.GetValue("SmartScreenEnabled")?.ToString();
            if (!string.IsNullOrWhiteSpace(v))
            {
                smartScreen = !v.Equals("Off", StringComparison.OrdinalIgnoreCase);
                sources.Add("Registry:SmartScreen");
            }
        }
        catch { /* ignore */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            var v = key?.GetValue("Enabled");
            if (v is int i) { hvci = i == 1; sources.Add("Registry:HVCI"); }
        }
        catch { /* ignore */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var enableLua = key?.GetValue("EnableLUA");
            if (enableLua is int lua)
            {
                uac = lua != 0;
                var consent = key?.GetValue("ConsentPromptBehaviorAdmin");
                uacLevel = consent switch
                {
                    0 => "NeverNotify",
                    1 or 3 => "PromptCredentials",
                    2 or 5 => "PromptConsent",
                    _ => "Configured"
                };
                sources.Add("Registry:UAC");
            }
        }
        catch { /* ignore */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard");
            var vbsVal = key?.GetValue("EnableVirtualizationBasedSecurity");
            if (vbsVal is int v) { vbs = v == 1; sources.Add("Registry:VBS"); }
            var cg = key?.GetValue("LsaCfgFlags");
            if (cg is int c) { credGuard = c != 0; sources.Add("Registry:CredentialGuard"); }
        }
        catch { /* ignore */ }

        return new SecurityInfo
        {
            DefenderEnabled = defender,
            FirewallEnabled = firewall,
            FirewallDomainOn = fwDomain,
            FirewallPrivateOn = fwPrivate,
            FirewallPublicOn = fwPublic,
            SecureBootEnabled = secureBoot,
            BitLockerActive = bitLocker,
            BitLockerDetail = bitDetail,
            TpmPresent = tpm,
            TpmVersion = tpmVer,
            TpmManufacturer = tpmMfr,
            SmartScreenEnabled = smartScreen,
            MemoryIntegrityEnabled = hvci,
            UacEnabled = uac,
            UacLevel = uacLevel,
            CredentialGuardEnabled = credGuard,
            VirtualizationBasedSecurity = vbs,
            Source = string.Join("+", sources),
            ReadAt = DateTimeOffset.Now
        };
    }

    private static bool? ReadFirewallProfileEnabled(string subKey)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey);
        var v = key?.GetValue("EnableFirewall");
        return v is int i ? i == 1 : null;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private sealed record InventoryCache(
        OsInfo Os,
        CpuInfo Cpu,
        IReadOnlyList<GpuInfo> Gpus,
        MemoryMeta MemoryMeta,
        IReadOnlyList<DiskInfo> Disks,
        MotherboardInfo Motherboard,
        BiosInfo Bios,
        IReadOnlyList<MonitorInfo> Monitors,
        NetworkInfo Network,
        IReadOnlyList<NetworkAdapterInfo> Adapters,
        bool? IsPortable);

    private sealed record MemoryMeta(
        double? SpeedMhz,
        string? MemoryType,
        int? SlotCount,
        string Source,
        IReadOnlyList<MemoryModuleInfo> Modules);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
}
