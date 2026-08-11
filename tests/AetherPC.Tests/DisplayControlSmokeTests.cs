using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using AetherPC.Infrastructure.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherPC.Tests;

public class DisplayControlSmokeTests
{
    [Fact]
    public async Task Enumerate_And_ProbeCapabilities_DoesNotThrow()
    {
        using var svc = new WindowsDisplayControlService(NullLogger<WindowsDisplayControlService>.Instance);
        var displays = await svc.EnumerateAsync();
        Assert.NotEmpty(displays);

        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        Assert.False(string.IsNullOrWhiteSpace(primary.Id));
        Assert.True(primary.Width > 0);
        Assert.True(primary.Height > 0);

        var caps = await svc.GetCapabilitiesAsync(primary.Id);
        Assert.Equal(primary.Id, caps.DisplayId);
        Assert.True(caps.SoftwareGamma);

        var modes = await svc.GetModesAsync(primary.Id);
        Assert.NotEmpty(modes);
        Assert.Contains(modes, m => m.IsCurrent);
    }

    [Fact]
    public async Task SoftColor_ApplyAndReset_IsReversible()
    {
        using var svc = new WindowsDisplayControlService(NullLogger<WindowsDisplayControlService>.Instance);
        var displays = await svc.EnumerateAsync();
        var id = (displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0]).Id;

        var warm = new SoftColorState
        {
            ColorTemperatureK = 4500,
            BlueLightReduction = 0.2,
            VisualBrightness = 0.9
        };
        var apply = await svc.ApplySoftColorAsync(id, warm);
        Assert.True(apply.Success, apply.ResolvedDetail);
        Assert.True(svc.HasSoftColorOverride(id));

        var reset = await svc.ResetSoftColorAsync(id);
        Assert.True(reset.Success, reset.ResolvedDetail);
        Assert.False(svc.HasSoftColorOverride(id));
    }

    [Fact]
    public async Task HardwareBrightness_Probe_ReportsCapabilityHonestly()
    {
        using var svc = new WindowsDisplayControlService(NullLogger<WindowsDisplayControlService>.Instance);
        var displays = await svc.EnumerateAsync();
        var id = (displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0]).Id;
        var caps = await svc.GetCapabilitiesAsync(id);

        // Laptop expected to have WMI; external may not. Either way must not invent.
        if (caps.HardwareBrightness)
        {
            Assert.Contains(caps.BrightnessSource, new[] { "Wmi", "Ddc" });
            Assert.NotNull(caps.BrightnessCurrent);
            var cur = caps.BrightnessCurrent!.Value;
            var target = Math.Clamp(cur, 0, 100);
            // Solo leer; no forzar cambio agresivo en CI — si hay HW, set al mismo valor
            var r = await svc.SetHardwareBrightnessAsync(id, target);
            Assert.True(r.Success, r.ResolvedDetail);
        }
        else
        {
            Assert.Equal("None", caps.BrightnessSource);
            Assert.False(string.IsNullOrWhiteSpace(caps.Notes));
        }
    }
}
