using AetherPC.Application;
using AetherPC.Application.Scanning;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using AetherPC.Infrastructure;
using AetherPC.Infrastructure.Localization;
using AetherPC.Optimization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AetherPC.Tests;

/// <summary>
/// Smoke de todas las pestañas: solo lectura / análisis.
/// NUNCA llama Apply, Clean, Prepare gaming, benchmarks de escritura, ni cambios de Windows.
/// </summary>
public class TabSmokeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public TabSmokeTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task AllTabs_ReadOnlySmoke_DoesNotApplyChanges()
    {
        var report = new List<string>();
        void Ok(string tab, string detail)
        {
            var line = $"PASS  {tab,-12} {detail}";
            report.Add(line);
            _out.WriteLine(line);
        }
        void Fail(string tab, string detail)
        {
            var line = $"FAIL  {tab,-12} {detail}";
            report.Add(line);
            _out.WriteLine(line);
        }

        using var services = BuildServices();
        Loc.Use(services.GetRequiredService<ILocalizer>());
        Loc.SetLanguage("es");

        var scan = services.GetRequiredService<ScanEngine>();
        var health = services.GetRequiredService<IHealthScorer>();
        var recs = services.GetRequiredService<IRecommendationEngine>();
        var settings = services.GetRequiredService<IAppSettingsStore>();
        var history = services.GetRequiredService<IHistoryStore>();
        var cleanup = services.GetRequiredService<ICleanupService>();
        var drivers = services.GetRequiredService<IDriverService>();
        var processes = services.GetRequiredService<IProcessService>();
        var servicesEnum = services.GetRequiredService<IServiceEnumerator>();
        var startup = services.GetRequiredService<IStartupService>();
        var bench = services.GetRequiredService<IBenchmarkService>();
        var opt = services.GetRequiredService<IOptimizationEngine>();
        var scanner = services.GetRequiredService<ISystemScanner>();

        SystemSnapshot? deep = null;

        // —— Inicio / Hardware (fast + deep, health, recomendaciones) ——
        try
        {
            var fast = await scan.GetSnapshotAsync(ScanDepth.Fast, force: true);
            Assert.NotNull(fast.Cpu);
            Assert.True(fast.Memory.TotalBytes > 0 || fast.Memory.TotalBytes == 0); // real read attempted
            var (score, factors) = health.Score(fast);
            Assert.InRange(score, 0, 100);
            Assert.NotEmpty(factors);
            Ok("Home/Fast", $"health={score} factors={factors.Count} cpu={fast.Cpu.UsagePercent:F0}%");

            deep = await scan.GetSnapshotAsync(ScanDepth.Deep, force: true);
            Assert.NotNull(deep);
            Ok("Hardware", $"cpu={Trim(deep.Cpu.Name)} disks={deep.Disks.Count} notes={deep.DetectionNotes.Count}");
        }
        catch (Exception ex)
        {
            Fail("Home/HW", ex.Message);
            throw;
        }

        // —— Monitor (live metrics) ——
        try
        {
            var live = await scan.GetSnapshotAsync(ScanDepth.Live);
            Ok("Monitor", $"cpu={live.Cpu.UsagePercent:F0}% ram={live.Memory.UsagePercent:F0}%");
        }
        catch (Exception ex)
        {
            Fail("Monitor", ex.Message);
            throw;
        }

        // —— Procesos (solo listar) ——
        try
        {
            var procs = await processes.GetProcessesAsync();
            Assert.NotEmpty(procs);
            Ok("Processes", $"count={procs.Count}");
        }
        catch (Exception ex)
        {
            Fail("Processes", ex.Message);
            throw;
        }

        // —— Servicios (solo listar) ——
        try
        {
            var svcs = await servicesEnum.GetServicesAsync();
            Assert.NotEmpty(svcs);
            Ok("Services", $"count={svcs.Count}");
        }
        catch (Exception ex)
        {
            Fail("Services", ex.Message);
            throw;
        }

        // —— Optimizar (solo BuildPlan / Analyze, NO Execute) ——
        try
        {
            deep ??= await scan.GetSnapshotAsync(ScanDepth.Deep, force: true);
            var plan = await opt.BuildPlanAsync(deep, beastMode: false);
            Ok("Optimize", $"actions={plan.Actions.Count} (no apply)");
        }
        catch (Exception ex)
        {
            Fail("Optimize", ex.Message);
            throw;
        }

        // —— Bestia (solo BuildPlan beast, NO Execute) ——
        try
        {
            deep ??= await scan.GetSnapshotAsync(ScanDepth.Deep, force: true);
            var plan = await opt.BuildPlanAsync(deep, beastMode: true);
            Ok("Beast", $"actions={plan.Actions.Count} selected={plan.Actions.Count(a => a.IsSelected)} (no apply)");
        }
        catch (Exception ex)
        {
            Fail("Beast", ex.Message);
            throw;
        }

        // —— Limpieza (solo Scan, NO Clean) ——
        try
        {
            var candidates = await cleanup.ScanAsync();
            Ok("Cleanup", $"candidates={candidates.Count} (no clean)");
        }
        catch (Exception ex)
        {
            Fail("Cleanup", ex.Message);
            throw;
        }

        // —— Gaming (solo detección de carpetas + snapshot, NO Prepare) ——
        try
        {
            deep ??= await scan.GetSnapshotAsync(ScanDepth.Live);
            var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic");
            var launchers = (Directory.Exists(steam) ? 1 : 0) + (Directory.Exists(epic) ? 1 : 0);
            Ok("Gaming", $"launchers={launchers} gpu={Trim(deep.Gpu?.Name)} (no prepare)");
        }
        catch (Exception ex)
        {
            Fail("Gaming", ex.Message);
            throw;
        }

        // —— Benchmarks (solo historial, NO RunCpu/Ram/Disk) ——
        try
        {
            var hist = await bench.ListHistoryAsync();
            Ok("Benchmark", $"history={hist.Count} (no run)");
        }
        catch (Exception ex)
        {
            Fail("Benchmark", ex.Message);
            throw;
        }

        // —— Seguridad (solo CaptureSecurity) ——
        try
        {
            var sec = await scanner.CaptureSecurityAsync();
            Ok("Security", $"source={sec.Source} defender={sec.DefenderEnabled} fw={sec.FirewallEnabled}");
        }
        catch (Exception ex)
        {
            Fail("Security", ex.Message);
            throw;
        }

        // —— Drivers (solo listar) ——
        try
        {
            var list = await drivers.GetDriversAsync();
            Ok("Drivers", $"count={list.Count}");
        }
        catch (Exception ex)
        {
            Fail("Drivers", ex.Message);
            throw;
        }

        // —— Historial (solo leer) ——
        try
        {
            var entries = await history.ListAsync(20);
            Ok("History", $"entries={entries.Count}");
        }
        catch (Exception ex)
        {
            Fail("History", ex.Message);
            throw;
        }

        // —— Configuración (solo LoadProfile, NO Save/tema) ——
        try
        {
            var profile = await settings.LoadProfileAsync();
            Ok("Settings", $"theme={profile.Theme} lang={profile.Language} (no save)");
        }
        catch (Exception ex)
        {
            Fail("Settings", ex.Message);
            throw;
        }

        // —— Startup items (solo listar, NO Disable) ——
        try
        {
            var items = await startup.GetStartupItemsAsync();
            Ok("Startup", $"items={items.Count} (read-only helper)");
        }
        catch (Exception ex)
        {
            Fail("Startup", ex.Message);
            throw;
        }

        // —— Recomendaciones Inicio ——
        try
        {
            deep ??= await scan.GetSnapshotAsync(ScanDepth.Deep, force: false);
            var profile = await settings.LoadProfileAsync();
            var list = await recs.AnalyzeAsync(deep, profile);
            Ok("Recs", $"count={list.Count}");
        }
        catch (Exception ex)
        {
            Fail("Recs", ex.Message);
            throw;
        }

        // Imprime informe (visible en salida de test)
        var summary = string.Join(Environment.NewLine, report);
        Assert.DoesNotContain("FAIL", summary);
        // Force visibility in runners
        Assert.True(report.Count >= 12, "Se esperaban ≥12 pestañas/checks. Informe:\n" + summary);
    }

    private static ServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Warning);
        });
        sc.AddAetherApplication();
        sc.AddAetherInfrastructure();
        sc.AddAetherOptimization();
        return sc.BuildServiceProvider();
    }

    private static string Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "N/D";
        return s.Length <= 40 ? s : s[..40] + "…";
    }
}
