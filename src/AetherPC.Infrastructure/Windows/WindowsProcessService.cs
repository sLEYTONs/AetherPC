using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using AetherPC.Core.Windows;
using Microsoft.Win32;

namespace AetherPC.Infrastructure.Windows;

public sealed class WindowsProcessService : IProcessService
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "dwm", "explorer", "fontdrvhost", "conhost",
        "Memory Compression", "MsMpEng", "SecurityHealthService", "NisSrv", "SgrmBroker",
        "audiodg", "sihost", "taskhostw", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "ctfmon", "dllhost", "WmiPrvSE", "AetherPC",
        // Reforzado: componentes de arranque/actualización/indexado que no deben cerrarse/suspenderse
        "TrustedInstaller", "TiWorker", "wuauclt", "MoUsoCoreWorker", "SearchIndexer",
        "SearchProtocolHost", "SearchFilterHost", "spoolsv", "smartscreen", "WerFault",
        "WerFaultSecure", "LogonUI", "userinit", "wlanext", "LsaIso", "WUDFHost", "PresentationFontCache"
    };

    private static readonly string[] SecurityTokens =
    {
        "Defender", "MsMpEng", "SecurityHealth", "CrowdStrike", "Sentinel", "Avast", "AVG",
        "Norton", "McAfee", "Kaspersky", "Bitdefender", "Malwarebytes", "ESET", "Sophos",
        "WdNisSvc", "MpDefenderCoreService", "SenseIR", "MsSense", "SenseCncProxy"
    };

    private static readonly string[] LauncherTokens =
    {
        "steam", "epicgameslauncher", "origin", "eadesktop", "battle.net", "riotclient",
        "ubisoftconnect", "gog galaxy", "xboxapp", "gamebar"
    };

    private static readonly string[] UpdaterTokens =
    {
        "update", "updater", "setup", "installagent", "software_reporter", "googleupdate",
        "microsoftedgeupdate", "adobeupdater", "crashpad", "crashreporter"
    };

    private static readonly string[] TelemetryTokens =
    {
        "telemetry", "compattelrunner", "diagtrack", "ceip", "watson"
    };

    public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default)
    {
        // Muestreo corto y ligero: la UI de Procesos no puede bloquearse 10s+
        var sampled = await SampleAsync(TimeSpan.FromMilliseconds(450), ct).ConfigureAwait(false);
        return sampled.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes).ToList();
    }

    public async Task<IReadOnlyList<ProcessOptimizationHint>> AnalyzeForOptimizationAsync(
        SystemSnapshot snapshot,
        IReadOnlyList<StartupItem>? startupItems = null,
        CancellationToken ct = default,
        bool beastMode = false)
    {
        var processes = await SampleAsync(TimeSpan.FromMilliseconds(beastMode ? 900 : 550), ct).ConfigureAwait(false);
        // Adaptar umbrales de procesos al tramo de RAM del equipo (misma lógica que HardwareProfileBuilder)
        var ramGb = snapshot.Memory.TotalBytes / (1024.0 * 1024 * 1024);
        var ramThreshold = ramGb switch
        {
            <= 0 => 700L * 1024 * 1024,
            < 5 => 350L * 1024 * 1024,
            < 7 => 350L * 1024 * 1024,
            < 10 => 550L * 1024 * 1024,
            < 14 => 900L * 1024 * 1024,
            < 20 => 1400L * 1024 * 1024,
            _ => 2200L * 1024 * 1024
        };
        if (beastMode)
            ramThreshold = Math.Max(220L * 1024 * 1024, ramThreshold / 2);
        var cpuThreshold = Math.Max(beastMode ? 4.0 : 8.0, 100.0 / Math.Max(1, snapshot.Cpu.LogicalProcessors) * (beastMode ? 1.2 : 2));
        var allowRamClose = ramGb > 0 && (beastMode || ramGb < 14);

        var startupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (startupItems is not null)
        {
            foreach (var s in startupItems)
            {
                startupNames.Add(s.Name);
                var exe = ExtractExeName(s.Command);
                if (!string.IsNullOrEmpty(exe)) startupNames.Add(exe);
            }
        }

        var hints = new List<ProcessOptimizationHint>();

        foreach (var p in processes)
        {
            ct.ThrowIfCancellationRequested();
            if (IsProtected(p)) continue;
            if (p.Category is ProcessCategory.System or ProcessCategory.WindowsComponent or ProcessCategory.Security)
                continue;
            if (p.HasMainWindow && p.Responding)
                continue; // no cerrar apps con ventana visible en uso

            var key = !string.IsNullOrWhiteSpace(p.Path) ? p.Path! : p.Name;
            var isStartup = p.IsLikelyStartup || startupNames.Contains(p.Name);

            // Actualizador / launcher en segundo plano
            if (p.Category is ProcessCategory.Updater or ProcessCategory.Launcher && !p.HasMainWindow)
            {
                hints.Add(new ProcessOptimizationHint
                {
                    ActionId = "process.close",
                    TargetKey = key,
                    DisplayName = Loc.T("ProcHint.CloseBg.Name", p.Name),
                    WhatWillHappen = Loc.T("ProcHint.CloseBg.What", p.Name, p.Pid),
                    Why = p.Category == ProcessCategory.Updater
                        ? Loc.T("ProcHint.CloseBg.WhyUpdater")
                        : Loc.T("ProcHint.CloseBg.WhyLauncher"),
                    ExpectedImpact = Loc.T("ProcHint.CloseBg.Impact"),
                    Risk = RiskLevel.Low,
                    IsRecommendedDefault = true,
                    ActionKind = ProcessActionKind.CloseGraceful,
                    SamplePid = p.Pid
                });
                continue;
            }

            // CPU sostenida en background
            if (p.CpuPercent >= cpuThreshold && !p.HasMainWindow &&
                p.Category is ProcessCategory.Background or ProcessCategory.Helper or ProcessCategory.Telemetry
                    or ProcessCategory.Unknown or ProcessCategory.Disposable)
            {
                hints.Add(new ProcessOptimizationHint
                {
                    ActionId = "process.priority_low",
                    TargetKey = key,
                    DisplayName = Loc.T("ProcHint.Priority.Name", p.Name),
                    WhatWillHappen = Loc.T("ProcHint.Priority.What", p.Name),
                    Why = Loc.T("ProcHint.Priority.Why", p.CpuPercent),
                    ExpectedImpact = Loc.T("ProcHint.Priority.Impact"),
                    Risk = RiskLevel.Low,
                    IsRecommendedDefault = true,
                    ActionKind = ProcessActionKind.SetPriorityBelowNormal,
                    SamplePid = p.Pid
                });
            }

            // Mucha RAM privada en background
            if (allowRamClose && p.PrivateBytes >= ramThreshold && !p.HasMainWindow &&
                p.Category is ProcessCategory.Helper or ProcessCategory.Telemetry or ProcessCategory.Disposable)
            {
                if (hints.All(h => h.TargetKey != key || h.ActionId != "process.close"))
                {
                    hints.Add(new ProcessOptimizationHint
                    {
                        ActionId = "process.close",
                        TargetKey = key,
                        DisplayName = Loc.T("ProcHint.CloseHelper.Name", p.Name),
                        WhatWillHappen = Loc.T("ProcHint.CloseHelper.What", p.Name, p.PrivateBytes / (1024.0 * 1024)),
                        Why = Loc.T("ProcHint.CloseHelper.Why", ramThreshold / (1024 * 1024), ramGb),
                        ExpectedImpact = Loc.T("ProcHint.CloseHelper.Impact"),
                        Risk = beastMode ? RiskLevel.Low : RiskLevel.Medium,
                        IsRecommendedDefault = beastMode,
                        ActionKind = ProcessActionKind.CloseGraceful,
                        SamplePid = p.Pid
                    });
                }
            }
            else if (!allowRamClose && p.PrivateBytes >= ramThreshold && !p.HasMainWindow &&
                     p.Category == ProcessCategory.Telemetry)
            {
                if (hints.All(h => h.TargetKey != key || h.ActionId != "process.close"))
                {
                    hints.Add(new ProcessOptimizationHint
                    {
                        ActionId = "process.close",
                        TargetKey = key,
                        DisplayName = Loc.T("ProcHint.CloseTelemetry.Name", p.Name),
                        WhatWillHappen = Loc.T("ProcHint.CloseTelemetry.What", p.Name, ramGb),
                        Why = Loc.T("ProcHint.CloseTelemetry.Why"),
                        ExpectedImpact = Loc.T("ProcHint.CloseTelemetry.Impact"),
                        Risk = RiskLevel.Low,
                        IsRecommendedDefault = beastMode,
                        ActionKind = ProcessActionKind.CloseGraceful,
                        SamplePid = p.Pid
                    });
                }
            }

            if (beastMode && !p.HasMainWindow && !p.IsProtected &&
                (p.Category is ProcessCategory.Background or ProcessCategory.Helper or ProcessCategory.Updater
                    or ProcessCategory.Launcher or ProcessCategory.Unknown or ProcessCategory.Disposable
                    or ProcessCategory.Telemetry) &&
                hints.All(h => !(h.TargetKey == key && h.ActionId == "process.priority_low")))
            {
                hints.Add(new ProcessOptimizationHint
                {
                    ActionId = "process.priority_low",
                    TargetKey = key,
                    DisplayName = Loc.T("ProcHint.LimitCpu.Name", p.Name),
                    WhatWillHappen = Loc.T("ProcHint.LimitCpu.What", p.Name),
                    Why = Loc.T("ProcHint.LimitCpu.Why"),
                    ExpectedImpact = Loc.T("ProcHint.LimitCpu.Impact"),
                    Risk = RiskLevel.Low,
                    IsRecommendedDefault = true,
                    ActionKind = ProcessActionKind.SetPriorityBelowNormal,
                    SamplePid = p.Pid
                });
            }

            if (!p.Responding && !p.IsProtected)
            {
                hints.Add(new ProcessOptimizationHint
                {
                    ActionId = "process.close",
                    TargetKey = key,
                    DisplayName = Loc.T("ProcHint.CloseHang.Name", p.Name),
                    WhatWillHappen = Loc.T("ProcHint.CloseHang.What", p.Name),
                    Why = Loc.T("ProcHint.CloseHang.Why"),
                    ExpectedImpact = Loc.T("ProcHint.CloseHang.Impact"),
                    Risk = RiskLevel.Medium,
                    IsRecommendedDefault = beastMode,
                    ActionKind = ProcessActionKind.CloseGraceful,
                    SamplePid = p.Pid
                });
            }

            if (isStartup && !p.HasMainWindow && p.Category is ProcessCategory.User or ProcessCategory.Background
                or ProcessCategory.Launcher or ProcessCategory.Updater)
            {
                var maxStartupHints = beastMode ? 12 : 5;
                if (hints.Count(h => h.ActionId == "process.disable_startup") < maxStartupHints &&
                    hints.All(h => !(h.ActionId == "process.disable_startup" && h.TargetKey.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    hints.Add(new ProcessOptimizationHint
                    {
                        ActionId = "process.disable_startup",
                        TargetKey = p.Name,
                        DisplayName = Loc.T("ProcHint.DisableStartup.Name", p.Name),
                        WhatWillHappen = Loc.T("ProcHint.DisableStartup.What", p.Name),
                        Why = Loc.T("ProcHint.DisableStartup.Why"),
                        ExpectedImpact = Loc.T("ProcHint.DisableStartup.Impact"),
                        Risk = RiskLevel.Low,
                        IsRecommendedDefault = beastMode,
                        RequiresElevation = false,
                        ActionKind = ProcessActionKind.DisableStartup,
                        SamplePid = p.Pid
                    });
                }
            }
        }
        // Limitar sugerencias para no saturar el plan (Bestia admite más)
        return hints
            .GroupBy(h => h.ActionId + "|" + h.TargetKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(h => h.Risk)
            .ThenByDescending(h => h.IsRecommendedDefault)
            .Take(beastMode ? 28 : 12)
            .ToList();
    }

    public async Task<ActionResult> CloseGracefulAsync(int pid, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var info = FromProcessQuick(p);
                if (IsProtected(info))
                    return new ActionResult { ActionId = "process.close", Success = false, Detail = $"Protegido: {info.Name}" };

                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    p.CloseMainWindow();
                    if (p.WaitForExit(4000))
                        return new ActionResult
                        {
                            ActionId = "process.close",
                            Success = true,
                            Detail = $"Cerrado: {info.Name} ({pid})",
                            RollbackToken = $"reopen:{info.Path}"
                        };

                    return new ActionResult
                    {
                        ActionId = "process.close",
                        Success = false,
                        Detail = $"«{info.Name}» no cerró (puede pedir guardar). Ciérrala en la ventana o usa forzar desde Optimizar."
                    };
                }

                // Sin ventana: terminar proceso en segundo plano
                p.Kill(entireProcessTree: false);
                if (p.WaitForExit(3000))
                    return new ActionResult
                    {
                        ActionId = "process.close",
                        Success = true,
                        Detail = $"Cerrado (segundo plano): {info.Name} ({pid})",
                        RollbackToken = $"reopen:{info.Path}"
                    };

                return new ActionResult
                {
                    ActionId = "process.close",
                    Success = false,
                    Detail = $"No se pudo cerrar: {info.Name} ({pid})."
                };
            }
            catch (Exception ex)
            {
                return new ActionResult { ActionId = "process.close", Success = false, Detail = ex.Message };
            }
        }, ct);
    }

    public async Task<ActionResult> CloseByTargetAsync(string targetKey, bool forceIfNeeded, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            // E) Elevado: habilitar SeDebugPrivilege una sola vez por proceso — permite Kill()
            // sobre procesos de otra sesión/usuario cuando el token actual lo permite.
            if (ProcessPrivileges.IsElevated)
                ProcessPrivileges.EnableDebugAndPriorityPrivileges();

            var matched = 0;
            var closed = 0;
            var deniedProtected = 0;
            var deniedAccess = 0;
            var details = new List<string>();
            var debugTried = false;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!MatchesTarget(p, targetKey)) continue;
                    matched++;
                    var info = FromProcessQuick(p);
                    if (IsProtected(info))
                    {
                        deniedProtected++;
                        details.Add($"omitido protegido {info.Name}");
                        continue;
                    }

                    var ok = false;
                    var hasUi = false;
                    try { hasUi = p.MainWindowHandle != IntPtr.Zero; } catch { /* */ }

                    if (hasUi)
                    {
                        try { p.CloseMainWindow(); } catch { /* seguimos con Kill si corresponde */ }
                        ok = p.WaitForExit(3500);
                        // Solo matar apps con ventana si el usuario confirmó el plan (force)
                        if (!ok && forceIfNeeded)
                        {
                            ok = TryKillWithRetry(p, ref debugTried, out var denied1);
                            if (!ok && denied1) deniedAccess++;
                        }
                    }
                    else
                    {
                        // Segundo plano: siempre terminar (CloseMainWindow no aplica)
                        ok = TryKillWithRetry(p, ref debugTried, out var denied2);
                        if (!ok && denied2) deniedAccess++;
                    }

                    if (ok) closed++;
                    details.Add($"{info.Name}({p.Id}):{(ok ? "OK" : "pendiente")}");
                }
                catch (Exception ex)
                {
                    details.Add(ex.Message);
                }
                finally { p.Dispose(); }
            }

            // Todos los emparejados eran protegidos: esto es un Skip claro, no un Fail — el plan
            // nunca debió proponer cerrarlos (ver IsProtected), pero por si el target llegó de otra vía.
            var allProtected = matched > 0 && deniedProtected == matched;
            var status = matched == 0
                ? ActionApplyStatus.Skipped
                : closed > 0 ? ActionApplyStatus.Applied
                : allProtected ? ActionApplyStatus.Skipped
                : ActionApplyStatus.Failed;

            return new ActionResult
            {
                ActionId = "process.close",
                Success = matched == 0 || closed > 0 || allProtected,
                Status = status,
                Detail = matched == 0
                    ? $"Omitido: «{ExtractExeName(targetKey)}» ya no estaba en ejecución."
                    : allProtected
                        ? $"Omitido: «{ExtractExeName(targetKey)}» es un proceso protegido del sistema."
                        : closed > 0
                            ? $"Cerrados {closed}/{matched}. {string.Join("; ", details.Take(4))}"
                            : deniedAccess > 0
                                ? $"Acceso denegado ({deniedAccess}/{matched}). Requiere permisos adicionales o el proceso pertenece a otra sesión. {string.Join("; ", details.Take(4))}"
                                : $"No se pudo cerrar ({closed}/{matched}). {string.Join("; ", details.Take(4))}",
                RollbackToken = closed > 0 ? $"reopen:{targetKey}" : null
            };
        }, ct);
    }

    /// <summary>Intenta Kill(); si falla por Access Denied y estamos elevados, habilita
    /// SeDebugPrivilege y reintenta con Kill + TerminateProcess nativo.</summary>
    private static bool TryKillWithRetry(Process p, ref bool debugPrivilegeTried, out bool accessDenied)
    {
        accessDenied = false;
        try
        {
            p.Kill(entireProcessTree: false);
            return p.WaitForExit(3000);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            accessDenied = true;
            if (!ProcessPrivileges.IsElevated) return false;
            if (!debugPrivilegeTried)
            {
                debugPrivilegeTried = true;
                ProcessPrivileges.EnableDebugAndPriorityPrivileges(force: true);
            }

            try
            {
                p.Kill(entireProcessTree: false);
                accessDenied = false;
                return p.WaitForExit(3000);
            }
            catch (Exception ex2) when (IsAccessDenied(ex2))
            {
                if (ProcessPrivileges.TryTerminateNative(p.Id))
                {
                    accessDenied = false;
                    try { return p.WaitForExit(3000); } catch { return true; }
                }
                accessDenied = true;
                return false;
            }
            catch { return false; }
        }
        catch { return false; }
    }

    private static bool IsAccessDenied(Exception ex)
        => ex is UnauthorizedAccessException
           || ex is System.Security.SecurityException
           || ex is Win32Exception { NativeErrorCode: 5 }
           || ex.Message.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("acceso es denegado", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("acceso denegado", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("Attempted to perform", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("no autorizad", StringComparison.OrdinalIgnoreCase);

    public async Task<ActionResult> SetPriorityAsync(string targetKey, ProcessPriorityKind priority, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (ProcessPrivileges.IsElevated)
                ProcessPrivileges.EnableDebugAndPriorityPrivileges();

            var cls = priority switch
            {
                ProcessPriorityKind.BelowNormal => ProcessPriorityClass.BelowNormal,
                ProcessPriorityKind.AboveNormal => ProcessPriorityClass.AboveNormal,
                ProcessPriorityKind.High => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal
            };

            if (cls == ProcessPriorityClass.High)
                return new ActionResult { ActionId = "process.priority", Success = false, Detail = "High solo en casos controlados; omitido por seguridad." };

            var changed = 0;
            var matched = 0;
            var deniedProtected = 0;
            var deniedAccess = 0;
            var debugTried = false;
            string? prev = null;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!MatchesTarget(p, targetKey)) continue;
                    matched++;
                    var info = FromProcessQuick(p);
                    if (IsProtected(info)) { deniedProtected++; continue; }

                    prev ??= SafePriority(p);

                    // 1) Método alterno primero: OpenProcess(PROCESS_SET_INFORMATION) pide menos
                    //    acceso que Process.PriorityClass y a veces funciona donde el otro falla.
                    if (ProcessPrivileges.TrySetPriorityNative(p.Id, (int)cls))
                    {
                        changed++;
                        continue;
                    }

                    // 2) Reintento único: habilitar SeDebugPrivilege y reprobar nativo, luego API .NET.
                    if (!debugTried)
                    {
                        debugTried = true;
                        if (ProcessPrivileges.IsElevated) ProcessPrivileges.EnableDebugAndPriorityPrivileges();
                    }
                    if (ProcessPrivileges.TrySetPriorityNative(p.Id, (int)cls))
                    {
                        changed++;
                        continue;
                    }

                    try { p.PriorityClass = cls; changed++; }
                    catch (Exception ex) when (IsAccessDenied(ex)) { deniedAccess++; }
                    catch { deniedAccess++; }
                }
                catch { deniedAccess++; }
                finally { p.Dispose(); }
            }

            var allProtected = matched > 0 && deniedProtected == matched;
            var status = matched == 0
                ? ActionApplyStatus.Skipped
                : changed > 0 ? ActionApplyStatus.Applied
                : allProtected ? ActionApplyStatus.Skipped
                : ActionApplyStatus.Failed;

            return new ActionResult
            {
                ActionId = "process.priority_low",
                Success = matched == 0 || changed > 0 || allProtected,
                Status = status,
                Detail = matched == 0
                    ? $"Omitido: «{ExtractExeName(targetKey)}» ya no estaba en ejecución."
                    : allProtected
                        ? $"Omitido: «{ExtractExeName(targetKey)}» es un proceso protegido del sistema."
                        : changed > 0
                            ? $"Prioridad {cls} aplicada a {changed}/{matched} instancia(s) de «{ExtractExeName(targetKey)}»."
                              + (deniedProtected > 0 ? $" ({deniedProtected} protegidos omitidos)" : "")
                            : $"No se pudo cambiar prioridad (acceso denegado, {deniedAccess}/{matched}).",
                RollbackToken = prev is null ? null : $"prio:{targetKey}|{prev}"
            };
        }, ct);
    }

    public async Task<ActionResult> SuspendAsync(string targetKey, CancellationToken ct = default)
        => await SuspendOrResumeAsync(targetKey, suspend: true, ct);

    public async Task<ActionResult> ResumeAsync(string targetKey, CancellationToken ct = default)
        => await SuspendOrResumeAsync(targetKey, suspend: false, ct);

    public bool IsProtected(ProcessInfo info)
    {
        if (info.IsCritical || info.IsProtected) return true;
        if (CriticalNames.Contains(info.Name)) return true;
        if (info.Category is ProcessCategory.System or ProcessCategory.WindowsComponent or ProcessCategory.Security)
            return true;
        if (!string.IsNullOrEmpty(info.Path) &&
            info.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) &&
            info.Category != ProcessCategory.Updater)
            return true;
        if (info.Name.Equals("AetherPC", StringComparison.OrdinalIgnoreCase)) return true;
        if (SecurityTokens.Any(t =>
            info.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            (info.Company?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)))
            return true;

        // Señal autoritativa del SO (independiente de nombre/ruta/heurísticas): el mismo flag
        // que usa Task Manager para negarse a terminar un proceso crítico (csrss, wininit, smss…).
        if (info.Pid > 4 && ProcessPrivileges.IsBreakOnTerminationProcess(info.Pid))
            return true;

        return false;
    }

    private async Task<List<ProcessInfo>> SampleAsync(TimeSpan gap, CancellationToken ct)
    {
        // Primera pasada fuera del hilo UI (GetProcesses es síncrono y pesado)
        var first = await Task.Run(() =>
        {
            var map = new Dictionary<int, TimeSpan>(256);
            foreach (var p in Process.GetProcesses())
            {
                try { map[p.Id] = p.TotalProcessorTime; }
                catch { /* acceso denegado / proceso salió */ }
                finally { try { p.Dispose(); } catch { /* */ } }
            }
            return map;
        }, ct).ConfigureAwait(false);

        try { await Task.Delay(gap, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* seguir con ceros */ }

        // Segunda pasada también fuera del UI
        return await Task.Run(() => BuildSampleList(first, gap, ct), ct).ConfigureAwait(false);
    }

    private List<ProcessInfo> BuildSampleList(Dictionary<int, TimeSpan> first, TimeSpan gap, CancellationToken ct)
    {
        var list = new List<ProcessInfo>(first.Count);
        var cores = Math.Max(1, Environment.ProcessorCount);
        var elapsedMs = Math.Max(1, gap.TotalMilliseconds);
        var pathBudget = 80; // MainModule es carísimo; limitar lecturas de ruta/empresa

        foreach (var kv in first)
        {
            if (ct.IsCancellationRequested) break;
            Process? p = null;
            try
            {
                p = Process.GetProcessById(kv.Key);
            }
            catch
            {
                continue;
            }

            try
            {
                string name;
                try { name = p.ProcessName; }
                catch { continue; }

                TimeSpan cpuNow;
                try { cpuNow = p.TotalProcessorTime; }
                catch { cpuNow = kv.Value; }

                var cpuDelta = (cpuNow - kv.Value).TotalMilliseconds;
                var cpuPct = Math.Clamp(cpuDelta / (elapsedMs * cores) * 100.0, 0, 100);

                string? path = null, company = null, desc = null;
                if (pathBudget > 0 && !CriticalNames.Contains(name))
                {
                    try
                    {
                        path = p.MainModule?.FileName;
                        var vi = p.MainModule?.FileVersionInfo;
                        company = vi?.CompanyName;
                        desc = vi?.FileDescription;
                        pathBudget--;
                    }
                    catch { /* denegado — normal en muchos procesos */ }
                }

                long privateBytes = 0;
                try { privateBytes = p.PrivateMemorySize64; } catch { /* */ }

                DateTime? start = null;

                var category = Classify(name, path, company);
                var critical = CriticalNames.Contains(name) || category is ProcessCategory.System or ProcessCategory.Security;
                var hasWindow = false;
                var responding = true;
                try
                {
                    hasWindow = p.MainWindowHandle != IntPtr.Zero;
                    if (hasWindow)
                        responding = p.Responding;
                }
                catch { /* */ }

                list.Add(new ProcessInfo
                {
                    Pid = p.Id,
                    Name = name,
                    Path = path,
                    Description = desc,
                    Company = company,
                    UserName = null,
                    CpuPercent = cpuPct,
                    WorkingSetBytes = SafeWs(p),
                    PrivateBytes = privateBytes,
                    ThreadCount = 0,
                    HandleCount = 0,
                    Priority = SafePriority(p),
                    StartTime = start,
                    HasMainWindow = hasWindow,
                    Responding = responding,
                    IsCritical = critical,
                    IsProtected = critical || category is ProcessCategory.WindowsComponent or ProcessCategory.Security,
                    Category = category,
                    ConsumptionPattern = cpuPct >= 5 ? "Sostenido/activo" : "Reposo/bajo",
                    IsLikelyStartup = false
                });
            }
            catch
            {
                // skip proceso inestable
            }
            finally
            {
                try { p.Dispose(); } catch { /* */ }
            }
        }

        return list;
    }

    private async Task<ActionResult> SuspendOrResumeAsync(string targetKey, bool suspend, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            if (ProcessPrivileges.IsElevated)
                ProcessPrivileges.EnableDebugAndPriorityPrivileges();

            var n = 0;
            var matched = 0;
            var deniedProtected = 0;
            var deniedAccess = 0;
            var debugTried = false;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!MatchesTarget(p, targetKey)) continue;
                    matched++;
                    var info = FromProcessQuick(p);
                    if (IsProtected(info) || info.HasMainWindow)
                    {
                        deniedProtected++;
                        continue; // no suspender apps críticas/con UI visible desde masivo
                    }

                    // Handle explícito con solo PROCESS_SUSPEND_RESUME (menos privilegio que
                    // Process.Handle, que pide acceso amplio y puede fallar por Access Denied).
                    var handle = ProcessPrivileges.OpenSuspendResumeHandle(p.Id);
                    if (handle == IntPtr.Zero && !debugTried)
                    {
                        debugTried = true;
                        if (ProcessPrivileges.IsElevated) ProcessPrivileges.EnableDebugAndPriorityPrivileges();
                        handle = ProcessPrivileges.OpenSuspendResumeHandle(p.Id);
                    }
                    if (handle == IntPtr.Zero) { deniedAccess++; continue; }

                    try
                    {
                        var ok = suspend ? NtSuspendProcess(handle) == 0 : NtResumeProcess(handle) == 0;
                        if (ok) n++; else deniedAccess++;
                    }
                    finally { ProcessPrivileges.CloseNativeHandle(handle); }
                }
                catch { deniedAccess++; }
                finally { p.Dispose(); }
            }

            var allProtected = matched > 0 && deniedProtected == matched;
            return new ActionResult
            {
                ActionId = suspend ? "process.suspend" : "process.resume",
                Success = matched == 0 || n > 0 || allProtected,
                Status = matched == 0
                    ? ActionApplyStatus.Skipped
                    : n > 0 ? ActionApplyStatus.Applied
                    : allProtected ? ActionApplyStatus.Skipped
                    : ActionApplyStatus.Failed,
                Detail = matched == 0
                    ? $"Omitido: «{ExtractExeName(targetKey)}» ya no estaba en ejecución."
                    : allProtected
                        ? $"Omitido: «{ExtractExeName(targetKey)}» es protegido o tiene ventana visible."
                        : n > 0
                            ? $"{(suspend ? "Suspendidos" : "Reanudados")} {n}/{matched} proceso(s)."
                            : $"No se pudo {(suspend ? "suspender" : "reanudar")} (acceso denegado, {deniedAccess}/{matched}).",
                RollbackToken = suspend ? $"resume:{targetKey}" : null
            };
        }, ct);
    }

    private static ProcessInfo FromProcessQuick(Process p)
    {
        string? path = null, company = null;
        try
        {
            path = p.MainModule?.FileName;
            company = p.MainModule?.FileVersionInfo.CompanyName;
        }
        catch { /* */ }

        var name = p.ProcessName;
        var category = Classify(name, path, company);
        var critical = CriticalNames.Contains(name) || category is ProcessCategory.System or ProcessCategory.Security;
        var hasWindow = false;
        try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { /* */ }

        return new ProcessInfo
        {
            Pid = p.Id,
            Name = name,
            Path = path,
            Company = company,
            Category = category,
            IsCritical = critical,
            IsProtected = critical,
            HasMainWindow = hasWindow,
            WorkingSetBytes = SafeWs(p)
        };
    }

    private static ProcessCategory Classify(string name, string? path, string? company)
    {
        if (CriticalNames.Contains(name)) return ProcessCategory.System;
        if (SecurityTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                    (company?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)))
            return ProcessCategory.Security;

        if (TelemetryTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return ProcessCategory.Telemetry;

        if (UpdaterTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return ProcessCategory.Updater;

        if (LauncherTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return ProcessCategory.Launcher;

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(path) && path.StartsWith(win, StringComparison.OrdinalIgnoreCase))
            return ProcessCategory.WindowsComponent;

        if (string.IsNullOrEmpty(path)) return ProcessCategory.Unknown;

        if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Helper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Helper", StringComparison.OrdinalIgnoreCase))
            return ProcessCategory.Helper;

        return ProcessCategory.User;
    }

    private static bool MatchesTarget(Process p, string targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey)) return false;
        try
        {
            if (p.ProcessName.Equals(targetKey, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.ProcessName.Equals(ExtractExeName(targetKey), StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.Equals(targetKey, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* */ }
        }
        catch { /* */ }
        return false;
    }

    private static string ExtractExeName(string commandOrPath)
    {
        if (string.IsNullOrWhiteSpace(commandOrPath)) return "";
        var s = commandOrPath.Trim().Trim('"');
        try
        {
            var file = Path.GetFileNameWithoutExtension(s.Split(' ')[0].Trim('"'));
            return file ?? "";
        }
        catch { return ""; }
    }

    private static long SafeWs(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
    private static int SafeThreads(Process p) { try { return p.Threads.Count; } catch { return 0; } }
    private static int SafeHandles(Process p) { try { return p.HandleCount; } catch { return 0; } }
    private static string SafePriority(Process p) { try { return p.PriorityClass.ToString(); } catch { return "N/D"; } }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
