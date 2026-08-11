using AetherPC.Core.Models;
using AetherPC.Infrastructure.Localization;
using AetherPC.Core.Localization;

namespace AetherPC.Tests;

public class LocalizationHardwareKeysTests
{
    [Fact]
    public void HardwareAndCommonKeys_ExistInEsAndEn()
    {
        var loc = new CatalogLocalizer();
        Loc.Use(loc);

        string[] keys =
        [
            "Hardware.Cpu", "Hardware.Gpu", "Hardware.Ram", "Hardware.Disk",
            "Hardware.Bios", "Hardware.Monitors", "Hardware.Adapters", "Hardware.Notes",
            "Hardware.ReadyHint", "Hardware.ScanningFast", "Hardware.ScanningDeep",
            "Hardware.ReadyMs", "Hardware.Error", "Page.Hardware", "Common.Refresh",
            "Security.Refresh", "Settings.Blurb", "Beast.HideLog"
        ];

        Loc.SetLanguage("es");
        foreach (var k in keys)
        {
            var v = Loc.T(k);
            Assert.False(string.Equals(v, k, StringComparison.Ordinal), $"Missing ES key: {k}");
        }

        Loc.SetLanguage("en");
        foreach (var k in keys)
        {
            var v = Loc.T(k);
            Assert.False(string.Equals(v, k, StringComparison.Ordinal), $"Missing EN key: {k}");
        }
    }

    [Fact]
    public void SecurityInfo_Defaults_DoNotThrow()
    {
        var s = new SecurityInfo();
        Assert.Null(s.DefenderEnabled);
        Assert.NotNull(s.Source);
    }
}
