using System.Diagnostics;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using AetherPC.Infrastructure.Sensors;
using AetherPC.Infrastructure.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherPC.Tests;

public class ScannerPerformanceTests
{
    [Fact]
    public async Task FastScan_IsFasterThanDeep_AndDetectsBasics()
    {
        var sensors = new LibreHardwareSensorService(NullLogger<LibreHardwareSensorService>.Instance);
        var scanner = new WindowsSystemScanner(sensors, NullLogger<WindowsSystemScanner>.Instance);

        var sw = Stopwatch.StartNew();
        var fast = await scanner.CaptureSnapshotAsync(ScanDepth.Fast);
        var fastMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var deep = await scanner.CaptureSnapshotAsync(ScanDepth.Deep);
        var deepMs = sw.Elapsed.TotalMilliseconds;

        Assert.NotEqual(NotDetected.Text, fast.Cpu.Name);
        Assert.True(fast.Memory.TotalBytes > 0);
        Assert.True(fastMs < 8_000, $"Fast scan: {fastMs:F0} ms");
        Assert.True(deepMs < 25_000, $"Deep scan: {deepMs:F0} ms");

        // Inventario deep añade capas (placa/bios/monitores pueden o no existir según HW/permisos)
        Assert.NotNull(deep.Motherboard);
        Assert.NotNull(deep.Bios);
        Assert.NotNull(deep.StageTimingsMs);

        sensors.Dispose();
    }
}
