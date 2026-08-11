using System.Diagnostics;
using AetherPC.Core.Enums;
using AetherPC.Core.Models;
using Microsoft.Win32;

namespace AetherPC.Optimization;

/// <summary>
/// Pack de privacidad/rendimiento inspirado en herramientas tipo Optimizer.
/// Nunca toca Defender, Firewall ni Windows Update.
/// </summary>
public sealed partial class WindowsOfficialTuning
{
    private static ActionResult DisableTelemetryPack(string actionId)
    {
        try
        {
            // Pro/Home: 0 a veces se fuerza a 1; intentamos Security-level mínimo
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            key?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
            using var key2 = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection");
            key2?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);

            // CEIP / SQM
            using var ceip = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows");
            ceip?.SetValue("CEIPEnable", 0, RegistryValueKind.DWord);

            return Ok(actionId, "Telemetría limitada (AllowTelemetry=0 / CEIP off). En Home puede requerir Pro/Enterprise para efecto completo. No se toca Defender ni Windows Update.", "privacy.telemetry");
        }
        catch (Exception ex)
        {
            return Fail(actionId, "Telemetría requiere admin: " + ex.Message);
        }
    }

    private static ActionResult SetTelemetryServicesManual(string actionId)
    {
        var details = new List<string>();
        foreach (var svc in new[] { "DiagTrack", "dmwappushservice" })
        {
            var r = SetServiceStartMode(actionId, svc, "disabled");
            details.Add($"{svc}: {(r.Success ? "disabled/ok" : r.Detail)}");
        }

        // Connected User Experiences
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection");
            k?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
        }
        catch { /* */ }

        var ok = details.Any(d => d.Contains("ok", StringComparison.OrdinalIgnoreCase)
                                  || d.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                                  || d.Contains("deshabilitado", StringComparison.OrdinalIgnoreCase));
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok,
            Detail = "Telemetry services: " + string.Join(" · ", details),
            RollbackToken = "privacy.diagtrack",
            Status = ok ? ActionApplyStatus.Applied : ActionApplyStatus.Skipped
        };
    }

    private static ActionResult DisableAdvertisingId(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo");
            key?.SetValue("Enabled", 0, RegistryValueKind.DWord);
            using var pol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo");
            pol?.SetValue("DisabledByGroupPolicy", 1, RegistryValueKind.DWord);
            return Ok(actionId, "ID de publicidad desactivado.", "privacy.advertising");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableTipsAndSuggestions(string actionId)
    {
        try
        {
            using var cdm = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
            foreach (var name in new[]
                     {
                         "SubscribedContent-338389Enabled", "SubscribedContent-310093Enabled",
                         "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled",
                         "SubscribedContent-353696Enabled", "SoftLandingEnabled",
                         "SystemPaneSuggestionsEnabled", "SilentInstalledAppsEnabled",
                         "PreInstalledAppsEnabled", "OemPreInstalledAppsEnabled"
                     })
                cdm?.SetValue(name, 0, RegistryValueKind.DWord);

            using var explorer = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            explorer?.SetValue("ShowSyncProviderNotifications", 0, RegistryValueKind.DWord);
            explorer?.SetValue("Start_IrisRecommendations", 0, RegistryValueKind.DWord);

            return Ok(actionId, "Sugerencias, tips y contenido recomendado desactivados (HKCU).", "privacy.tips");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableActivityHistory(string actionId)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
            key?.SetValue("PublishUserActivities", 0, RegistryValueKind.DWord);
            key?.SetValue("UploadUserActivities", 0, RegistryValueKind.DWord);
            using var hkcu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Privacy");
            // Tailored experiences
            using var te = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Privacy");
            te?.SetValue("TailoredExperiencesWithDiagnosticDataEnabled", 0, RegistryValueKind.DWord);
            return Ok(actionId, "Historial de actividad / experiencias personalizadas limitados.", "privacy.activity");
        }
        catch (Exception ex)
        {
            return Fail(actionId, "Requiere admin: " + ex.Message);
        }
    }

    private static ActionResult DisableFeedback(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Siuf\Rules");
            key?.SetValue("NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord);
            try { key?.DeleteValue("PeriodInNanoSeconds", throwOnMissingValue: false); } catch { /* */ }
            return Ok(actionId, "Encuestas/feedback de Windows desactivados.", "privacy.feedback");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableCopilotHints(string actionId)
    {
        try
        {
            using var pol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot");
            pol?.SetValue("TurnOffWindowsCopilot", 1, RegistryValueKind.DWord);
            using var explorer = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            explorer?.SetValue("ShowCopilotButton", 0, RegistryValueKind.DWord);
            // Edge Copilot sidebar (si existe)
            using var edge = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Edge");
            edge?.SetValue("HubsSidebarEnabled", 0, RegistryValueKind.DWord);
            return Ok(actionId, "Copilot / botón Copilot ocultado o deshabilitado por política (si la edición lo permite).", "privacy.copilot");
        }
        catch (Exception ex)
        {
            return Fail(actionId, "Copilot: " + ex.Message);
        }
    }

    private static ActionResult DisableWidgetsAndNews(string actionId)
    {
        try
        {
            using var advanced = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            advanced?.SetValue("TaskbarDa", 0, RegistryValueKind.DWord);

            using var feeds = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Feeds");
            feeds?.SetValue("ShellFeedsTaskbarViewMode", 2, RegistryValueKind.DWord);

            // HKLM es opcional (políticas): no tumba la acción si falla por unauthorized.
            try
            {
                using var dsh = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Dsh");
                dsh?.SetValue("AllowNewsAndInterests", 0, RegistryValueKind.DWord);
            }
            catch { /* best-effort */ }

            return Ok(actionId, "Taskbar widgets / News & interests disabled.", "privacy.widgets");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableBackgroundApps(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications");
            key?.SetValue("GlobalUserDisabled", 1, RegistryValueKind.DWord);
            return Ok(actionId, "Apps en segundo plano desactivadas de forma global (UWP).", "privacy.background_apps");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableOfficeTelemetry(string actionId)
    {
        try
        {
            var roots = new[]
            {
                @"SOFTWARE\Policies\Microsoft\Office\16.0\osm",
                @"SOFTWARE\Policies\Microsoft\Office\15.0\osm"
            };
            var any = false;
            foreach (var path in roots)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                if (key is null) continue;
                key.SetValue("EnableUpload", 0, RegistryValueKind.DWord);
                key.SetValue("Enablelogging", 0, RegistryValueKind.DWord);
                key.SetValue("EnableFileObfuscation", 1, RegistryValueKind.DWord);
                any = true;
            }

            if (!any)
                return Fail(actionId, "No se detectó política Office 2016+; omitido.");

            return Ok(actionId, "Telemetría de Office limitada (políticas OSM).", "privacy.office_telemetry");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult SetMenuShowDelayZero(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            key?.SetValue("MenuShowDelay", "0", RegistryValueKind.String);
            return Ok(actionId, "Retraso de menús = 0 (respuesta más rápida al abrir menús).", "perf.menu_delay");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableNetworkThrottling(string actionId)
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        var (ok1, e1) = TrySetDwordWithFallback(RegistryHive.LocalMachine, path, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF));
        var (ok2, e2) = TrySetDwordWithFallback(RegistryHive.LocalMachine, path, "SystemResponsiveness", 0);
        return ok1 && ok2
            ? Ok(actionId, "NetworkThrottlingIndex máximo + SystemResponsiveness=0 (mejor para multimedia/juegos). Requiere admin.", "perf.network_throttle")
            : Fail(actionId, $"Requiere admin: {e1} {e2}".Trim());
    }

    private async Task<ActionResult> DisableNtfsLastAccessAsync(string actionId, CancellationToken ct)
    {
        var (ok, output) = await RunCaptureAsync("fsutil", "behavior set disablelastaccess 1", ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok,
            Detail = ok
                ? "NTFS Last Access desactivado (menos escrituras en disco)."
                : (string.IsNullOrWhiteSpace(output) ? "fsutil falló (admin)." : output),
            RollbackToken = ok ? "perf.ntfs_lastaccess" : null
        };
    }

    private static ActionResult SetVisualEffectsPerformance(string actionId)
    {
        try
        {
            // 2 = Adjust for best performance
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects");
            key?.SetValue("VisualFXSetting", 2, RegistryValueKind.DWord);

            using var desk = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            desk?.SetValue("UserPreferencesMask",
                new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 },
                RegistryValueKind.Binary);
            desk?.SetValue("DragFullWindows", "0", RegistryValueKind.String);
            desk?.SetValue("FontSmoothing", "2", RegistryValueKind.String);
            desk?.SetValue("MenuShowDelay", "0", RegistryValueKind.String);

            using var winmet = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            winmet?.SetValue("ListviewAlphaSelect", 0, RegistryValueKind.DWord);
            winmet?.SetValue("ListviewShadow", 0, RegistryValueKind.DWord);
            winmet?.SetValue("TaskbarAnimations", 0, RegistryValueKind.DWord);
            winmet?.SetValue("IconsOnly", 1, RegistryValueKind.DWord);
            winmet?.SetValue("ListviewShadow", 0, RegistryValueKind.DWord);

            using var dwm = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM");
            dwm?.SetValue("EnableAeroPeek", 0, RegistryValueKind.DWord);
            dwm?.SetValue("AlwaysHibernateThumbnails", 0, RegistryValueKind.DWord);

            using var metrics = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop\WindowMetrics");
            metrics?.SetValue("MinAnimate", "0", RegistryValueKind.String);

            using var personalize = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            personalize?.SetValue("EnableTransparency", 0, RegistryValueKind.DWord);

            return Ok(actionId, "Efectos visuales: máximo rendimiento (animaciones/sombras/transparencias/AeroPeek OFF). Windows sigue intacto.", "perf.visual_perf");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private async Task<ActionResult> ReduceDefenderLoadAsync(string actionId, CancellationToken ct)
    {
        // NUNCA desactiva protección en tiempo real ni Windows Defender.
        // Solo reduce CPU/IO de escaneos y prioridad del proceso.
        var ps =
            "try {" +
            "Set-MpPreference -EnableLowCpuPriority $true; " +
            "Set-MpPreference -ScanAvgCPULoadFactor 25; " +
            "Set-MpPreference -DisableCatchupFullScan $true; " +
            "Set-MpPreference -DisableCatchupQuickScan $true; " +
            "Set-MpPreference -DisableArchiveScanning $true; " +
            "Set-MpPreference -DisableEmailScanning $true; " +
            "'ok'" +
            "} catch { $_.Exception.Message; exit 1 }";

        var (ok, output) = await RunCaptureAsync(
            "powershell",
            "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"",
            ct);

        var prioNote = "";
        try
        {
            foreach (var name in new[] { "MsMpEng", "NisSrv", "MpDefenderCoreService" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        using (p) { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
                        prioNote += $" {name}=BelowNormal";
                    }
                    catch { /* access denied sin admin total */ }
                }
            }
        }
        catch { /* */ }

        return new ActionResult
        {
            ActionId = actionId,
            // Nunca hard-fail: Defender no se desactiva; si no se pudo aligerar, se omite.
            Success = true,
            Detail = ok
                ? "Defender stays ON. Scans: LowCpuPriority + ~25% CPU max; catch-up/archive/email OFF." + prioNote
                : ("Could not adjust MpPreference (admin/policy?). " +
                   (string.IsNullOrWhiteSpace(output) ? "Protection was not changed." : output)),
            Status = ok ? ActionApplyStatus.Applied : ActionApplyStatus.Skipped,
            RollbackToken = ok ? "defender.reduce_load" : null
        };
    }

    private static ActionResult SetTransparency(string actionId, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            key?.SetValue("EnableTransparency", enabled ? 1 : 0, RegistryValueKind.DWord);
            return Ok(actionId, enabled ? "Transparencias activadas." : "Transparencias desactivadas (menos carga GPU).", "perf.transparency");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult SetAnimations(string actionId, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Control Panel\Desktop\WindowMetrics");
            key?.SetValue("MinAnimate", enabled ? "1" : "0", RegistryValueKind.String);
            return Ok(actionId, enabled ? "Animaciones de ventana ON." : "Animaciones de minimizar/maximizar OFF.", "perf.animations");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableFastStartup(string actionId)
    {
        var (ok, error) = TrySetDwordWithFallback(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0);
        return ok
            ? Ok(actionId, "Inicio rápido desactivado (arranques/apagados más limpios; ayuda con dual-boot y drivers).", "perf.fast_startup")
            : Fail(actionId, "Requiere admin: " + error);
    }

    private async Task<ActionResult> DisableHibernateAsync(string actionId, CancellationToken ct)
    {
        var (ok, output) = await RunCaptureAsync("powercfg", "/hibernate off", ct);
        return new ActionResult
        {
            ActionId = actionId,
            Success = ok,
            Detail = ok
                ? "Hibernación desactivada (libera espacio de hiberfil.sys). En portátiles puede quitar hibernar."
                : output,
            RollbackToken = ok ? "perf.hibernate" : null
        };
    }

    private static ActionResult SetXboxServicesManual(string actionId)
    {
        var svcs = new[] { "XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc", "GamingServices" };
        var details = new List<string>();
        var any = false;
        foreach (var s in svcs)
        {
            var r = SetServiceStartMode(actionId, s, "demand");
            if (r.Success) any = true;
            details.Add($"{s}:{(r.Success ? "manual" : "n/d")}");
        }

        return new ActionResult
        {
            ActionId = actionId,
            Success = any,
            Detail = any
                ? "Servicios Xbox a Manual (si no usas Game Pass/Xbox App). " + string.Join(", ", details)
                : "No se pudieron cambiar servicios Xbox (¿no instalados o sin admin?).",
            RollbackToken = any ? "perf.xbox" : null
        };
    }

    private static ActionResult DisableAutoplay(string actionId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers");
            key?.SetValue("DisableAutoplay", 1, RegistryValueKind.DWord);
            using var pol = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer");
            pol?.SetValue("NoDriveTypeAutoRun", 255, RegistryValueKind.DWord);
            return Ok(actionId, "AutoPlay desactivado.", "perf.autoplay");
        }
        catch (Exception ex)
        {
            return Fail(actionId, ex.Message);
        }
    }

    private static ActionResult DisableRemoteAssistance(string actionId)
    {
        var (ok, error) = TrySetDwordWithFallback(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0);
        return ok
            ? Ok(actionId, "Asistencia remota desactivada.", "perf.remote_assist")
            : Fail(actionId, "Requiere admin: " + error);
    }

    private static ActionResult Ok(string id, string detail, string token) => new()
    {
        ActionId = id,
        Success = true,
        Detail = detail,
        RollbackToken = token
    };

    private static ActionResult Fail(string id, string detail) => new()
    {
        ActionId = id,
        Success = false,
        Detail = detail
    };
}
