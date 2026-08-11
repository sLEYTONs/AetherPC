using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Models;
using Microsoft.Extensions.Logging;

namespace AetherPC.Infrastructure.Windows;

/// <summary>
/// Control real de pantallas: EnumDisplay*, WMI brightness, DDC/CI (dxva2), gamma ramp.
/// No inventa capacidades; degrada a Settings cuando no hay API fiable.
/// </summary>
public sealed class WindowsDisplayControlService : IDisplayControlService, IDisposable
{
    private readonly ILogger<WindowsDisplayControlService> _log;
    private readonly ConcurrentDictionary<string, SoftColorState> _soft = new();
    private readonly ConcurrentDictionary<string, ushort[]> _originalRamps = new();
    private readonly ConcurrentDictionary<string, PendingDisplayModeChange> _pending = new();
    private readonly ConcurrentDictionary<string, DisplayDeviceInfo> _cache = new();
    private readonly object _gate = new();
    private bool _disposed;

    public WindowsDisplayControlService(ILogger<WindowsDisplayControlService> log) => _log = log;

    public async Task<IReadOnlyList<DisplayDeviceInfo>> EnumerateAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var list = new List<DisplayDeviceInfo>();
            var monitors = new List<(IntPtr hMon, RECT rc)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (hMon, _, lprc, _) =>
                {
                    monitors.Add((hMon, lprc));
                    return true;
                }, IntPtr.Zero);

            var idx = 0;
            foreach (var (hMon, rc) in monitors)
            {
                ct.ThrowIfCancellationRequested();
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMon, ref mi)) continue;

                var device = mi.szDevice?.TrimEnd('\0') ?? $"\\\\.\\DISPLAY{idx + 1}";
                var isPrimary = (mi.dwFlags & 1) != 0;

                DEVMODE dm = default;
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm);

                var adapter = "";
                var friendly = device;
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(device, 0, ref dd, 0))
                {
                    friendly = string.IsNullOrWhiteSpace(dd.DeviceString) ? device : dd.DeviceString.Trim();
                    adapter = dd.DeviceString?.Trim() ?? "";
                }

                // Nombre del monitor físico (segundo nivel)
                var monDd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(device, 0, ref monDd, EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    if (!string.IsNullOrWhiteSpace(monDd.DeviceString))
                        friendly = monDd.DeviceString.Trim();
                }

                var dpi = 96;
                try { GetDpiForMonitor(hMon, 0, out var dx, out _); dpi = (int)dx; } catch { /* */ }
                var scale = Math.Round(dpi / 96.0 * 100);

                var isInternal = LooksInternal(friendly, device);
                var (hdrSup, hdrOn) = ProbeHdr(hMon);

                var id = device;
                var info = new DisplayDeviceInfo
                {
                    Id = id,
                    DeviceName = device,
                    FriendlyName = friendly,
                    IsPrimary = isPrimary,
                    IsInternal = isInternal,
                    IsActive = true,
                    Width = dm.dmPelsWidth,
                    Height = dm.dmPelsHeight,
                    RefreshHz = dm.dmDisplayFrequency,
                    BitsPerPixel = dm.dmBitsPerPel,
                    OrientationDegrees = OrientationFromDevMode(dm),
                    ScalePercent = scale,
                    AdapterName = string.IsNullOrWhiteSpace(adapter) ? null : adapter,
                    ConnectionHint = isInternal ? "Internal" : "External",
                    HdrSupported = hdrSup,
                    HdrEnabled = hdrOn,
                    IccProfileName = TryReadIccName(device),
                    HMonitor = hMon,
                    Source = "EnumDisplayMonitors+EnumDisplaySettings"
                };
                list.Add(info);
                _cache[id] = info;
                idx++;
            }

            if (list.Count == 0)
            {
                // Fallback mínimo
                var w = GetSystemMetrics(0);
                var h = GetSystemMetrics(1);
                var fallback = new DisplayDeviceInfo
                {
                    Id = "\\\\.\\DISPLAY1",
                    DeviceName = "\\\\.\\DISPLAY1",
                    FriendlyName = "Monitor principal",
                    IsPrimary = true,
                    IsActive = true,
                    Width = w,
                    Height = h,
                    RefreshHz = 60,
                    BitsPerPixel = 32,
                    ScalePercent = 100,
                    Source = "GetSystemMetrics"
                };
                list.Add(fallback);
                _cache[fallback.Id] = fallback;
            }

            return (IReadOnlyList<DisplayDeviceInfo>)list;
        }, ct).ConfigureAwait(false);
    }

    public async Task<DisplayCapabilities> GetCapabilitiesAsync(string displayId, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            EnsureCached(displayId);
            _cache.TryGetValue(displayId, out var d);

            var caps = new DisplayCapabilities
            {
                DisplayId = displayId,
                SoftwareGamma = true,
                SoftwareAttenuation = true,
                ColorTemperatureFilter = true,
                RgbBalance = true,
                ContrastGamma = true,
                CanChangeDisplayMode = true,
                HdrReported = d?.HdrSupported == true
            };

            var (wmiOk, wmiCur, wmiMin, wmiMax) = TryReadWmiBrightness();
            var (ddcOk, ddcCur, ddcMin, ddcMax) = TryReadDdcBrightness(displayId);

            if (wmiOk && (d?.IsInternal != false || d == null))
            {
                caps.HardwareBrightness = true;
                caps.BrightnessSource = "Wmi";
                caps.BrightnessCurrent = wmiCur;
                caps.BrightnessMin = wmiMin;
                caps.BrightnessMax = wmiMax;
            }
            else if (ddcOk)
            {
                caps.HardwareBrightness = true;
                caps.BrightnessSource = "Ddc";
                caps.DdcCi = true;
                caps.BrightnessCurrent = ddcCur;
                caps.BrightnessMin = ddcMin;
                caps.BrightnessMax = ddcMax;
            }
            else
            {
                caps.Notes = "Monitor.Brightness.Unsupported";
            }

            if (ddcOk) caps.DdcCi = true;
            return caps;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DisplayModeInfo>> GetModesAsync(string displayId, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            EnsureCached(displayId);
            var device = displayId;
            var modes = new Dictionary<(int, int, int, int), DisplayModeInfo>();
            DEVMODE dm = default;
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            for (var i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.dmBitsPerPel < 24) continue;
                if (dm.dmPelsWidth < 800 || dm.dmPelsHeight < 600) continue;
                var key = (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel);
                if (modes.ContainsKey(key)) continue;
                modes[key] = new DisplayModeInfo
                {
                    Width = dm.dmPelsWidth,
                    Height = dm.dmPelsHeight,
                    RefreshHz = dm.dmDisplayFrequency,
                    BitsPerPixel = dm.dmBitsPerPel
                };
            }

            DEVMODE cur = default;
            cur.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref cur);
            var list = modes.Values
                .OrderByDescending(m => m.Width * m.Height)
                .ThenByDescending(m => m.RefreshHz)
                .ToList();

            // Nativa aproximada = mayor resolución reportada
            var native = list.FirstOrDefault();
            return (IReadOnlyList<DisplayModeInfo>)list.Select(m => new DisplayModeInfo
            {
                Width = m.Width,
                Height = m.Height,
                RefreshHz = m.RefreshHz,
                BitsPerPixel = m.BitsPerPixel,
                IsCurrent = m.Width == cur.dmPelsWidth && m.Height == cur.dmPelsHeight && m.RefreshHz == cur.dmDisplayFrequency,
                IsRecommended = native != null && m.Width == native.Width && m.Height == native.Height
            }).ToList();
        }, ct).ConfigureAwait(false);
    }

    public async Task<ActionResult> SetHardwareBrightnessAsync(string displayId, int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        return await Task.Run(() =>
        {
            var caps = GetCapabilitiesAsync(displayId, ct).GetAwaiter().GetResult();
            if (!caps.HardwareBrightness)
            {
                return Fail("brightness", "Monitor.Brightness.Unsupported");
            }

            if (caps.BrightnessSource == "Wmi")
            {
                if (!TrySetWmiBrightness(percent))
                    return Fail("brightness", "Monitor.Brightness.SetFailed");
                return Ok("brightness", "Monitor.Brightness.SetOk", percent.ToString());
            }

            if (caps.BrightnessSource == "Ddc")
            {
                if (!TrySetDdcBrightness(displayId, percent))
                    return Fail("brightness", "Monitor.Brightness.SetFailed");
                return Ok("brightness", "Monitor.Brightness.SetOk", percent.ToString());
            }

            return Fail("brightness", "Monitor.Brightness.Unsupported");
        }, ct).ConfigureAwait(false);
    }

    public async Task<int?> ReadHardwareBrightnessAsync(string displayId, CancellationToken ct = default)
    {
        var caps = await GetCapabilitiesAsync(displayId, ct).ConfigureAwait(false);
        return caps.BrightnessCurrent;
    }

    public async Task<ActionResult> ApplySoftColorAsync(string displayId, SoftColorState state, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            state = ClampSoft(state);
            var hdc = CreateDC(null, displayId, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                hdc = GetDC(IntPtr.Zero); // primary fallback

            try
            {
                if (!_originalRamps.ContainsKey(displayId))
                {
                    var orig = new ushort[256 * 3];
                    if (GetDeviceGammaRamp(hdc, orig))
                        _originalRamps[displayId] = (ushort[])orig.Clone();
                    else
                        _originalRamps[displayId] = BuildIdentityRamp();
                }

                var ramp = BuildRamp(state);
                if (!SetDeviceGammaRamp(hdc, ramp))
                    return Fail("softcolor", "Monitor.SoftColor.ApplyFailed");

                _soft[displayId] = state.Clone();
                return Ok("softcolor", "Monitor.SoftColor.Applied");
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    // CreateDC needs DeleteDC; GetDC needs ReleaseDC — try both safely
                    if (!DeleteDC(hdc))
                        ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<ActionResult> ResetSoftColorAsync(string displayId, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            RestoreSoft(displayId);
            _soft.TryRemove(displayId, out _);
            return Ok("softcolor", "Monitor.SoftColor.Reset");
        }, ct).ConfigureAwait(false);
    }

    public SoftColorState? GetLastSoftColor(string displayId) =>
        _soft.TryGetValue(displayId, out var s) ? s.Clone() : null;

    public bool HasSoftColorOverride(string displayId) => _soft.ContainsKey(displayId);

    public async Task<ActionResult> BeginModeChangeAsync(string displayId, DisplayModeInfo mode, TimeSpan previewWindow, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            DEVMODE cur = default;
            cur.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettings(displayId, ENUM_CURRENT_SETTINGS, ref cur))
                return Fail("mode", "Monitor.Mode.ReadFailed");

            var previous = new DisplayModeInfo
            {
                Width = cur.dmPelsWidth,
                Height = cur.dmPelsHeight,
                RefreshHz = cur.dmDisplayFrequency,
                BitsPerPixel = cur.dmBitsPerPel,
                IsCurrent = true
            };

            var target = cur;
            target.dmPelsWidth = mode.Width;
            target.dmPelsHeight = mode.Height;
            target.dmDisplayFrequency = mode.RefreshHz;
            target.dmBitsPerPel = mode.BitsPerPixel > 0 ? mode.BitsPerPixel : cur.dmBitsPerPel;
            target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;

            var test = ChangeDisplaySettingsEx(displayId, ref target, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != DISP_CHANGE_SUCCESSFUL)
                return Fail("mode", "Monitor.Mode.Unsupported");

            var apply = ChangeDisplaySettingsEx(displayId, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_RESET, IntPtr.Zero);
            if (apply != DISP_CHANGE_SUCCESSFUL)
                return Fail("mode", "Monitor.Mode.ApplyFailed");

            _pending[displayId] = new PendingDisplayModeChange
            {
                DisplayId = displayId,
                Target = mode,
                Previous = previous,
                ExpiresAt = DateTimeOffset.Now.Add(previewWindow)
            };

            return new ActionResult
            {
                ActionId = "mode",
                Success = true,
                DetailKey = "Monitor.Mode.Preview",
                Status = ActionApplyStatus.Applied,
                RollbackToken = $"{previous.Width}x{previous.Height}@{previous.RefreshHz}"
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<ActionResult> ConfirmPendingModeAsync(string displayId, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        _pending.TryRemove(displayId, out _);
        return Ok("mode", "Monitor.Mode.Confirmed");
    }

    public async Task<ActionResult> RevertPendingModeAsync(string displayId, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!_pending.TryRemove(displayId, out var pending))
                return Fail("mode", "Monitor.Mode.NoPending");

            var prev = pending.Previous;
            DEVMODE dm = default;
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            EnumDisplaySettings(displayId, ENUM_CURRENT_SETTINGS, ref dm);
            dm.dmPelsWidth = prev.Width;
            dm.dmPelsHeight = prev.Height;
            dm.dmDisplayFrequency = prev.RefreshHz;
            dm.dmBitsPerPel = prev.BitsPerPixel;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
            var r = ChangeDisplaySettingsEx(displayId, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_RESET, IntPtr.Zero);
            return r == DISP_CHANGE_SUCCESSFUL
                ? Ok("mode", "Monitor.Mode.Reverted")
                : Fail("mode", "Monitor.Mode.RevertFailed");
        }, ct).ConfigureAwait(false);
    }

    public PendingDisplayModeChange? GetPendingMode(string displayId) =>
        _pending.TryGetValue(displayId, out var p) ? p : null;

    public void OpenWindowsDisplaySettings() => OpenUri("ms-settings:display");
    public void OpenWindowsHdrSettings() => OpenUri("ms-settings:display-advancedgraphics");
    public void OpenWindowsNightLightSettings() => OpenUri("ms-settings:nightlight");
    public void OpenWindowsColorManagement() =>
        OpenUri("colorcpl.exe"); // color management applet

    public void RestoreAllSoftColor()
    {
        foreach (var id in _originalRamps.Keys.ToList())
            RestoreSoft(id);
        _soft.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { RestoreAllSoftColor(); } catch { /* */ }
    }

    // —— Soft color ——

    private void RestoreSoft(string displayId)
    {
        if (!_originalRamps.TryGetValue(displayId, out var ramp))
            ramp = BuildIdentityRamp();

        var hdc = CreateDC(null, displayId, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) hdc = GetDC(IntPtr.Zero);
        try
        {
            SetDeviceGammaRamp(hdc, ramp);
        }
        finally
        {
            if (hdc != IntPtr.Zero && !DeleteDC(hdc))
                ReleaseDC(IntPtr.Zero, hdc);
        }
        _originalRamps.TryRemove(displayId, out _);
    }

    private static SoftColorState ClampSoft(SoftColorState s)
    {
        s.VisualBrightness = Math.Clamp(s.VisualBrightness, 0.55, 1.0);
        s.SoftwareAttenuation = Math.Clamp(s.SoftwareAttenuation, 0, 0.25);
        s.Contrast = Math.Clamp(s.Contrast, 0.7, 1.35);
        s.Gamma = Math.Clamp(s.Gamma, 0.90, 1.35);
        s.Saturation = Math.Clamp(s.Saturation, 0.65, 1.35);
        s.RedGain = Math.Clamp(s.RedGain, 0.75, 1.40);
        s.GreenGain = Math.Clamp(s.GreenGain, 0.75, 1.40);
        s.BlueGain = Math.Clamp(s.BlueGain, 0.75, 1.40);
        s.ColorTemperatureK = Math.Clamp(s.ColorTemperatureK, 4200, 7500);
        s.BlueLightReduction = Math.Clamp(s.BlueLightReduction, 0, 0.40);
        return s;
    }

    private static ushort[] BuildIdentityRamp()
    {
        var ramp = new ushort[256 * 3];
        for (var i = 0; i < 256; i++)
        {
            var v = (ushort)(i * 257);
            ramp[i] = v;
            ramp[256 + i] = v;
            ramp[512 + i] = v;
        }
        return ramp;
    }

    private static ushort[] BuildRamp(SoftColorState s)
    {
        KelvinToRgb(s.ColorTemperatureK, out var tr, out var tg, out var tb);
        // Blue light reduction further cuts blue
        tb *= (1.0 - s.BlueLightReduction * 0.85);
        tg *= (1.0 - s.BlueLightReduction * 0.25);

        var bright = s.VisualBrightness * (1.0 - s.SoftwareAttenuation);
        var contrast = s.Contrast;
        var gamma = s.Gamma;
        var sat = s.Saturation;

        var ramp = new ushort[256 * 3];
        for (var i = 0; i < 256; i++)
        {
            var n = i / 255.0;
            // gamma
            var g = Math.Pow(Math.Clamp(n, 0, 1), 1.0 / gamma);
            // contrast around 0.5
            g = (g - 0.5) * contrast + 0.5;
            g = Math.Clamp(g, 0, 1) * bright;

            var r = g * tr * s.RedGain;
            var gre = g * tg * s.GreenGain;
            var b = g * tb * s.BlueGain;

            // saturation approx toward luminance
            var lum = 0.2126 * r + 0.7152 * gre + 0.0722 * b;
            r = lum + (r - lum) * sat;
            gre = lum + (gre - lum) * sat;
            b = lum + (b - lum) * sat;

            ramp[i] = ToRamp(r);
            ramp[256 + i] = ToRamp(gre);
            ramp[512 + i] = ToRamp(b);
        }
        return ramp;
    }

    private static ushort ToRamp(double v) =>
        (ushort)Math.Clamp((int)(Math.Clamp(v, 0, 1) * 65535.0), 0, 65535);

    private static void KelvinToRgb(int kelvin, out double r, out double g, out double b)
    {
        // Approximation (Tanner Helland / similar)
        var temp = kelvin / 100.0;
        if (temp <= 66)
        {
            r = 1;
            g = Math.Clamp(0.390081578 * Math.Log(temp) - 0.631841443, 0, 1);
        }
        else
        {
            r = Math.Clamp(1.292936 * Math.Pow(temp - 60, -0.1332047592), 0, 1);
            g = Math.Clamp(1.12989086 * Math.Pow(temp - 60, -0.0755148492), 0, 1);
        }
        if (temp >= 66) b = 1;
        else if (temp <= 19) b = 0;
        else b = Math.Clamp(0.543206789 * Math.Log(temp - 10) - 1.196254089, 0, 1);
    }

    // —— Brightness WMI / DDC ——

    private static (bool ok, int cur, int min, int max) TryReadWmiBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
            {
                var cur = Convert.ToInt32(o["CurrentBrightness"]);
                var levels = o["Level"] as byte[];
                var min = 0;
                var max = 100;
                if (levels is { Length: > 0 })
                {
                    min = levels.Min(x => (int)x);
                    max = levels.Max(x => (int)x);
                }
                return (true, cur, min, max);
            }
        }
        catch { /* no WMI brightness */ }
        return (false, 0, 0, 100);
    }

    private static bool TrySetWmiBrightness(int percent)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { 1, (byte)percent });
                return true;
            }
        }
        catch { /* */ }
        return false;
    }

    private (bool ok, int cur, int min, int max) TryReadDdcBrightness(string displayId)
    {
        if (!_cache.TryGetValue(displayId, out var d) || d.HMonitor == IntPtr.Zero)
            return (false, 0, 0, 100);

        var arr = new PHYSICAL_MONITOR[1];
        if (!GetPhysicalMonitorsFromHMONITOR(d.HMonitor, 1, arr))
            return (false, 0, 0, 100);
        try
        {
            if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, out var min, out var cur, out var max))
                return (false, 0, 0, 100);
            if (max <= min) return (false, 0, 0, 100);
            var pct = (int)Math.Round((cur - min) * 100.0 / (max - min));
            return (true, pct, 0, 100);
        }
        finally
        {
            DestroyPhysicalMonitors(1, arr);
        }
    }

    private bool TrySetDdcBrightness(string displayId, int percent)
    {
        if (!_cache.TryGetValue(displayId, out var d) || d.HMonitor == IntPtr.Zero)
            return false;
        var arr = new PHYSICAL_MONITOR[1];
        if (!GetPhysicalMonitorsFromHMONITOR(d.HMonitor, 1, arr))
            return false;
        try
        {
            if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, out var min, out _, out var max))
                return false;
            if (max <= min) return false;
            var value = (uint)(min + (max - min) * percent / 100.0);
            return SetMonitorBrightness(arr[0].hPhysicalMonitor, value);
        }
        finally
        {
            DestroyPhysicalMonitors(1, arr);
        }
    }

    private void EnsureCached(string displayId)
    {
        if (_cache.ContainsKey(displayId)) return;
        EnumerateAsync().GetAwaiter().GetResult();
    }

    private static bool LooksInternal(string name, string device)
    {
        var n = (name + " " + device).ToLowerInvariant();
        return n.Contains("internal") || n.Contains("built-in") || n.Contains("panel")
               || n.Contains("lcd") || n.Contains("edp") || n.Contains("intel(r) uhd")
               || n.Contains("laptop");
    }

    private static int OrientationFromDevMode(DEVMODE dm)
    {
        // dmDisplayOrientation if present in fields — default 0
        return 0;
    }

    private static (bool? supported, bool? enabled) ProbeHdr(IntPtr hMonitor)
    {
        // Sin DXGI avanzado fiable aquí: no afirmar HDR. Dejar null / false.
        return (null, null);
    }

    private static string? TryReadIccName(string device)
    {
        try
        {
            // Lectura ligera vía GetICMProfile
            var hdc = CreateDC(null, device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var size = 0;
                GetICMProfile(hdc, ref size, null);
                if (size <= 0 || size > 2048) return null;
                var buf = new char[size];
                if (!GetICMProfile(hdc, ref size, buf)) return null;
                var path = new string(buf, 0, Math.Max(0, size - 1)).Trim('\0');
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
            }
            finally
            {
                DeleteDC(hdc);
            }
        }
        catch { return null; }
    }

    private static void OpenUri(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
        }
        catch { /* */ }
    }

    private static ActionResult Ok(string id, string key, params string[] args) => new()
    {
        ActionId = id,
        Success = true,
        DetailKey = key,
        DetailArgs = args,
        Status = ActionApplyStatus.Applied
    };

    private static ActionResult Fail(string id, string key) => new()
    {
        ActionId = id,
        Success = false,
        DetailKey = key,
        Status = ActionApplyStatus.Failed
    };

    // —— P/Invoke ——

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_BITSPERPEL = 0x40000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const int CDS_TEST = 0x00000002;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int CDS_RESET = 0x40000000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetICMProfile(IntPtr hdc, ref int pBufSize, char[]? pszFilename);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hPhysicalMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint dwNewBrightness);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }
}
