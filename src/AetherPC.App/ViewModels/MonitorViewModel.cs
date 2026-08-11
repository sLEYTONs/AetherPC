using System.Collections.ObjectModel;
using System.Windows.Threading;
using AetherPC.App.Services;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherPC.App.ViewModels;

public partial class MonitorViewModel : ObservableObject
{
    private readonly IDisplayControlService _display;
    private readonly IAppSettingsStore _settings;
    private readonly DispatcherTimer _previewTimer;
    private SoftColorState? _undoSoft;
    private int? _undoBrightness;
    private bool _suppressSoftApply;
    private bool _suppressBrightness;
    private bool _displayLoaded;

    public ObservableCollection<DisplayDeviceInfo> Displays { get; } = new();
    public ObservableCollection<DisplayModeInfo> Modes { get; } = new();

    public SoftColorState Soft { get; } = SoftColorState.Defaults;

    [ObservableProperty] private string _section = "screen"; // screen | color | info
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private DisplayDeviceInfo? _selectedDisplay;
    [ObservableProperty] private DisplayCapabilities? _capabilities;
    [ObservableProperty] private int _hardwareBrightness = 50;
    [ObservableProperty] private bool _hardwareBrightnessAvailable;
    public bool HardwareBrightnessUnavailable => !HardwareBrightnessAvailable;
    [ObservableProperty] private string _brightnessHint = "";
    [ObservableProperty] private bool _advancedColorOpen;
    [ObservableProperty] private DisplayModeInfo? _selectedMode;
    [ObservableProperty] private bool _modePreviewActive;
    [ObservableProperty] private int _modePreviewSeconds;
    [ObservableProperty] private bool _nightMode;
    [ObservableProperty] private bool _applyToAllCompatible;
    [ObservableProperty] private string _calibrationNote = "";

    // Soft bindings (percent UI)
    [ObservableProperty] private double _softBrightnessPct = 100;
    [ObservableProperty] private double _softAttenuationPct;
    [ObservableProperty] private double _softContrastPct = 100;
    [ObservableProperty] private double _softGamma = 1.0;
    [ObservableProperty] private double _softSaturationPct = 100;
    [ObservableProperty] private double _softRedPct = 100;
    [ObservableProperty] private double _softGreenPct = 100;
    [ObservableProperty] private double _softBluePct = 100;
    [ObservableProperty] private int _colorTempK = 6500;
    [ObservableProperty] private double _blueLightPct;

    public bool IsScreen => Section == "screen";
    public bool IsColor => Section == "color";
    public bool IsInfo => Section == "info";

    public MonitorViewModel(IDisplayControlService display, IAppSettingsStore settings)
    {
        _display = display;
        _settings = settings;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _previewTimer.Tick += async (_, _) => await PreviewTickAsync();

        Status = UiLoc.Instance.T("Monitor.Status.Ready");
        UiLoc.Instance.PropertyChanged += (_, _) => RefreshLocalizedHints();
        _ = InitDisplayAsync();
    }

    private void RefreshLocalizedHints()
    {
        if (Capabilities is not null)
        {
            BrightnessHint = HardwareBrightnessAvailable
                ? UiLoc.Instance.T("Monitor.Brightness.Source", Capabilities.BrightnessSource)
                : UiLoc.Instance.T("Monitor.Brightness.Unsupported");
        }
        CalibrationNote = UiLoc.Instance.T("Monitor.SoftColor.Disclaimer");
        if (string.IsNullOrWhiteSpace(Status) || Status == "…" ||
            Status.Contains("listo", StringComparison.OrdinalIgnoreCase) ||
            Status.Contains("ready", StringComparison.OrdinalIgnoreCase))
            Status = UiLoc.Instance.T("Monitor.Status.Ready");
    }

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsScreen));
        OnPropertyChanged(nameof(IsColor));
        OnPropertyChanged(nameof(IsInfo));
    }

    public void StopLive()
    {
        try { _previewTimer.Stop(); } catch { /* */ }
        try { _display.RestoreAllSoftColor(); } catch { /* */ }
    }

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section))
            Section = section;
    }

    [RelayCommand]
    private async Task RefreshDisplayAsync() => await LoadDisplayAsync(force: true);

    [RelayCommand]
    private async Task SelectDisplayAsync(DisplayDeviceInfo? d)
    {
        if (d is null) return;
        SelectedDisplay = d;
        await LoadCapsAndModesAsync();
        await LoadSoftFromStoreAsync();
    }

    [RelayCommand]
    private async Task ApplyHardwareBrightnessAsync()
    {
        if (SelectedDisplay is null || !HardwareBrightnessAvailable) return;
        _undoBrightness = Capabilities?.BrightnessCurrent;
        var r = await _display.SetHardwareBrightnessAsync(SelectedDisplay.Id, HardwareBrightness);
        Status = r.ResolvedDetail;
        if (Capabilities is not null)
            Capabilities = await _display.GetCapabilitiesAsync(SelectedDisplay.Id);
    }

    [RelayCommand]
    private async Task ApplySoftAsync()
    {
        if (SelectedDisplay is null || _suppressSoftApply) return;
        PushSoftFromUi();
        _undoSoft ??= _display.GetLastSoftColor(SelectedDisplay.Id)?.Clone() ?? SoftColorState.Defaults;
        var r = await _display.ApplySoftColorAsync(SelectedDisplay.Id, Soft);
        Status = r.ResolvedDetail;
        await PersistSoftAsync();
    }

    [RelayCommand]
    private async Task ResetSoftAsync()
    {
        if (SelectedDisplay is null) return;
        var r = await _display.ResetSoftColorAsync(SelectedDisplay.Id);
        Soft.VisualBrightness = 1; Soft.SoftwareAttenuation = 0; Soft.Contrast = 1;
        Soft.Gamma = 1; Soft.Saturation = 1; Soft.RedGain = Soft.GreenGain = Soft.BlueGain = 1;
        Soft.ColorTemperatureK = 6500; Soft.BlueLightReduction = 0; Soft.NightMode = false;
        SyncUiFromSoft();
        NightMode = false;
        Status = r.ResolvedDetail;
        await PersistSoftAsync();
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (SelectedDisplay is null) return;
        if (_undoSoft is not null)
        {
            CopySoft(_undoSoft, Soft);
            SyncUiFromSoft();
            await _display.ApplySoftColorAsync(SelectedDisplay.Id, Soft);
            _undoSoft = null;
        }
        if (_undoBrightness is not null && HardwareBrightnessAvailable)
        {
            HardwareBrightness = _undoBrightness.Value;
            await _display.SetHardwareBrightnessAsync(SelectedDisplay.Id, HardwareBrightness);
            _undoBrightness = null;
        }
        Status = UiLoc.Instance.T("Monitor.Undo.Done");
    }

    [RelayCommand]
    private async Task BeginModePreviewAsync()
    {
        if (SelectedDisplay is null || SelectedMode is null) return;
        var ok = AetherDialog.Confirm(
            UiLoc.Instance.T("Monitor.Mode.ConfirmTitle"),
            UiLoc.Instance.T("Monitor.Mode.ConfirmBody", SelectedMode.Label));
        if (!ok) return;

        var r = await _display.BeginModeChangeAsync(SelectedDisplay.Id, SelectedMode, TimeSpan.FromSeconds(15));
        Status = r.ResolvedDetail;
        if (!r.Success) return;
        ModePreviewActive = true;
        ModePreviewSeconds = 15;
        _previewTimer.Start();
    }

    [RelayCommand]
    private async Task ConfirmModeAsync()
    {
        if (SelectedDisplay is null) return;
        _previewTimer.Stop();
        ModePreviewActive = false;
        var r = await _display.ConfirmPendingModeAsync(SelectedDisplay.Id);
        Status = r.ResolvedDetail;
        await LoadDisplayAsync(force: true);
    }

    [RelayCommand]
    private async Task RevertModeAsync()
    {
        if (SelectedDisplay is null) return;
        _previewTimer.Stop();
        ModePreviewActive = false;
        var r = await _display.RevertPendingModeAsync(SelectedDisplay.Id);
        Status = r.ResolvedDetail;
        await LoadDisplayAsync(force: true);
    }

    [RelayCommand]
    private async Task ToggleNightAsync()
    {
        if (SelectedDisplay is null) return;
        if (NightMode)
        {
            Soft.NightMode = true;
            Soft.ColorTemperatureK = 4000;
            Soft.BlueLightReduction = 0.35;
            Soft.VisualBrightness = Math.Min(Soft.VisualBrightness, 0.75);
            Soft.SoftwareAttenuation = Math.Max(Soft.SoftwareAttenuation, 0.15);
            SyncUiFromSoft();
            await ApplySoftAsync();
            if (HardwareBrightnessAvailable)
            {
                HardwareBrightness = Math.Min(HardwareBrightness, 40);
                await ApplyHardwareBrightnessAsync();
            }
            Status = UiLoc.Instance.T("Monitor.Night.On");
        }
        else
        {
            Soft.NightMode = false;
            Soft.ColorTemperatureK = 6500;
            Soft.BlueLightReduction = 0;
            Soft.SoftwareAttenuation = 0;
            Soft.VisualBrightness = 1;
            SyncUiFromSoft();
            await ApplySoftAsync();
            Status = UiLoc.Instance.T("Monitor.Night.Off");
        }
    }

    [RelayCommand] private void OpenDisplaySettings() => _display.OpenWindowsDisplaySettings();
    [RelayCommand] private void OpenHdrSettings() => _display.OpenWindowsHdrSettings();
    [RelayCommand] private void OpenNightLightSettings() => _display.OpenWindowsNightLightSettings();
    [RelayCommand] private void OpenColorManagement() => _display.OpenWindowsColorManagement();

    [RelayCommand]
    private async Task ResetSelectedDisplayAsync()
    {
        await ResetSoftAsync();
        Status = UiLoc.Instance.T("Monitor.Reset.Display");
    }

    // Soft slider change → apply (debounced lightly by user releasing; we apply on command / property end)
    partial void OnSoftBrightnessPctChanged(double value) { Soft.VisualBrightness = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnSoftAttenuationPctChanged(double value) { Soft.SoftwareAttenuation = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnSoftContrastPctChanged(double value) { Soft.Contrast = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnSoftGammaChanged(double value) { Soft.Gamma = value; _ = ApplySoftAsync(); }
    partial void OnSoftSaturationPctChanged(double value) { Soft.Saturation = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnSoftRedPctChanged(double value) { Soft.RedGain = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnSoftGreenPctChanged(double value) { Soft.GreenGain = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnSoftBluePctChanged(double value) { Soft.BlueGain = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnColorTempKChanged(int value) { Soft.ColorTemperatureK = value; _ = ApplySoftAsync(); }
    partial void OnBlueLightPctChanged(double value) { Soft.BlueLightReduction = value / 100.0; _ = ApplySoftAsync(); }
    partial void OnHardwareBrightnessChanged(int value)
    {
        if (_suppressBrightness || !HardwareBrightnessAvailable || !_displayLoaded) return;
        _ = ApplyHardwareBrightnessAsync();
    }

    partial void OnNightModeChanged(bool value)
    {
        if (_suppressSoftApply || !_displayLoaded) return;
        _ = ToggleNightAsync();
    }

    private async Task InitDisplayAsync()
    {
        await LoadDisplayAsync(force: false);
        _displayLoaded = true;
    }

    private async Task LoadDisplayAsync(bool force)
    {
        try
        {
            var list = await _display.EnumerateAsync();
            Displays.Clear();
            foreach (var d in list) Displays.Add(d);
            SelectedDisplay = Displays.FirstOrDefault(x => x.IsPrimary) ?? Displays.FirstOrDefault();
            await LoadCapsAndModesAsync();
            await LoadSoftFromStoreAsync();
            Status = UiLoc.Instance.T("Monitor.Status.Displays", Displays.Count);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Monitor.Status.Error", ex.Message);
        }
    }

    private async Task LoadCapsAndModesAsync()
    {
        if (SelectedDisplay is null) return;
        Capabilities = await _display.GetCapabilitiesAsync(SelectedDisplay.Id);
        HardwareBrightnessAvailable = Capabilities.HardwareBrightness;
        OnPropertyChanged(nameof(HardwareBrightnessUnavailable));
        BrightnessHint = HardwareBrightnessAvailable
            ? UiLoc.Instance.T("Monitor.Brightness.Source", Capabilities.BrightnessSource)
            : UiLoc.Instance.T("Monitor.Brightness.Unsupported");
        if (Capabilities.BrightnessCurrent is int cur)
        {
            _suppressBrightness = true;
            HardwareBrightness = cur;
            _suppressBrightness = false;
        }

        Modes.Clear();
        foreach (var m in await _display.GetModesAsync(SelectedDisplay.Id))
            Modes.Add(m);
        SelectedMode = Modes.FirstOrDefault(m => m.IsCurrent) ?? Modes.FirstOrDefault();
        CalibrationNote = UiLoc.Instance.T("Monitor.SoftColor.Disclaimer");
        OnPropertyChanged(nameof(SelectedDisplay));
    }

    private async Task LoadSoftFromStoreAsync()
    {
        if (SelectedDisplay is null) return;
        var profile = await _settings.LoadProfileAsync();
        if (profile.SoftColorByDisplay.TryGetValue(SelectedDisplay.Id, out var saved))
        {
            CopySoft(saved, Soft);
            SyncUiFromSoft();
        }
        else
        {
            var last = _display.GetLastSoftColor(SelectedDisplay.Id);
            if (last is not null)
            {
                CopySoft(last, Soft);
                SyncUiFromSoft();
            }
        }
    }

    private async Task PersistSoftAsync()
    {
        if (SelectedDisplay is null) return;
        var profile = await _settings.LoadProfileAsync();
        profile.SoftColorByDisplay[SelectedDisplay.Id] = Soft.Clone();
        await _settings.SaveProfileAsync(profile);
    }

    private async Task PreviewTickAsync()
    {
        if (!ModePreviewActive || SelectedDisplay is null) return;
        ModePreviewSeconds--;
        if (ModePreviewSeconds > 0) return;
        await RevertModeAsync();
    }

    private void PushSoftFromUi()
    {
        Soft.VisualBrightness = SoftBrightnessPct / 100.0;
        Soft.SoftwareAttenuation = SoftAttenuationPct / 100.0;
        Soft.Contrast = SoftContrastPct / 100.0;
        Soft.Gamma = SoftGamma;
        Soft.Saturation = SoftSaturationPct / 100.0;
        Soft.RedGain = SoftRedPct / 100.0;
        Soft.GreenGain = SoftGreenPct / 100.0;
        Soft.BlueGain = SoftBluePct / 100.0;
        Soft.ColorTemperatureK = ColorTempK;
        Soft.BlueLightReduction = BlueLightPct / 100.0;
        Soft.NightMode = NightMode;
    }

    private void SyncUiFromSoft()
    {
        _suppressSoftApply = true;
        SoftBrightnessPct = Soft.VisualBrightness * 100;
        SoftAttenuationPct = Soft.SoftwareAttenuation * 100;
        SoftContrastPct = Soft.Contrast * 100;
        SoftGamma = Soft.Gamma;
        SoftSaturationPct = Soft.Saturation * 100;
        SoftRedPct = Soft.RedGain * 100;
        SoftGreenPct = Soft.GreenGain * 100;
        SoftBluePct = Soft.BlueGain * 100;
        ColorTempK = Soft.ColorTemperatureK;
        BlueLightPct = Soft.BlueLightReduction * 100;
        _suppressSoftApply = false;
    }

    private static void CopySoft(SoftColorState from, SoftColorState to)
    {
        to.VisualBrightness = from.VisualBrightness;
        to.SoftwareAttenuation = from.SoftwareAttenuation;
        to.Contrast = from.Contrast;
        to.Gamma = from.Gamma;
        to.Saturation = from.Saturation;
        to.RedGain = from.RedGain;
        to.GreenGain = from.GreenGain;
        to.BlueGain = from.BlueGain;
        to.ColorTemperatureK = from.ColorTemperatureK;
        to.BlueLightReduction = from.BlueLightReduction;
        to.NightMode = from.NightMode;
    }

}
