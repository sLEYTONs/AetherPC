using System.Text.Json;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using Microsoft.Win32;

namespace AetherPC.Optimization;

/// <summary>
/// Lectura de estado, verificación y soft-rollback — misma clase WindowsOfficialTuning
/// (sin SystemStateProbe ni ProcessRunner nuevos).
/// </summary>
public sealed partial class WindowsOfficialTuning
{
    public async Task<SystemOptimizationState> ReadSystemStateAsync(SystemSnapshot? snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var notes = new List<string>();
        string? scheme = null, schemeName = null;
        try
        {
            var (ok, output) = await RunCaptureAsync("powercfg", "/getactivescheme", ct).ConfigureAwait(false);
            if (ok && !string.IsNullOrWhiteSpace(output))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    output,
                    @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]+)\)");
                if (m.Success)
                {
                    scheme = m.Groups[1].Value;
                    schemeName = LocalizePowerScheme(scheme, m.Groups[2].Value.Trim());
                }
                else
                    schemeName = LocalizePowerScheme(null, output.Trim());
            }
            else
                notes.Add(Loc.T("State.PowerUnavailable"));
        }
        catch (Exception ex)
        {
            notes.Add(Loc.T("State.PowerError", ex.Message));
        }

        int? gameMode = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        int? hags = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        int? dvr = ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
        int? storage = ReadDword(Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01");
        int? delivery = ReadDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode");

        var sysMain = QueryServiceStartType("SysMain");
        var search = QueryServiceStartType("WSearch");

        var hasSsd = snapshot?.Disks.Any(d =>
            d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            d.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) == true;
        bool? trim = hasSsd ? true : snapshot?.Disks.Count > 0 ? false : null;
        if (trim is null) notes.Add(Loc.T("State.TrimUnknown"));

        var page = snapshot?.Memory.PageFileConfigDetail
                   ?? (snapshot?.Memory.PageFileSystemManaged == true ? Loc.T("State.PagefileManaged") : null);

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(schemeName)) lines.Add(Loc.T("State.PowerLine", schemeName));
        if (gameMode is int gm) lines.Add($"Game Mode: {(gm != 0 ? "ON" : "OFF")}");
        else notes.Add(Loc.T("State.GameModeUnread"));
        if (hags is int h) lines.Add($"HAGS: {(h == 2 ? "ON" : "OFF")}");
        if (!string.IsNullOrWhiteSpace(sysMain)) lines.Add($"SysMain: {sysMain}");
        if (!string.IsNullOrWhiteSpace(search)) lines.Add($"WSearch: {search}");
        if (page is not null) lines.Add($"Pagefile: {page}");

        return new SystemOptimizationState
        {
            ActivePowerScheme = scheme,
            ActivePowerSchemeName = schemeName,
            GameModeEnabled = gameMode,
            HagsMode = hags,
            GameDvrEnabled = dvr,
            SysMainStartType = sysMain,
            SearchStartType = search,
            StorageSenseEnabled = storage,
            DeliveryOptMode = delivery,
            TrimSupported = trim,
            PageFileDetail = page,
            Notes = notes,
            SummaryText = string.Join(" · ", lines)
        };
    }

    private static string LocalizePowerScheme(string? guid, string rawName)
    {
        var g = (guid ?? "").Trim().ToLowerInvariant();
        if (g is "381b4222-f694-41f0-9685-ff5bb260df2e") return Loc.T("Power.Balanced");
        if (g is "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") return Loc.T("Power.HighPerf");
        if (g is "a1841308-3541-4fab-bc81-f71556f20b4a") return Loc.T("Power.Saver");
        if (g is "e9a42b02-d5df-448d-aa00-03f14749eb61") return Loc.T("Power.Ultimate");

        var n = rawName ?? "";
        if (n.Contains("equilibrad", StringComparison.OrdinalIgnoreCase)
            || n.Contains("balanced", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Power.Balanced");
        if (n.Contains("alto rendimiento", StringComparison.OrdinalIgnoreCase)
            || n.Contains("high performance", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Power.HighPerf");
        if (n.Contains("ahorro", StringComparison.OrdinalIgnoreCase)
            || n.Contains("power saver", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Power.Saver");
        if (n.Contains("rendimiento máximo", StringComparison.OrdinalIgnoreCase)
            || n.Contains("ultimate", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Power.Ultimate");
        return string.IsNullOrWhiteSpace(n) ? Loc.T("Common.NotDetected") : n;
    }

    public async Task<(bool Verified, string? Detail, string? AfterValue)> VerifyAppliedAsync(
        string actionId, SystemSnapshot? snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            switch (actionId)
            {
                case "windows.gamemode":
                {
                    var v = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                    var ok = v is 1;
                    return (ok, ok ? "Game Mode = 1" : $"Game Mode = {v?.ToString() ?? "N/D"}", v?.ToString());
                }
                case "windows.hags":
                {
                    var v = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
                    var ok = v is 2;
                    return (ok, ok ? "HwSchMode=2" : $"HwSchMode={v?.ToString() ?? "N/D"}", v?.ToString());
                }
                case "windows.gamedvr_off":
                {
                    var v = ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
                    var ok = v is 0;
                    return (ok, ok ? "GameDVR_Enabled=0" : $"GameDVR={v?.ToString() ?? "N/D"}", v?.ToString());
                }
                case "windows.storage_sense":
                {
                    var v = ReadDword(Registry.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01");
                    var ok = v is 1;
                    return (ok, ok ? "Storage Sense ON" : $"StorageSense={v?.ToString() ?? "N/D"}", v?.ToString());
                }
                case "windows.delivery_opt":
                {
                    var v = ReadDword(Registry.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode");
                    var ok = v is 1;
                    return (ok, ok ? "DODownloadMode=1 (LAN)" : $"DO={v?.ToString() ?? "N/D"}", v?.ToString());
                }
                case "service.sysmain.manual":
                {
                    var t = QueryServiceStartType("SysMain");
                    var ok = t is not null &&
                             (t.Contains("DEMAND", StringComparison.OrdinalIgnoreCase) ||
                              t.Contains("MANUAL", StringComparison.OrdinalIgnoreCase) ||
                              t.Contains("3", StringComparison.OrdinalIgnoreCase));
                    return (ok, $"SysMain start={t ?? "N/D"}", t);
                }
                case "service.search.manual":
                {
                    var t = QueryServiceStartType("WSearch");
                    var ok = t is not null &&
                             (t.Contains("DEMAND", StringComparison.OrdinalIgnoreCase) ||
                              t.Contains("MANUAL", StringComparison.OrdinalIgnoreCase) ||
                              t.Contains("3", StringComparison.OrdinalIgnoreCase));
                    return (ok, $"WSearch start={t ?? "N/D"}", t);
                }
                case "power.high" or "power.balanced" or "power.ultimate":
                {
                    var state = await ReadSystemStateAsync(snapshot, ct).ConfigureAwait(false);
                    var name = state.ActivePowerSchemeName ?? "N/D";
                    var ok = actionId switch
                    {
                        "power.balanced" => name.Contains("balanced", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("equilibrado", StringComparison.OrdinalIgnoreCase),
                        "power.high" => name.Contains("high", StringComparison.OrdinalIgnoreCase) ||
                                        name.Contains("alto", StringComparison.OrdinalIgnoreCase) ||
                                        name.Contains("rendimiento", StringComparison.OrdinalIgnoreCase),
                        "power.ultimate" => name.Contains("ultimate", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("high", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("alto", StringComparison.OrdinalIgnoreCase),
                        _ => true
                    };
                    return (ok, $"Plan activo: {name}", name);
                }
                case "perf.transparency_off":
                {
                    var v = ReadDword(Registry.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
                    var ok = v is 0;
                    return (ok, $"EnableTransparency={v?.ToString() ?? "N/D"}", v?.ToString());
                }
                case "perf.animations_off":
                {
                    var v = ReadDword(Registry.CurrentUser,
                        @"Control Panel\Desktop\WindowMetrics", "MinAnimate");
                    // MinAnimate may be string "0"
                    var s = ReadString(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate");
                    var ok = v is 0 || s == "0";
                    return (ok, $"MinAnimate={s ?? v?.ToString() ?? "N/D"}", s ?? v?.ToString());
                }
                case "net.flushdns":
                    return (true, "flushdns ejecutado (sin estado persistente)", null);
                case "disk.trim":
                    return (true, "TRIM solicitado en volúmenes SSD/NVMe", null);
                case "repair.sfc" or "repair.dism" or "repair.netreset":
                    return (true, "Comando de reparación ejecutado — revisar salida", null);
                default:
                    // Sin re-lectura de estado: no marcar verificado
                    return (false, "Sin verificación profunda para esta acción", null);
            }
        }
        catch (Exception ex)
        {
            return (false, "Verify: " + ex.Message, null);
        }
    }

    /// <summary>Revierte un token producido por ExecuteAsync / startup.</summary>
    public async Task<(bool Ok, string Detail)> RollbackTokenAsync(string actionId, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (token.StartsWith("startup:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = token["startup:".Length..];
                var pipe = payload.IndexOf('|');
                if (pipe <= 0) return (false, "Token startup inválido.");
                var name = payload[..pipe];
                var cmd = payload[(pipe + 1)..];
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                key?.SetValue(name, cmd, RegistryValueKind.String);
                return (true, $"Restaurado HKCU\\Run: {name}");
            }

            if (token.StartsWith("startuplnk:", StringComparison.OrdinalIgnoreCase))
            {
                var bak = token["startuplnk:".Length..];
                if (!File.Exists(bak)) return (false, "Backup .aetherbak no encontrado.");
                var dest = bak.EndsWith(".aetherbak", StringComparison.OrdinalIgnoreCase)
                    ? bak[..^".aetherbak".Length]
                    : bak + ".lnk";
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(bak, dest);
                return (true, "Acceso directo de inicio restaurado.");
            }

            if (token.StartsWith("{", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(token);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
                if (kind == "power" && root.TryGetProperty("prevScheme", out var ps))
                {
                    var guid = ps.GetString();
                    if (string.IsNullOrWhiteSpace(guid)) return (false, "Sin GUID previo.");
                    var (ok, output) = await RunCaptureAsync("powercfg", $"/setactive {guid}", ct).ConfigureAwait(false);
                    return (ok, ok ? $"Plan restaurado: {guid}" : output);
                }
                if (kind == "svc" && root.TryGetProperty("name", out var sn) && root.TryGetProperty("prev", out var prev))
                {
                    var name = sn.GetString()!;
                    var mode = MapStartModeArg(prev.GetString());
                    var (ok, output) = RunCaptureSync("sc", $"config {name} start= {mode}");
                    return (ok, ok ? $"{name} → {mode}" : output);
                }
                if (kind == "reg")
                    return RestoreRegSnapshot(root);
            }

            // Tokens legacy cortos
            return token switch
            {
                "gamemode" => RestoreDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 0)
                              ? (true, "Game Mode revertido (0)")
                              : (false, "No se pudo revertir Game Mode"),
                "hags" => RestoreDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1)
                    ? (true, "HAGS revertido (1)")
                    : (false, "HAGS revert requiere admin"),
                "gamedvr" => RestoreDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1)
                    ? (true, "Game DVR reactivado")
                    : (false, "No se pudo revertir Game DVR"),
                "storagesense" => RestoreDword(Registry.CurrentUser,
                    @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0)
                    ? (true, "Storage Sense OFF")
                    : (false, "No se pudo revertir Storage Sense"),
                "do" => RestoreDword(Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 0)
                    ? (true, "Delivery Optimization restaurado")
                    : (false, "DO revert requiere admin"),
                "power.prev" => await RestorePreviousPowerAsync(ct).ConfigureAwait(false),
                _ when token.StartsWith("svc:", StringComparison.OrdinalIgnoreCase) =>
                    RestoreServiceAuto(token["svc:".Length..]),
                _ => (false, $"Token no reversible automáticamente: {token}")
            };
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public string DescribeCurrentState(string actionId, SystemOptimizationState state)
    {
        return actionId switch
        {
            "windows.gamemode" => state.GameModeEnabled is int g
                ? (g != 0 ? "Game Mode ON" : "Game Mode OFF")
                : "Game Mode N/D",
            "windows.hags" => state.HagsMode is int h
                ? (h == 2 ? "HAGS ON" : "HAGS OFF/otro")
                : "HAGS N/D",
            "windows.gamedvr_off" => state.GameDvrEnabled is int d
                ? (d != 0 ? "Game DVR ON" : "Game DVR OFF")
                : "Game DVR N/D",
            "service.sysmain.manual" => string.IsNullOrWhiteSpace(state.SysMainStartType)
                ? "SysMain N/D"
                : $"SysMain = {state.SysMainStartType}",
            "service.search.manual" => string.IsNullOrWhiteSpace(state.SearchStartType)
                ? "WSearch N/D"
                : $"WSearch = {state.SearchStartType}",
            "power.high" or "power.balanced" or "power.ultimate" or "power.cpu_max" =>
                state.ActivePowerSchemeName is { Length: > 0 } n ? $"Plan: {n}" : "Plan N/D",
            "windows.storage_sense" => state.StorageSenseEnabled is int s
                ? (s != 0 ? "Storage Sense ON" : "Storage Sense OFF")
                : "Storage Sense N/D",
            "windows.delivery_opt" => state.DeliveryOptMode is int m
                ? $"DODownloadMode={m}"
                : "Delivery Opt N/D",
            "disk.trim" => state.TrimSupported == true ? "SSD/NVMe (TRIM aplicable)"
                : state.TrimSupported == false ? "Sin SSD/NVMe" : "TRIM N/D",
            _ => ""
        };
    }

    private static string? QueryServiceStartType(string serviceName)
    {
        try
        {
            var (ok, output) = RunCaptureSync("sc", $"qc {serviceName}");
            if (!ok && string.IsNullOrWhiteSpace(output)) return null;
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("TIPO_DE_INICIO", StringComparison.OrdinalIgnoreCase))
                    continue;
                return line.Trim();
            }
            return output.Length > 0 ? "leído (ver sc qc)" : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            var v = key?.GetValue(name);
            return v switch
            {
                int i => i,
                long l => (int)l,
                _ => null
            };
        }
        catch { return null; }
    }

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    private static bool RestoreDword(RegistryKey root, string path, string name, int value)
    {
        try
        {
            using var key = root.CreateSubKey(path);
            key?.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    private static (bool Ok, string Detail) RestoreServiceAuto(string serviceName)
    {
        var (ok, output) = RunCaptureSync("sc", $"config {serviceName} start= auto");
        return (ok, ok ? $"{serviceName} → auto" : output);
    }

    private static string MapStartModeArg(string? prev)
    {
        if (string.IsNullOrWhiteSpace(prev)) return "auto";
        if (prev.Contains("DEMAND", StringComparison.OrdinalIgnoreCase) ||
            prev.Contains("3", StringComparison.OrdinalIgnoreCase)) return "demand";
        if (prev.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) ||
            prev.Contains("4", StringComparison.OrdinalIgnoreCase)) return "disabled";
        return "auto";
    }

    private async Task<(bool Ok, string Detail)> RestorePreviousPowerAsync(CancellationToken ct)
    {
        // Sin GUID guardado: volver a Balanced
        var (ok, output) = await RunCaptureAsync("powercfg", "/setactive SCHEME_BALANCED", ct).ConfigureAwait(false);
        return (ok, ok ? "Plan Equilibrado restaurado (fallback)" : output);
    }

    private static (bool Ok, string Detail) RestoreRegSnapshot(JsonElement root)
    {
        try
        {
            var hiveName = root.GetProperty("hive").GetString() ?? "hkcu";
            var path = root.GetProperty("path").GetString() ?? "";
            var hive = hiveName.Equals("hklm", StringComparison.OrdinalIgnoreCase)
                ? Registry.LocalMachine
                : Registry.CurrentUser;
            using var key = hive.CreateSubKey(path);
            if (key is null) return (false, "No se abrió clave.");
            if (!root.TryGetProperty("values", out var values)) return (false, "Sin values.");
            foreach (var prop in values.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    key.SetValue(prop.Name, prop.Value.GetInt32(), RegistryValueKind.DWord);
                else
                    key.SetValue(prop.Name, prop.Value.GetString() ?? "", RegistryValueKind.String);
            }
            return (true, $"Registro restaurado: {path}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string BuildPowerRollbackToken(string? prevSchemeGuid) =>
        JsonSerializer.Serialize(new { kind = "power", prevScheme = prevSchemeGuid ?? "" });

    private static string BuildServiceRollbackToken(string serviceName, string? prevLine) =>
        JsonSerializer.Serialize(new { kind = "svc", name = serviceName, prev = prevLine ?? "AUTO_START" });
}
