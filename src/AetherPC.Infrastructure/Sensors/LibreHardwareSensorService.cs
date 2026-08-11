using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace AetherPC.Infrastructure.Sensors;

public sealed class LibreHardwareSensorService : ISensorService, IDisposable
{
    private readonly ILogger<LibreHardwareSensorService> _logger;
    private readonly object _gate = new();
    private Computer? _computer;
    private bool _initFailed;
    private bool _ready;
    private Task? _warmupTask;

    public LibreHardwareSensorService(ILogger<LibreHardwareSensorService> logger)
    {
        _logger = logger;
    }

    public bool IsReady
    {
        get { lock (_gate) return _ready && !_initFailed; }
    }

    public Task WarmupAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_ready || _initFailed) return Task.CompletedTask;
            _warmupTask ??= Task.Run(() => OpenComputer(), ct);
            return _warmupTask;
        }
    }

    public async Task<ThermalInfo> ReadThermalsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_initFailed)
        {
            return new ThermalInfo
            {
                Availability = FeatureAvailability.Unavailable,
                Note = Loc.T("Common.NotDetected"),
                Source = "LibreHardwareMonitor:failed"
            };
        }

        if (!IsReady)
        {
            // No bloquea: dispara warmup y responde inmediatamente
            _ = WarmupAsync(ct);
            return new ThermalInfo
            {
                Availability = FeatureAvailability.Unavailable,
                Note = Loc.T("Sensors.Warming"),
                Source = "LibreHardwareMonitor:warming"
            };
        }

        return await Task.Run(() => ReadLocked(), ct);
    }

    private ThermalInfo ReadLocked()
    {
        lock (_gate)
        {
            try
            {
                if (_computer is null)
                {
                    return new ThermalInfo
                    {
                        Availability = FeatureAvailability.Unavailable,
                        Note = Loc.T("Common.NotDetected"),
                        Source = "LibreHardwareMonitor"
                    };
                }

                double? cpu = null, gpu = null;
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) sub.Update();
                    if (hw.HardwareType == HardwareType.Cpu)
                        cpu ??= FindTemp(hw);
                    if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                        gpu ??= FindTemp(hw);
                }

                var ok = cpu is not null || gpu is not null;
                return new ThermalInfo
                {
                    CpuCelsius = cpu,
                    GpuCelsius = gpu,
                    Availability = ok ? FeatureAvailability.Available : FeatureAvailability.Unavailable,
                    Note = ok ? null : "No detectado por el sistema",
                    Source = "LibreHardwareMonitor"
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lectura sensores");
                return new ThermalInfo
                {
                    Availability = FeatureAvailability.Unavailable,
                    Note = Loc.T("Common.NotDetected"),
                    Source = "LibreHardwareMonitor:error"
                };
            }
        }
    }

    private void OpenComputer()
    {
        lock (_gate)
        {
            if (_ready || _initFailed) return;
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsMemoryEnabled = false,
                    IsStorageEnabled = true,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false
                };
                _computer.Open();
                _ready = true;
                _logger.LogInformation("LibreHardwareMonitor listo");
                // Single-file: LHM puede crear AetherPC.sys junto al EXE → reubicar/limpiar
                TryCleanupKernelDriverSidecar();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo abrir LibreHardwareMonitor");
                _initFailed = true;
                _computer = null;
                TryCleanupKernelDriverSidecar();
            }
        }
    }

    /// <summary>
    /// LibreHardwareMonitor extrae el driver kernel como "{nombreExe}.sys" junto al proceso.
    /// En portable single-file eso ensucia la carpeta del EXE; lo movemos a LocalAppData.
    /// </summary>
    private static void TryCleanupKernelDriverSidecar()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var sidecar = Path.ChangeExtension(exe, ".sys");
            if (!File.Exists(sidecar)) return;

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AetherPC", "drivers");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, Path.GetFileName(sidecar));

            try
            {
                File.Copy(sidecar, dest, overwrite: true);
            }
            catch { /* destino bloqueado o igual */ }

            try
            {
                File.Delete(sidecar);
            }
            catch
            {
                // Sigue en uso por el driver cargado; se reintenta en Dispose/Close.
            }
        }
        catch
        {
            // No bloquear sensores por limpieza
        }
    }

    private static double? FindTemp(IHardware hardware)
    {
        double? best = null;
        void Walk(IHardware hw)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value is null) continue;
                var name = s.Name ?? "";
                if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CCD", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Hot", StringComparison.OrdinalIgnoreCase))
                {
                    best = s.Value;
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                best ??= s.Value;
            }
            foreach (var sub in hw.SubHardware) Walk(sub);
        }
        Walk(hardware);
        return best;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { /* ignore */ }
            _computer = null;
            _ready = false;
        }
        TryCleanupKernelDriverSidecar();
    }
}
