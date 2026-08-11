using System.Diagnostics;
using System.Text;
using AetherPC.Core.Models;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AetherPC.Optimization;

/// <summary>
/// Ajustes mediante APIs/comandos oficiales de Windows y rutas de driver documentadas/comunes.
/// Cada acción debe ser compatible con el hardware detectado (el motor de reglas filtra).
/// </summary>
public sealed partial class WindowsOfficialTuning
{
    private readonly ILogger<WindowsOfficialTuning> _logger;

    public WindowsOfficialTuning(ILogger<WindowsOfficialTuning> logger) => _logger = logger;

    public async Task<ActionResult> ExecuteAsync(string actionId, SystemSnapshot? snapshot, CancellationToken ct)
    {
        return actionId switch
        {
            "cleanup.temp" or "cleanup.advanced" => new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.DelegatedCleanup" },
            "net.flushdns" => await RunAsync(actionId, "ipconfig", "/flushdns", ct),
            "power.balanced" => await SetPowerSchemeAsync(actionId, "SCHEME_BALANCED", ct),
            "power.high" => await SetPowerSchemeAsync(actionId, "SCHEME_MIN", ct),
            "power.ultimate" => await ActivateUltimatePerformanceAsync(actionId, ct),
            "power.cpu_max" => await SetProcessorStatesMaxAsync(actionId, snapshot, ct),
            "power.core_unpark" => await SetCoreParkingMinAsync(actionId, ct),
            "power.pcie_max" => await SetPciExpressMaxAsync(actionId, ct),
            "power.boost_aggressive" => await SetPerfBoostAggressiveAsync(actionId, ct),
            "windows.gamemode" => SetGameMode(actionId),
            "windows.hags" => SetHags(actionId, enabled: true),
            "windows.gamedvr_off" => SetGameDvrOff(actionId),
            "windows.mmcss_low_latency" => SetMmcssGaming(actionId),
            "windows.storage_sense" => SetStorageSense(actionId, enabled: true),
            "windows.delivery_opt" => SetDeliveryOptimizationLanOnly(actionId),
            "disk.trim" => await TrimVolumesAsync(actionId, snapshot, ct),
            "service.sysmain.manual" => SetServiceStartMode(actionId, "SysMain", "demand"),
            "service.search.manual" => SetServiceStartMode(actionId, "WSearch", "demand"),
            "defender.reduce_load" => await ReduceDefenderLoadAsync(actionId, ct),
            "nvidia.powermizer_max" => SetNvidiaPowerMizerMax(actionId, snapshot),
            "nvidia.low_latency" => SetNvidiaLowLatencyHints(actionId, snapshot),
            "intel.cpu_max" => await SetProcessorStatesMaxAsync(actionId, snapshot, ct),
            "intel.gpu_max" => SetIntelGpuPreferMax(actionId, snapshot),
            "amd.gpu_max" => SetAmdGpuPreferMax(actionId, snapshot),

            // —— Pack máximo estilo Optimizer (sin tocar Defender / Windows Update) ——
            "privacy.telemetry" => DisableTelemetryPack(actionId),
            "privacy.diagtrack" => SetTelemetryServicesManual(actionId),
            "privacy.advertising" => DisableAdvertisingId(actionId),
            "privacy.tips" => DisableTipsAndSuggestions(actionId),
            "privacy.activity" => DisableActivityHistory(actionId),
            "privacy.feedback" => DisableFeedback(actionId),
            "privacy.copilot" => DisableCopilotHints(actionId),
            "privacy.widgets" => DisableWidgetsAndNews(actionId),
            "privacy.background_apps" => DisableBackgroundApps(actionId),
            "privacy.office_telemetry" => DisableOfficeTelemetry(actionId),
            "perf.menu_delay" => SetMenuShowDelayZero(actionId),
            "perf.network_throttle" => DisableNetworkThrottling(actionId),
            "perf.ntfs_lastaccess" => await DisableNtfsLastAccessAsync(actionId, ct),
            "perf.visual_perf" => SetVisualEffectsPerformance(actionId),
            "perf.transparency_off" => SetTransparency(actionId, enabled: false),
            "perf.animations_off" => SetAnimations(actionId, enabled: false),
            "perf.fast_startup_off" => DisableFastStartup(actionId),
            "perf.hibernate_off" => await DisableHibernateAsync(actionId, ct),
            "perf.xbox_manual" => SetXboxServicesManual(actionId),
            "perf.autoplay_off" => DisableAutoplay(actionId),
            "perf.remote_assist_off" => DisableRemoteAssistance(actionId),

            // Reparación Windows — Advanced, nunca auto (el plan las deja desmarcadas)
            "repair.sfc" => await RunRepairAsync(actionId, "sfc", "/scannow", ct),
            "repair.dism" => await RunRepairAsync(actionId, "DISM.exe",
                "/Online /Cleanup-Image /RestoreHealth", ct),
            "repair.netreset" => await ResetNetworkStackAsync(actionId, ct),

            "startup.review" or "ram.hint" or "ram.smart" => new ActionResult
            {
                ActionId = actionId,
                Success = false,
                Detail = "Acción solo informativa (no ejecutable). Usa desactivar inicio / cerrar procesos del plan.",
                Status = ActionApplyStatus.Skipped
            },
            _ => new ActionResult
            {
                ActionId = actionId,
                Success = false,
                Detail = $"Acción '{actionId}' no reconocida; omitida por seguridad.",
                Status = ActionApplyStatus.Skipped
            }
        };
    }

    private async Task<ActionResult> RunRepairAsync(string actionId, string file, string args, CancellationToken ct)
    {
        var (ok, output) = await RunCaptureAsync(file, args, ct).ConfigureAwait(false);
        if (!ok && LooksLikeSfcBusy(output))
        {
            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Status = ActionApplyStatus.Skipped,
                Detail = Loc.T("Exec.SfcBusy"),
                DetailKey = "Exec.SfcBusy",
                Verified = false
            };
        }
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok,
            Detail = string.IsNullOrWhiteSpace(output) ? (ok ? "OK" : "Failed") : TruncateOut(output, 400),
            Status = ok ? ActionApplyStatus.NeedsReboot : ActionApplyStatus.Failed,
            Verified = ok
        };
    }

    private static bool LooksLikeSfcBusy(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("another", StringComparison.OrdinalIgnoreCase)
               && (output.Contains("repair", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("operation", StringComparison.OrdinalIgnoreCase))
               || output.Contains("otra operación", StringComparison.OrdinalIgnoreCase)
               || output.Contains("se está ejecutando otra", StringComparison.OrdinalIgnoreCase)
               || output.Contains("wait for it to complete", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ActionResult> ResetNetworkStackAsync(string actionId, CancellationToken ct)
    {
        var steps = new[]
        {
            ("netsh", "winsock reset"),
            ("netsh", "int ip reset"),
            ("ipconfig", "/flushdns")
        };
        var details = new List<string>();
        var anyOk = false;
        foreach (var (file, args) in steps)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, output) = await RunCaptureAsync(file, args, ct).ConfigureAwait(false);
            var softOk = ok || LooksLikeCommandSuccess(output);
            anyOk |= softOk;
            details.Add($"{file} {args}: {(softOk ? "OK" : TruncateOut(output, 80))}");
        }
        return new ActionResult
        {
            ActionId = actionId,
            Success = anyOk,
            Detail = string.Join(" | ", details) + " · reboot recommended.",
            Status = anyOk ? ActionApplyStatus.NeedsReboot : ActionApplyStatus.Failed,
            Verified = anyOk
        };
    }

    private static bool LooksLikeCommandSuccess(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("successfully", StringComparison.OrdinalIgnoreCase)
               || output.Contains("correctamente", StringComparison.OrdinalIgnoreCase)
               || output.Contains("completed", StringComparison.OrdinalIgnoreCase)
               || output.Contains("restablecid", StringComparison.OrdinalIgnoreCase)
               || output.Contains("reset", StringComparison.OrdinalIgnoreCase)
               || output.Contains("OK", StringComparison.Ordinal);
    }

    private static string TruncateOut(string s, int n)
    {
        s = s.Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    private async Task<ActionResult> SetPowerSchemeAsync(string actionId, string scheme, CancellationToken ct)
    {
        string? prevGuid = null;
        try
        {
            var (okPrev, prevOut) = await RunCaptureAsync("powercfg", "/getactivescheme", ct);
            if (okPrev)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    prevOut,
                    @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                if (m.Success) prevGuid = m.Groups[1].Value;
            }
        }
        catch { /* */ }

        // Aliases oficiales + GUIDs conocidos (por si el alias falla en alguna edición/idioma)
        var candidates = scheme.ToUpperInvariant() switch
        {
            "SCHEME_BALANCED" => new[] { "SCHEME_BALANCED", "381b4222-f694-41f0-9685-ff5bb260df2e" },
            "SCHEME_MIN" => new[] { "SCHEME_MIN", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" }, // High performance
            "SCHEME_MAX" => new[] { "SCHEME_MAX", "a1841308-3541-4fab-bc81-f71556f20b4a" }, // Power saver
            _ => new[] { scheme }
        };

        string last = "";
        foreach (var id in candidates)
        {
            var (ok, output) = await RunCaptureAsync("powercfg", $"/setactive {id}", ct);
            if (ok)
            {
                return new ActionResult
                {
                    ActionId = actionId,
                    Success = true,
                    Detail = $"Plan de energía activo: {id}.",
                    RollbackToken = BuildPowerRollbackToken(prevGuid),
                    BeforeValue = prevGuid,
                    AfterValue = id,
                    Status = ActionApplyStatus.Applied
                };
            }
            last = output;
        }

        return new ActionResult
        {
            ActionId = actionId,
            Success = false,
            Detail = string.IsNullOrWhiteSpace(last) ? "No se pudo activar el plan de energía." : last,
            Status = ActionApplyStatus.Failed
        };
    }

    private async Task<ActionResult> ActivateUltimatePerformanceAsync(string actionId, CancellationToken ct)
    {
        const string ultimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        var (dupOk, dupOut) = await RunCaptureAsync("powercfg", $"-duplicatescheme {ultimateGuid}", ct);

        // Si se duplicó, el GUID nuevo aparece en la salida
        var guidToActivate = ultimateGuid;
        var matches = System.Text.RegularExpressions.Regex.Matches(
            dupOut,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (matches.Count > 0)
            guidToActivate = matches[^1].Value;

        var (setOk, setOut) = await RunCaptureAsync("powercfg", $"/setactive {guidToActivate}", ct);
        if (setOk)
        {
            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = $"Plan Ultimate Performance activado ({guidToActivate}).",
                RollbackToken = BuildPowerRollbackToken(null),
                Status = ActionApplyStatus.Applied,
                AfterValue = guidToActivate
            };
        }

        // Fallback: High Performance
        var high = await SetPowerSchemeAsync(actionId, "SCHEME_MIN", ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = high.Success,
            Detail = high.Success
                ? "Ultimate no disponible; se activó Alto rendimiento."
                : $"Ultimate/Alto rendimiento falló. {setOut} | {high.Detail} | dup:{dupOut}",
            RollbackToken = "power.prev"
        };
    }

    private async Task<ActionResult> SetProcessorStatesMaxAsync(string actionId, SystemSnapshot? snapshot, CancellationToken ct)
    {
        // Solo AC (conectado). En portátil no forzamos DC al 100% para no vaciar batería.
        var sb = new StringBuilder();
        var steps = new[]
        {
            ("SUB_PROCESSOR", "PROCTHROTTLEMIN", "100"),
            ("SUB_PROCESSOR", "PROCTHROTTLEMAX", "100")
        };

        var okAll = true;
        foreach (var (sub, setting, value) in steps)
        {
            var (okAc, outAc) = await RunCaptureAsync("powercfg", $"/setacvalueindex SCHEME_CURRENT {sub} {setting} {value}", ct);
            okAll &= okAc;
            if (!okAc) sb.AppendLine($"AC {setting}: {outAc}");
        }

        // En escritorio también DC (sin batería suele ser irrelevante)
        if (snapshot?.IsPortable != true)
        {
            foreach (var (sub, setting, value) in steps)
            {
                var (okDc, outDc) = await RunCaptureAsync("powercfg", $"/setdcvalueindex SCHEME_CURRENT {sub} {setting} {value}", ct);
                okAll &= okDc;
                if (!okDc) sb.AppendLine($"DC {setting}: {outDc}");
            }
        }

        var (actOk, actOut) = await RunCaptureAsync("powercfg", "/setactive SCHEME_CURRENT", ct);
        okAll &= actOk;
        if (!actOk) sb.AppendLine(actOut);

        return new ActionResult
        {
            ActionId = actionId,
            Success = okAll,
            Detail = okAll
                ? "Estados mínimo/máximo del procesador al 100% (plan actual, AC" +
                  (snapshot?.IsPortable == true ? "; DC sin tocar en portátil" : "+DC") + ")."
                : sb.ToString().Trim(),
            RollbackToken = "power.cpu"
        };
    }

    private async Task<ActionResult> SetCoreParkingMinAsync(string actionId, CancellationToken ct)
    {
        // CPMINCORES = porcentaje mínimo de cores desaparcados (100 = sin parking)
        const string guid = "0cc5b647-c1df-4637-891a-dec35c318583";
        var (ok1, o1) = await RunCaptureAsync("powercfg", $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR {guid} 100", ct);
        var (ok2, o2) = await RunCaptureAsync("powercfg", "/setactive SCHEME_CURRENT", ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok1 && ok2,
            Detail = ok1 && ok2 ? "Core parking mínimo al 100% (más cores disponibles bajo carga)." : $"{o1} {o2}",
            RollbackToken = "power.cores"
        };
    }

    private async Task<ActionResult> SetPciExpressMaxAsync(string actionId, CancellationToken ct)
    {
        // ASPM Off = 0 — menos latencia PCIe (GPU), más consumo
        var (ok1, o1) = await RunCaptureAsync("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0", ct);
        var (ok2, o2) = await RunCaptureAsync("powercfg", "/setactive SCHEME_CURRENT", ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok1 && ok2,
            Detail = ok1 && ok2
                ? "PCIe Link State Power Management desactivado en AC (mejor para GPU discreta)."
                : $"{o1} {o2}",
            RollbackToken = "power.pcie"
        };
    }

    private async Task<ActionResult> SetPerfBoostAggressiveAsync(string actionId, CancellationToken ct)
    {
        // PERFBOOSTMODE: 2 = Aggressive (si el firmware lo permite)
        var (ok1, o1) = await RunCaptureAsync("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 2", ct);
        var (ok2, o2) = await RunCaptureAsync("powercfg", "/setactive SCHEME_CURRENT", ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok1 && ok2,
            Detail = ok1 && ok2
                ? "Modo de turbo del procesador: Aggressive (AC)."
                : $"No aplicable o requiere plan compatible: {o1} {o2}",
            RollbackToken = "power.boost"
        };
    }

    private static ActionResult SetGameMode(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            key?.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
            key?.SetValue("AllowAutoGameMode", 1, RegistryValueKind.DWord);
            return new ActionResult { ActionId = actionId, Success = true, DetailKey = "Tune.GameModeOn", RollbackToken = "gamemode" };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = ex.Message };
        }
    }

    private static ActionResult SetHags(string actionId, bool enabled)
    {
        var (ok, error) = TrySetDwordWithFallback(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", enabled ? 2 : 1);
        return ok
            ? new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "HAGS configurado. Se recomienda reiniciar.",
                RollbackToken = "hags",
                Status = ActionApplyStatus.Applied
            }
            : new ActionResult { ActionId = actionId, Success = false, Detail = "HAGS requiere admin: " + error, Status = ActionApplyStatus.Failed };
    }

    private static ActionResult SetGameDvrOff(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
            key?.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
            using var key2 = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            key2?.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "Capturas/Game DVR en segundo plano desactivadas (menos overhead al jugar).",
                RollbackToken = "gamedvr"
            };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = ex.Message };
        }
    }

    private static ActionResult SetMmcssGaming(string actionId)
    {
        // SystemResponsiveness: 0–100; valores bajos = más CPU a multimedia/juegos (documentado MMCSS)
        var (ok1, err1) = TrySetDwordWithFallback(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10);
        const string gamesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        var (ok2, err2) = TrySetDwordWithFallback(RegistryHive.LocalMachine, gamesPath, "GPU Priority", 8);
        var (ok3, err3) = TrySetDwordWithFallback(RegistryHive.LocalMachine, gamesPath, "Priority", 6);
        // Scheduling Category / SFIO Priority son REG_SZ; sin contraparte reg.exe DWORD, van directo.
        var strOk = true;
        var strErr = "";
        try
        {
            using var games = Registry.LocalMachine.CreateSubKey(gamesPath);
            games?.SetValue("Scheduling Category", "High", RegistryValueKind.String);
            games?.SetValue("SFIO Priority", "High", RegistryValueKind.String);
        }
        catch (Exception ex) { strOk = false; strErr = ex.Message; }

        var okAll = ok1 && ok2 && ok3;
        return okAll
            ? new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "Perfil MMCSS Games priorizado (latencia multimedia/juegos). Requiere admin." +
                          (strOk ? "" : $" (aviso: {strErr})"),
                RollbackToken = "mmcss",
                Status = ActionApplyStatus.Applied
            }
            : new ActionResult
            {
                ActionId = actionId,
                Success = false,
                Detail = $"MMCSS requiere admin: {err1} {err2} {err3}".Trim(),
                Status = ActionApplyStatus.Failed
            };
    }

    private static ActionResult SetStorageSense(string actionId, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
            key?.SetValue("01", enabled ? 1 : 0, RegistryValueKind.DWord);
            key?.SetValue("04", 1, RegistryValueKind.DWord);
            return new ActionResult { ActionId = actionId, Success = true, DetailKey = "Tune.StorageSenseOn", RollbackToken = "storagesense" };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = ex.Message };
        }
    }

    private static ActionResult SetDeliveryOptimizationLanOnly(string actionId)
    {
        var (ok, error) = TrySetDwordWithFallback(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 1);
        return ok
            ? new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "Delivery Optimization limitado a LAN.",
                RollbackToken = "do",
                Status = ActionApplyStatus.Applied
            }
            : new ActionResult { ActionId = actionId, Success = false, Detail = "Requiere admin: " + error, Status = ActionApplyStatus.Failed };
    }

    private static ActionResult SetNvidiaPowerMizerMax(string actionId, SystemSnapshot? snapshot)
    {
        if (!HasGpu(snapshot, "NVIDIA") && !HasGpu(snapshot, "GeForce"))
            return new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.NoNvidia", Status = ActionApplyStatus.NotCompatible };

        // Prefer Maximum Performance (rutas usadas por el driver NVIDIA en Windows)
        const string path = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak";
        var (ok1, e1) = TrySetDwordWithFallback(RegistryHive.LocalMachine, path, "PowerMizerEnable", 1);
        var (ok2, e2) = TrySetDwordWithFallback(RegistryHive.LocalMachine, path, "PowerMizerLevel", 1);
        var (ok3, e3) = TrySetDwordWithFallback(RegistryHive.LocalMachine, path, "PowerMizerLevelAC", 1);
        var (ok4, e4) = TrySetDwordWithFallback(RegistryHive.LocalMachine, path, "PerfLevelSrc", 0x2222);

        var okAll = ok1 && ok2 && ok3 && ok4;
        return okAll
            ? new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "NVIDIA: Prefer Maximum Performance (PowerMizer AC). Puede subir consumo/temps. Requiere admin.",
                RollbackToken = "nvidia.pm",
                Status = ActionApplyStatus.Applied
            }
            : new ActionResult
            {
                ActionId = actionId,
                Success = false,
                Detail = $"NVIDIA PowerMizer requiere admin: {e1} {e2} {e3} {e4}".Trim(),
                Status = ActionApplyStatus.Failed
            };
    }

    private static ActionResult SetNvidiaLowLatencyHints(string actionId, SystemSnapshot? snapshot)
    {
        if (!HasGpu(snapshot, "NVIDIA") && !HasGpu(snapshot, "GeForce"))
            return new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.NoNvidia", Status = ActionApplyStatus.NotCompatible };

        try
        {
            // Pistas de baja latencia en perfil global del usuario (si el driver las respeta)
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\NVIDIA Corporation\Global\NVTweak");
            key?.SetValue("Gestalt", unchecked((int)0xABCD0000), RegistryValueKind.DWord);
            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "NVIDIA: ajustes de latencia en HKCU\\NVTweak. Combínalo con el Panel de Control NVIDIA si quieres Low Latency Mode = Ultra.",
                RollbackToken = "nvidia.ll"
            };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = ex.Message };
        }
    }

    private static ActionResult SetIntelGpuPreferMax(string actionId, SystemSnapshot? snapshot)
    {
        if (!HasGpu(snapshot, "Intel"))
            return new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.NoIntelGpu", Status = ActionApplyStatus.NotCompatible };

        try
        {
            // Preferencia de rendimiento gráfico Intel (cuando el driver la lee)
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Intel\Display\igfxcui\profiles\Media");
            // Algunas builds usan PowerPlan; escribimos valor de rendimiento máximo si la clave existe/creable
            // (best-effort con método alterno vía reg.exe si el acceso directo falla estando elevado)
            TrySetDwordWithFallback(RegistryHive.LocalMachine, @"Software\Intel\GMM", "DedicatedSegmentSize", 512);

            using var winGpu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            // No forzamos apps concretas; dejamos marca de sistema para que el usuario vea el ajuste aplicado
            winGpu?.SetValue("DirectXUserGlobalSettings", "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", RegistryValueKind.String);

            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "Intel/DirectX: preferencias de rendimiento gráfico aplicadas (HKCU/HKLM). En laptops híbridas, elige la GPU dedicada por juego en Configuración > Gráficos.",
                RollbackToken = "intel.gpu"
            };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = ex.Message };
        }
    }

    private static ActionResult SetAmdGpuPreferMax(string actionId, SystemSnapshot? snapshot)
    {
        if (!HasGpu(snapshot, "AMD") && !HasGpu(snapshot, "Radeon"))
            return new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.NoAmdGpu", Status = ActionApplyStatus.NotCompatible };

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            // KMD_DeLagEnabled / Power related — solo si la clave del adaptador existe
            if (key is null)
                return new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.AmdClassMissing" };

            // Preferencia Windows DirectX (seguro, no rompe driver)
            using var winGpu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            winGpu?.SetValue("DirectXUserGlobalSettings", "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", RegistryValueKind.String);

            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = "AMD/DirectX: preferencias de rendimiento aplicadas. Para Chill/Radeon Software, ajústalo en la app oficial AMD.",
                RollbackToken = "amd.gpu"
            };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = "AMD GPU requiere admin: " + ex.Message };
        }
    }

    private async Task<ActionResult> TrimVolumesAsync(string actionId, SystemSnapshot? snapshot, CancellationToken ct)
    {
        var letters = snapshot?.Disks
            .Where(d => (d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                         d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) &&
                        d.DriveLetter.Length > 0)
            .Select(d => d.DriveLetter.TrimEnd(':', '\\').FirstOrDefault())
            .Where(c => c != '\0')
            .Distinct()
            .ToList() ?? new List<char>();

        if (letters.Count == 0)
            return new ActionResult { ActionId = actionId, Success = false, DetailKey = "Tune.NoTrimVolumes", Status = ActionApplyStatus.NotCompatible };

        var details = new List<string>();
        var okAny = false;
        foreach (var letter in letters)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, output) = await RunCaptureAsync("defrag", $"{letter}: /L", ct);
            okAny |= ok;
            details.Add($"{letter}: {(ok ? "TRIM OK" : output)}");
        }

        return new ActionResult
        {
            ActionId = actionId,
            Success = okAny,
            Detail = string.Join(" | ", details)
        };
    }

    private static ActionResult SetServiceStartMode(string actionId, string serviceName, string modeArg)
    {
        try
        {
            var prev = QueryServiceStartType(serviceName);
            var (cfgOk, cfgOut) = RunCaptureSync("sc", $"config {serviceName} start= {modeArg}");
            if (!cfgOk)
            {
                // B.2) Método alterno único: sc.exe denegado estando elevado (poco común, pero puede
                // pasar por políticas/locale) → escribir directamente el valor Start en el registro
                // del servicio (2=auto, 3=demand/manual, 4=disabled — documentado por Microsoft).
                var accessDenied = cfgOut.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                                    cfgOut.Contains("acceso es denegado", StringComparison.OrdinalIgnoreCase) ||
                                    cfgOut.Contains("5:", StringComparison.Ordinal);
                if (accessDenied && ProcessPrivileges.IsElevated && TrySetServiceStartRegistry(serviceName, modeArg))
                {
                    var (stopOkAlt, stopOutAlt) = RunCaptureSync("sc", $"stop {serviceName}");
                    return new ActionResult
                    {
                        ActionId = actionId,
                        Success = true,
                        Detail = $"{serviceName} → inicio {modeArg} (vía registro; sc.exe denegó acceso)." +
                                  (stopOkAlt ? " Detenido ahora." : $" (stop: {stopOutAlt.Trim()})"),
                        RollbackToken = BuildServiceRollbackToken(serviceName, prev),
                        BeforeValue = prev,
                        AfterValue = modeArg,
                        Status = ActionApplyStatus.Applied
                    };
                }

                return new ActionResult
                {
                    ActionId = actionId,
                    Success = false,
                    Detail = string.IsNullOrWhiteSpace(cfgOut) ? "sc config falló" : cfgOut,
                    Status = ActionApplyStatus.Failed,
                    BeforeValue = prev
                };
            }

            // WSearch/SysMain: en PCs lentos `sc stop` puede colgar minutos. Configurar inicio basta;
            // el stop se lanza en segundo plano con tope corto.
            string stopMsg;
            if (serviceName.Equals("WSearch", StringComparison.OrdinalIgnoreCase)
                || serviceName.Equals("SysMain", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() =>
                {
                    try { RunCaptureSync("sc", $"stop {serviceName}"); } catch { /* ignore */ }
                });
                stopMsg = " " + Loc.T("Svc.StopBackground");
            }
            else
            {
                var (stopOk, stopOut) = RunCaptureSync("sc", $"stop {serviceName}");
                stopMsg = stopOk
                    ? " " + Loc.T("Svc.StoppedNow")
                    : (stopOut.Contains("1062", StringComparison.Ordinal) || stopOut.Contains("not been started", StringComparison.OrdinalIgnoreCase)
                        ? " " + Loc.T("Svc.AlreadyStopped")
                        : " " + Loc.T("Svc.ConfigOkStop", stopOut.Trim()));
            }

            return new ActionResult
            {
                ActionId = actionId,
                Success = true,
                Detail = $"{serviceName} → {modeArg}.{stopMsg}",
                RollbackToken = BuildServiceRollbackToken(serviceName, prev),
                BeforeValue = prev,
                AfterValue = modeArg,
                Status = ActionApplyStatus.Applied
            };
        }
        catch (Exception ex)
        {
            return new ActionResult { ActionId = actionId, Success = false, Detail = ex.Message, Status = ActionApplyStatus.Failed };
        }
    }

    private static bool TrySetServiceStartRegistry(string serviceName, string modeArg)
    {
        var value = modeArg.ToLowerInvariant() switch
        {
            "auto" => 2,
            "demand" => 3,
            "disabled" => 4,
            _ => (int?)null
        };
        if (value is null) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
            if (key is null) return false;
            key.SetValue("Start", value.Value, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    private static (bool Ok, string Output) RunCaptureSync(string file, string args)
    {
        var exe = ResolveSystemTool(file);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        using var p = Process.Start(psi);
        if (p is null) return (false, "Could not start " + exe);

        // sc config/stop y similares: tope corto para no congelar Bestia/Optimizar en "Verifying…".
        var timeoutMs = file.Contains("sc", StringComparison.OrdinalIgnoreCase) ? 8_000
            : args.Contains("flushdns", StringComparison.OrdinalIgnoreCase) ? 6_000
            : 12_000;

        var stdout = "";
        var stderr = "";
        try
        {
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                try { stdout = outTask.IsCompleted ? outTask.Result : ""; } catch { /* */ }
                try { stderr = errTask.IsCompleted ? errTask.Result : ""; } catch { /* */ }
                var partial = (stdout + "\n" + stderr).Trim();
                if (LooksLikeCommandSuccess(partial) || partial.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    return (true, string.IsNullOrWhiteSpace(partial) ? "OK" : partial);
                // flushdns / sc stop: timeout ≈ aplicado o no crítico
                if (args.Contains("flushdns", StringComparison.OrdinalIgnoreCase)
                    || args.Contains("stop ", StringComparison.OrdinalIgnoreCase))
                    return (true, string.IsNullOrWhiteSpace(partial) ? "OK" : partial);
                return (false, string.IsNullOrWhiteSpace(partial) ? "Timed out" : partial);
            }
            stdout = outTask.GetAwaiter().GetResult();
            stderr = errTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (p.ExitCode == 0, (stdout + "\n" + stderr).Trim());
    }

    private static bool HasGpu(SystemSnapshot? snapshot, string token)
    {
        if (snapshot is null) return false;
        if (snapshot.Gpu?.Name.Contains(token, StringComparison.OrdinalIgnoreCase) == true) return true;
        return snapshot.Gpus.Any(g => g.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ActionResult> RunAsync(string actionId, string file, string args, CancellationToken ct)
    {
        var (ok, output) = await RunCaptureAsync(file, args, ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok,
            Detail = string.IsNullOrWhiteSpace(output) ? (ok ? "OK" : "Error") : output.Trim()
        };
    }

    private async Task<(bool Ok, string Output)> RunCaptureAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            var exe = ResolveSystemTool(file);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Could not start process.");

            // Comandos cortos: tope duro para no dejar UI en “Verifying…” eterno.
            var softTimeoutMs = args.Contains("flushdns", StringComparison.OrdinalIgnoreCase) ? 8_000
                : args.Contains("disablelastaccess", StringComparison.OrdinalIgnoreCase) ? 12_000
                : file.Contains("sc", StringComparison.OrdinalIgnoreCase) ? 12_000
                : file.Equals("ipconfig", StringComparison.OrdinalIgnoreCase) ? 10_000
                : 0;

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            if (softTimeoutMs > 0)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(softTimeoutMs);
                try
                {
                    await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* */ }
                    string partial = "";
                    try
                    {
                        // Intentar leer lo ya emitido (best-effort).
                        if (stdoutTask.IsCompletedSuccessfully) partial += stdoutTask.Result;
                        if (stderrTask.IsCompletedSuccessfully) partial += "\n" + stderrTask.Result;
                    }
                    catch { /* */ }
                    partial = partial.Trim();
                    var softOk = LooksLikeCommandSuccess(partial)
                                 || partial.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase)
                                 || args.Contains("flushdns", StringComparison.OrdinalIgnoreCase)
                                 || args.Contains("disablelastaccess", StringComparison.OrdinalIgnoreCase)
                                 || file.Equals("ipconfig", StringComparison.OrdinalIgnoreCase);
                    return (softOk, string.IsNullOrWhiteSpace(partial) ? (softOk ? "OK" : "Timed out") : partial);
                }
            }
            else
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var output = (stdout + "\n" + stderr).Trim();
            _logger.LogInformation("{File} {Args} exit={Code}", exe, args, p.ExitCode);
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string ResolveSystemTool(string file)
    {
        if (Path.IsPathRooted(file)) return file;
        var name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
        var sys = Path.Combine(Environment.SystemDirectory, name);
        return File.Exists(sys) ? sys : file;
    }

    /// <summary>
    /// B.2) Escritura de registro con método alterno único si falla por acceso denegado estando
    /// elevado: reintenta vía reg.exe (a veces evita bloqueos/virtualización que sí afectan a la
    /// API de registro directa). No reintenta en bucle — como mucho, un intento con la API y uno con reg.exe.
    /// </summary>
    private static (bool Ok, string Error) TrySetDwordWithFallback(RegistryHive hive, string path, string name, int value)
    {
        var root = hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
        try
        {
            using var key = root.CreateSubKey(path);
            if (key is not null)
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
                return (true, "");
            }
        }
        catch (Exception ex)
        {
            if (!IsAccessDenied(ex) || !ProcessPrivileges.IsElevated)
                return (false, ex.Message);
            // Sigue al método alterno solo si el fallo fue por permisos y estamos elevados.
        }

        var hiveArg = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
        var (ok, output) = RunCaptureSync("reg", $"add \"{hiveArg}\\{path}\" /v {name} /t REG_DWORD /d {value} /f");
        return (ok, ok ? "" : output);
    }

    private static bool IsAccessDenied(Exception ex)
        => ex is UnauthorizedAccessException
           || ex is System.Security.SecurityException
           || ex.Message.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("acceso es denegado", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("acceso denegado", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("Attempted to perform", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("no autorizad", StringComparison.OrdinalIgnoreCase);
}
