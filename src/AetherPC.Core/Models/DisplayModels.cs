namespace AetherPC.Core.Models;

/// <summary>Dispositivo de pantalla enumerado por APIs de Windows (no inventado).</summary>
public sealed class DisplayDeviceInfo
{
    public string Id { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; init; } = "N/A";
    public string? Manufacturer { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsInternal { get; init; }
    public bool IsActive { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshHz { get; init; }
    public int BitsPerPixel { get; init; }
    public int OrientationDegrees { get; init; }
    public double ScalePercent { get; init; } = 100;
    public string? AdapterName { get; init; }
    public string? ConnectionHint { get; init; }
    public bool? HdrSupported { get; init; }
    public bool? HdrEnabled { get; init; }
    public string? IccProfileName { get; init; }
    public IntPtr HMonitor { get; init; }
    public string Source { get; init; } = "";
}

public sealed class DisplayCapabilities
{
    public string DisplayId { get; set; } = "";
    public bool HardwareBrightness { get; set; }
    public string BrightnessSource { get; set; } = "None"; // Wmi | Ddc | None
    public int? BrightnessMin { get; set; }
    public int? BrightnessMax { get; set; }
    public int? BrightnessCurrent { get; set; }
    public bool SoftwareGamma { get; set; }
    public bool SoftwareAttenuation { get; set; }
    public bool ColorTemperatureFilter { get; set; }
    public bool RgbBalance { get; set; }
    public bool ContrastGamma { get; set; }
    public bool CanChangeDisplayMode { get; set; }
    public bool HdrReported { get; set; }
    public bool DdcCi { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class DisplayModeInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshHz { get; init; }
    public int BitsPerPixel { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsRecommended { get; init; }
    public string Label => $"{Width}×{Height} @ {RefreshHz} Hz";
}

/// <summary>Ajustes de color por software (gamma ramp). No es temperatura física del panel.</summary>
public sealed class SoftColorState
{
    public double VisualBrightness { get; set; } = 1.0;   // 0.5–1.0 (floor raised to avoid near-black screens)
    public double SoftwareAttenuation { get; set; } = 0; // 0–0.40 extra dim
    public double Contrast { get; set; } = 1.0;            // 0.6–1.4
    public double Gamma { get; set; } = 1.0;               // 0.85–1.4
    public double Saturation { get; set; } = 1.0;          // 0.6–1.4 (approx)
    public double RedGain { get; set; } = 1.0;             // 0.70–1.45
    public double GreenGain { get; set; } = 1.0;
    public double BlueGain { get; set; } = 1.0;
    public int ColorTemperatureK { get; set; } = 6500;    // 4000–7500
    public double BlueLightReduction { get; set; } = 0;   // 0–0.45
    public bool NightMode { get; set; }

    public SoftColorState Clone() => (SoftColorState)MemberwiseClone();

    public static SoftColorState Defaults => new();
}

public sealed class VisualDisplayProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NameKey { get; set; } = "Monitor.Profile.Custom";
    public string? CustomName { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsDefault { get; set; }
    public int? HardwareBrightness { get; set; }
    public SoftColorState Soft { get; set; } = SoftColorState.Defaults;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(CustomName)
            ? AetherPC.Core.Localization.Loc.T(NameKey)
            : CustomName;
}

public sealed class DisplayAutomationSettings
{
    public bool Enabled { get; set; }
    public bool NightBySchedule { get; set; }
    public int NightStartHour { get; set; } = 21;
    public int NightEndHour { get; set; } = 7;
    public string? NightProfileId { get; set; }
    public string? DayProfileId { get; set; }
    public bool GamingOnFullscreen { get; set; }
    public string? GamingProfileId { get; set; }
}

public sealed class PendingDisplayModeChange
{
    public string DisplayId { get; init; } = "";
    public DisplayModeInfo Target { get; init; } = new();
    public DisplayModeInfo Previous { get; init; } = new();
    public DateTimeOffset ExpiresAt { get; init; }
}
