using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using Microsoft.Win32;

namespace AetherPC.Infrastructure.Windows;

public sealed class WindowsServiceEnumerator : IServiceEnumerator
{
    private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "Winmgmt", "EventLog", "RpcSs", "Dnscache", "Dhcp", "LanmanWorkstation", "LanmanServer",
        "ProfSvc", "Power", "Schedule", "Audiosrv", "AudioEndpointBuilder", "BFE", "mpssvc", "WinDefend",
        "SystemEventsBroker", "UserManager", "SamSs", "CryptSvc", "DcomLaunch", "LSM", "PlugPlay",
        "BrokerInfrastructure", "CoreMessagingRegistrar", "StateRepository", "gpsvc"
    };

    public async Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Lista + PID/descripción vía WMI (una pasada)
        var rows = await Task.Run(() => ReadServiceRows(ct), ct).ConfigureAwait(false);

        // Muestrear CPU/RAM solo de PIDs en ejecución (svchost compartido = mismo valor en varias filas)
        var pids = rows.Where(r => r.ProcessId > 0).Select(r => r.ProcessId).Distinct().ToArray();
        var metrics = await SamplePidMetricsAsync(pids, TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);

        var list = new List<ServiceInfo>(rows.Count);
        foreach (var r in rows)
        {
            metrics.TryGetValue(r.ProcessId, out var m);
            list.Add(new ServiceInfo
            {
                Name = r.Name,
                DisplayName = r.DisplayName,
                Status = r.Status,
                StartType = r.StartType,
                Description = r.Description,
                IsCritical = CriticalServices.Contains(r.Name),
                ProcessId = r.ProcessId,
                PathName = r.PathName,
                CpuPercent = m.Cpu,
                WorkingSetBytes = m.Ws
            });
        }

        return list
            .OrderByDescending(s => s.WorkingSetBytes)
            .ThenByDescending(s => s.CpuPercent)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ServiceRow(
        string Name, string DisplayName, string Status, string StartType,
        string? Description, int ProcessId, string? PathName);

    private static List<ServiceRow> ReadServiceRows(CancellationToken ct)
    {
        var list = new List<ServiceRow>(300);
        var prevUi = System.Globalization.CultureInfo.CurrentUICulture;
        ServiceDisplayCatalog.PushAppUiLanguage();
        try
        {
            if (!TryReadWmiRows(list, ct))
                ReadFromServiceController(list);

            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i];
                var (dn, desc) = ServiceDisplayCatalog.Apply(r.Name, r.DisplayName, r.Description);
                list[i] = r with { DisplayName = dn, Description = Truncate(desc, 240) };
            }
        }
        finally
        {
            try { System.Threading.Thread.CurrentThread.CurrentUICulture = prevUi; } catch { /* */ }
            ServiceDisplayCatalog.ClearPreferredUiLanguages();
        }

        return list;
    }

    private static bool TryReadWmiRows(List<ServiceRow> list, CancellationToken ct)
    {
        // Idioma de la APP, no el de Windows. Si el LCID no está instalado, se reintenta sin Locale.
        var locales = Loc.IsEnglish
            ? new[] { "MS_409", null }
            : new[] { "MS_C0A", "MS_0C0A", "MS_80A", null };

        foreach (var locale in locales)
        {
            try
            {
                var conn = new ConnectionOptions();
                if (!string.IsNullOrEmpty(locale))
                    conn.Locale = locale;
                var scope = new ManagementScope(@"\\.\root\cimv2", conn);
                scope.Connect();
                var query = new ObjectQuery(
                    "SELECT Name, DisplayName, State, StartMode, ProcessId, Description, PathName FROM Win32_Service");
                using var searcher = new ManagementObjectSearcher(scope, query);
                using var results = searcher.Get();
                foreach (ManagementBaseObject obj in results)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var mo = (ManagementObject)obj;
                        var name = mo["Name"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(name)) continue;
                        var pid = 0;
                        try { pid = Convert.ToInt32(mo["ProcessId"] ?? 0); } catch { /* */ }
                        list.Add(new ServiceRow(
                            name,
                            mo["DisplayName"]?.ToString() ?? name,
                            mo["State"]?.ToString() ?? "Unknown",
                            NormalizeStartMode(mo["StartMode"]?.ToString()),
                            Truncate(mo["Description"]?.ToString(), 240),
                            pid,
                            Truncate(mo["PathName"]?.ToString(), 260)
                        ));
                    }
                    catch { /* fila suelta */ }
                }

                return list.Count > 0;
            }
            catch
            {
                list.Clear();
            }
        }

        return false;
    }

    private static void ReadFromServiceController(List<ServiceRow> list)
    {
        foreach (var sc in ServiceController.GetServices())
        {
            try
            {
                list.Add(new ServiceRow(
                    sc.ServiceName,
                    sc.DisplayName,
                    sc.Status.ToString(),
                    SafeStartType(sc),
                    null,
                    0,
                    null));
            }
            finally { sc.Dispose(); }
        }
    }

    private static async Task<Dictionary<int, (double Cpu, long Ws)>> SamplePidMetricsAsync(
        IReadOnlyList<int> pids, TimeSpan gap, CancellationToken ct)
    {
        var map = new Dictionary<int, (double Cpu, long Ws)>();
        if (pids.Count == 0) return map;

        var firstCpu = new Dictionary<int, TimeSpan>(pids.Count);
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                firstCpu[pid] = p.TotalProcessorTime;
            }
            catch { /* proceso salió */ }
        }

        try { await Task.Delay(gap, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* */ }

        var cores = Math.Max(1, Environment.ProcessorCount);
        var elapsedMs = Math.Max(1, gap.TotalMilliseconds);

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { /* */ }

                double cpu = 0;
                if (firstCpu.TryGetValue(pid, out var t0))
                {
                    try
                    {
                        var delta = (p.TotalProcessorTime - t0).TotalMilliseconds;
                        cpu = Math.Clamp(delta / (elapsedMs * cores) * 100.0, 0, 100);
                    }
                    catch { /* */ }
                }

                map[pid] = (cpu, ws);
            }
            catch
            {
                map[pid] = (0, 0);
            }
        }

        return map;
    }

    private static string NormalizeStartMode(string? mode) => mode switch
    {
        "Auto" => "Automatic",
        "Manual" => "Manual",
        "Disabled" => "Disabled",
        "Boot" => "Boot",
        "System" => "System",
        null or "" => "Unknown",
        _ => mode
    };

    private static string SafeStartType(ServiceController sc)
    {
        try { return sc.StartType.ToString(); }
        catch { return "Unknown"; }
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public sealed class WindowsStartupService : IStartupService
{
    private static readonly string[] NeverDisableTokens =
    {
        "SecurityHealth", "Windows Defender", "RtkAud", "Realtek", "NVIDIA Display", "AMD External",
        "igfx", "HotKey", "cFos", "AetherPC", "OneDriveSetup"
    };

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var items = new List<StartupItem>();
        ReadRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU\\Run", items);
        ReadRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM\\Run", items);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup\\User", items);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup\\Common", items);
        return Task.FromResult<IReadOnlyList<StartupItem>>(items);
    }

    public Task<ActionResult> DisableRunEntryAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new ActionResult { ActionId = "process.disable_startup", Success = false, Detail = "Nombre vacío." });

        if (NeverDisableTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(new ActionResult
            {
                ActionId = "process.disable_startup",
                Success = false,
                Detail = $"Protegido: no se desactiva «{name}»."
            });

        // 1) HKCU Run — match por nombre de valor, exe o ruta
        var hkcu = TryDisableInRunKey(Registry.CurrentUser, name);
        if (hkcu is not null) return Task.FromResult(hkcu);

        // 2) Carpeta Startup del usuario (.lnk)
        var folder = TryDisableStartupShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Startup), name);
        if (folder is not null) return Task.FromResult(folder);

        // 3) HKLM solo si estamos elevados
        try
        {
            var hklm = TryDisableInRunKey(Registry.LocalMachine, name);
            if (hklm is not null) return Task.FromResult(hklm);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new ActionResult
            {
                ActionId = "process.disable_startup",
                Success = false,
                Detail = $"«{name}» parece estar en HKLM\\Run. Ejecuta AetherPC como administrador."
            });
        }

        return Task.FromResult(new ActionResult
        {
            ActionId = "process.disable_startup",
            Success = false,
            Detail = $"No se encontró «{name}» en HKCU\\Run ni en la carpeta Inicio. Puede ser una tarea programada o UWP (Administrador de tareas → Inicio)."
        });
    }

    private static ActionResult? TryDisableInRunKey(RegistryKey root, string nameOrPath)
    {
        try
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return null;

            var needle = nameOrPath.Trim().Trim('"');
            var exeNeedle = Path.GetFileNameWithoutExtension(needle.Split(' ')[0].Trim('"'));

            foreach (var n in key.GetValueNames())
            {
                var cmd = key.GetValue(n)?.ToString() ?? "";
                var cmdExe = "";
                try
                {
                    var first = cmd.Trim().Trim('"').Split(' ')[0].Trim('"');
                    cmdExe = Path.GetFileNameWithoutExtension(first);
                }
                catch { /* */ }

                var match =
                    n.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
                    n.Equals(exeNeedle, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(exeNeedle) && cmd.Contains(exeNeedle, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(exeNeedle) && cmdExe.Equals(exeNeedle, StringComparison.OrdinalIgnoreCase)) ||
                    cmd.Contains(needle, StringComparison.OrdinalIgnoreCase);

                if (!match) continue;

                key.DeleteValue(n, throwOnMissingValue: false);
                var hive = root.Name.Contains("CurrentUser", StringComparison.OrdinalIgnoreCase) ? "HKCU" : "HKLM";
                return new ActionResult
                {
                    ActionId = "process.disable_startup",
                    Success = true,
                    Detail = $"Eliminado del inicio ({hive}\\Run): {n}.",
                    RollbackToken = $"startup:{n}|{cmd}"
                };
            }
        }
        catch (UnauthorizedAccessException) { throw; }
        catch { /* */ }
        return null;
    }

    private static ActionResult? TryDisableStartupShortcut(string folder, string nameOrPath)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var needle = Path.GetFileNameWithoutExtension(nameOrPath.Trim().Trim('"'));
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                if (!baseName.Equals(needle, StringComparison.OrdinalIgnoreCase) &&
                    !baseName.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
                    !file.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;

                var bak = file + ".aetherbak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(file, bak);
                return new ActionResult
                {
                    ActionId = "process.disable_startup",
                    Success = true,
                    Detail = $"Acceso directo de inicio desactivado: {Path.GetFileName(file)} (renombrado .aetherbak).",
                    RollbackToken = $"startuplnk:{bak}"
                };
            }
        }
        catch { /* */ }
        return null;
    }

    private static void ReadRunKey(RegistryKey root, string path, string location, List<StartupItem> items)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var cmd = key.GetValue(name)?.ToString() ?? string.Empty;
                items.Add(new StartupItem
                {
                    Name = name,
                    Command = cmd,
                    Location = location,
                    Impact = "Unknown",
                    Enabled = true
                });
            }
        }
        catch { /* access */ }
    }

    private static void ReadStartupFolder(string folder, string location, List<StartupItem> items)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Location = location,
                    Impact = "Unknown",
                    Enabled = true
                });
            }
        }
        catch { /* */ }
    }
}

public sealed class WindowsDriverService : IDriverService
{
    public Task<IReadOnlyList<DriverInfo>> GetDriversAsync(CancellationToken ct = default)
        => Task.Run(() => QueryDrivers(ct), ct);

    private static IReadOnlyList<DriverInfo> QueryDrivers(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var list = new List<DriverInfo>();
        try
        {
            // Consulta más ligera: solo campos usados + filtro de nombre vacío en cliente
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, Status, InfName FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");
            searcher.Options.Timeout = TimeSpan.FromSeconds(12);
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                var device = obj["DeviceName"]?.ToString();
                if (string.IsNullOrWhiteSpace(device)) continue;
                // Omitir entradas genéricas / duplicados de bus sin driver útil
                if (device.StartsWith("Root", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(obj["DriverVersion"]?.ToString()))
                    continue;

                DateTime? date = null;
                try
                {
                    if (obj["DriverDate"] is not null)
                        date = System.Management.ManagementDateTimeConverter.ToDateTime(obj["DriverDate"].ToString()!);
                }
                catch { /* ignore */ }

                var rawStatus = obj["Status"]?.ToString() ?? "OK";
                list.Add(new DriverInfo
                {
                    DeviceName = device,
                    Manufacturer = obj["Manufacturer"]?.ToString(),
                    DriverVersion = obj["DriverVersion"]?.ToString(),
                    DriverDate = date,
                    Status = rawStatus,
                    Category = Categorize(device),
                    HealthLabel = ClassifyHealth(rawStatus, date)
                });
            }
        }
        catch
        {
            // limited without elevation
        }

        // Priorizar GPU/Audio/Red y limitar lista
        return list
            .OrderBy(d => d.NeedsAttention ? 0 : 1)
            .ThenBy(d => CategoryRank(d.Category))
            .ThenBy(d => d.DeviceName)
            .Take(250)
            .ToList();
    }

    private static string ClassifyHealth(string status, DateTime? date)
    {
        if (string.IsNullOrWhiteSpace(status))
            status = "OK";
        if (status.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Degraded", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Pred Fail", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Unknown", StringComparison.OrdinalIgnoreCase) && status.Length > 10)
            return "Attention";
        if (date is { } d && d < DateTime.Now.AddYears(-6))
            return "Old";
        if (status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            return "OK";
        return "Unknown";
    }

    private static int CategoryRank(string cat) => cat switch
    {
        "GPU" => 0,
        "Network" => 1,
        "Audio" => 2,
        "Storage" => 3,
        "USB" => 4,
        _ => 5
    };

    private static string Categorize(string device)
    {
        if (device.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            device.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            device.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
            device.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
            device.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            device.Contains("Intel Arc", StringComparison.OrdinalIgnoreCase))
            return "GPU";
        if (device.Contains("Audio", StringComparison.OrdinalIgnoreCase) || device.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
            return "Audio";
        if (device.Contains("Network", StringComparison.OrdinalIgnoreCase) || device.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || device.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || device.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
            return "Network";
        if (device.Contains("USB", StringComparison.OrdinalIgnoreCase))
            return "USB";
        if (device.Contains("Disk", StringComparison.OrdinalIgnoreCase) || device.Contains("Storage", StringComparison.OrdinalIgnoreCase) || device.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return "Storage";
        return "Other";
    }
}

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    public bool IsElevated => AetherPC.Core.Windows.ProcessPrivileges.IsElevated;

    public Task<bool> EnsureElevatedHintAsync(string reason) => Task.FromResult(IsElevated);
}

public sealed class WindowsRestorePointService : IRestorePointService
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// Intento corto y no bloqueante. Si falla o tarda, el plan de optimización continúa igual.
    /// Nunca debe colgar la UI (antes WaitForExit 120s + redirect podía bloquear).
    /// </summary>
    public async Task<(bool Success, string Message)> CreateAsync(string description, CancellationToken ct = default)
    {
        try
        {
            if (!new WindowsPrivilegeService().IsElevated)
                return (false, "Sin admin: se omite el punto de restauración y se continúa con el plan.");

            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments =
                            "-NoProfile -ExecutionPolicy Bypass -Command " +
                            "\"try { Checkpoint-Computer -Description '" + Escape(description) +
                            "' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; exit 0 } " +
                            "catch { Write-Output $_.Exception.Message; exit 1 }\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p is null)
                        return (false, "No se pudo iniciar PowerShell para el punto de restauración.");

                    // Lectura async evita deadlock del buffer; timeout corto para no congelar la app
                    var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
                    var stderrTask = p.StandardError.ReadToEndAsync(ct);
                    var exited = p.WaitForExit(25_000);
                    if (!exited)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                        return (false, "Punto de restauración omitido (tardó demasiado). Se continúa aplicando el plan.");
                    }

                    var err = stderrTask.GetAwaiter().GetResult();
                    var outp = stdoutTask.GetAwaiter().GetResult();
                    if (p.ExitCode == 0)
                        return (true, "Punto de restauración creado.");

                    var msg = string.IsNullOrWhiteSpace(err) ? outp : err;
                    return (false, string.IsNullOrWhiteSpace(msg)
                        ? "No se pudo crear el punto (política/frecuencia). Se continúa con el plan."
                        : msg.Trim());
                }
                catch (Exception ex)
                {
                    return (false, "Restauración omitida: " + ex.Message);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "Restauración omitida: " + ex.Message);
        }
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
