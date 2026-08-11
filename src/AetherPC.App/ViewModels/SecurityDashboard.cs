using System.Windows.Media;
using AetherPC.App.Services;
using AetherPC.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherPC.App.ViewModels;

public enum SecurityTone
{
    Ok,
    Info,
    Warn,
    Bad,
    Unknown
}

public partial class SecurityCardItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "●";
    public string StatusText { get; init; } = "";
    public SecurityTone Tone { get; init; } = SecurityTone.Unknown;
    public string Detail { get; init; } = "";
    public string Importance { get; init; } = "";
    public string Description { get; init; } = "";
    public string Explain { get; init; } = "";

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Color fijo (no FindResource) para evitar excepciones al pintar tarjetas.</summary>
    public Brush StatusBrush => Tone switch
    {
        SecurityTone.Ok => SecurityBrushes.Ok,
        SecurityTone.Warn => SecurityBrushes.Warn,
        SecurityTone.Bad => SecurityBrushes.Bad,
        SecurityTone.Info => SecurityBrushes.Info,
        _ => SecurityBrushes.Muted
    };

    [RelayCommand]
    private void ToggleExplain() => IsExpanded = !IsExpanded;
}

internal static class SecurityBrushes
{
    public static readonly Brush Ok = Freeze(0x3D, 0xDC, 0x97);
    public static readonly Brush Warn = Freeze(0xF0, 0xA2, 0x02);
    public static readonly Brush Bad = Freeze(0xFF, 0x5C, 0x5C);
    public static readonly Brush Info = Freeze(0x3B, 0xA4, 0xFF);
    public static readonly Brush Muted = Freeze(0x8B, 0x98, 0xB0);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class SecurityDashboardBuilder
{
    private static string T(string key, params object[] args) => UiLoc.Instance.T(key, args);
    private static string Na => T("Security.NotAvailable");

    public static (int Score, string Label, SecurityTone Tone) Score(SecurityInfo s)
    {
        var bits = new List<(bool? Value, int Weight)>
        {
            (s.DefenderEnabled, 20),
            (s.FirewallEnabled, 18),
            (s.SecureBootEnabled, 14),
            (s.TpmPresent, 14),
            (s.SmartScreenEnabled, 10),
            (s.UacEnabled, 10),
            (s.MemoryIntegrityEnabled, 8),
            (s.BitLockerActive, 6)
        };

        var known = bits.Where(b => b.Value is not null).ToList();
        if (known.Count == 0)
            return (0, T("Security.ScoreUnknown"), SecurityTone.Unknown);

        var weightSum = known.Sum(b => b.Weight);
        var earned = known.Sum(b => b.Value == true ? b.Weight : 0);
        var score = (int)Math.Round(100.0 * earned / weightSum);

        if (score >= 90) return (score, T("Security.ScoreExcellent"), SecurityTone.Ok);
        if (score >= 70) return (score, T("Security.ScoreGood"), SecurityTone.Info);
        if (score >= 45) return (score, T("Security.ScoreAttention"), SecurityTone.Warn);
        return (score, T("Security.ScoreCritical"), SecurityTone.Bad);
    }

    public static IReadOnlyList<SecurityCardItem> BuildCards(SecurityInfo s)
    {
        var cards = new List<SecurityCardItem>
        {
            Card("defender", T("Security.Defender"), "D",
                BoolStatus(s.DefenderEnabled), ToneOf(s.DefenderEnabled),
                T("Security.DefenderDetail", BoolLabel(s.DefenderEnabled)),
                T("Security.ImportanceHigh"),
                T("Security.DefenderHint"),
                T("Security.Explain.Defender")),

            Card("firewall", T("Security.Firewall"), "F",
                BoolStatus(s.FirewallEnabled), ToneOf(s.FirewallEnabled),
                FirewallDetail(s),
                T("Security.ImportanceHigh"),
                T("Security.FirewallHint"),
                T("Security.Explain.Firewall")),

            Card("secureboot", T("Security.SecureBoot"), "S",
                BoolStatus(s.SecureBootEnabled), ToneOf(s.SecureBootEnabled),
                T("Security.SecureBootDetail", BoolLabel(s.SecureBootEnabled)),
                T("Security.ImportanceHigh"),
                T("Security.SecureBootHint"),
                T("Security.Explain.SecureBoot")),

            Card("tpm", T("Security.Tpm"), "T",
                BoolStatus(s.TpmPresent), ToneOf(s.TpmPresent),
                TpmDetail(s),
                T("Security.ImportanceHigh"),
                T("Security.TpmHint"),
                T("Security.Explain.Tpm")),

            Card("bitlocker", T("Security.BitLocker"), "B",
                BitLockerStatus(s), BitLockerTone(s),
                BitLockerDetail(s),
                T("Security.ImportanceMedium"),
                T("Security.BitLockerHint"),
                T("Security.Explain.BitLocker")),

            Card("smartscreen", T("Security.SmartScreen"), "W",
                BoolStatus(s.SmartScreenEnabled), ToneOf(s.SmartScreenEnabled, warnIfOff: true),
                T("Security.SmartScreenDetail", BoolLabel(s.SmartScreenEnabled)),
                T("Security.ImportanceMedium"),
                T("Security.SmartScreenHint"),
                T("Security.Explain.SmartScreen")),

            Card("hvci", T("Security.MemoryIntegrity"), "M",
                BoolStatus(s.MemoryIntegrityEnabled), ToneOf(s.MemoryIntegrityEnabled, warnIfOff: true),
                T("Security.MemoryIntegrityDetail", BoolLabel(s.MemoryIntegrityEnabled)),
                T("Security.ImportanceMedium"),
                T("Security.MemoryIntegrityHint"),
                T("Security.Explain.MemoryIntegrity")),

            Card("uac", T("Security.Uac"), "U",
                BoolStatus(s.UacEnabled), ToneOf(s.UacEnabled),
                UacDetail(s),
                T("Security.ImportanceMedium"),
                T("Security.UacHint"),
                T("Security.Explain.Uac")),

            Card("ransomware", T("Security.Ransomware"), "R",
                Na, SecurityTone.Unknown,
                T("Security.RansomwareDetail"),
                T("Security.ImportanceMedium"),
                T("Security.RansomwareHint"),
                T("Security.Explain.Ransomware")),

            Card("wus", T("Security.WindowsUpdate"), "U",
                Na, SecurityTone.Unknown,
                T("Security.WindowsUpdateDetail"),
                T("Security.ImportanceMedium"),
                T("Security.WindowsUpdateHint"),
                T("Security.Explain.WindowsUpdate"))
        };
        return cards;
    }

    public static IReadOnlyList<string> BuildRisks(SecurityInfo s)
    {
        var list = new List<string>();
        if (s.DefenderEnabled == false) list.Add(T("Security.Risk.DefenderOff"));
        if (s.FirewallEnabled == false) list.Add(T("Security.Risk.FirewallOff"));
        if (s.SecureBootEnabled == false) list.Add(T("Security.Risk.SecureBootOff"));
        if (s.TpmPresent == false) list.Add(T("Security.Risk.TpmMissing"));
        if (s.SmartScreenEnabled == false) list.Add(T("Security.Risk.SmartScreenOff"));
        if (s.MemoryIntegrityEnabled == false) list.Add(T("Security.Risk.MemoryIntegrityOff"));
        if (s.UacEnabled == false) list.Add(T("Security.Risk.UacOff"));
        return list;
    }

    public static IReadOnlyList<string> BuildRecommendations(SecurityInfo s)
    {
        var list = new List<string>();
        if (s.DefenderEnabled == true && s.FirewallEnabled == true)
            list.Add(T("Security.Rec.CoreOk"));
        if (s.MemoryIntegrityEnabled == false)
            list.Add(T("Security.Rec.EnableMemoryIntegrity"));
        if (s.BitLockerActive == false)
            list.Add(T("Security.Rec.NoBitLocker"));
        if (s.BitLockerActive is null)
            list.Add(T("Security.Rec.BitLockerUnknown"));
        if (s.TpmPresent == true)
            list.Add(T("Security.Rec.TpmReady"));
        if (s.SecureBootEnabled == true)
            list.Add(T("Security.Rec.SecureBootOk"));
        if (s.SmartScreenEnabled == false)
            list.Add(T("Security.Rec.EnableSmartScreen"));
        if (list.Count == 0)
            list.Add(T("Security.Rec.NoMajorRisks"));
        return list;
    }

    public static IReadOnlyList<(string Label, string Value)> BuildOsRows(OsInfo os, SecurityInfo s)
    {
        var rows = new List<(string, string)>
        {
            (T("Security.Os.Version"), string.IsNullOrWhiteSpace(os.Version) ? Na : os.Version),
            (T("Security.Os.Edition"), string.IsNullOrWhiteSpace(os.Edition) || os.Edition == NotDetected.Text ? Na : os.Edition),
            (T("Security.Os.Build"), string.IsNullOrWhiteSpace(os.Build) || os.Build == NotDetected.Text ? Na : os.Build),
            (T("Security.Os.Caption"), string.IsNullOrWhiteSpace(os.Caption) || os.Caption == NotDetected.Text ? Na : os.Caption),
            (T("Security.Os.Arch"), os.Architecture),
            (T("Security.Os.Vbs"), BoolLabel(s.VirtualizationBasedSecurity)),
            (T("Security.Os.CredentialGuard"), BoolLabel(s.CredentialGuardEnabled)),
            (T("Security.Os.Win11Ready"), Win11Ready(s))
        };
        return rows;
    }

    private static SecurityCardItem Card(
        string id, string title, string glyph, string status, SecurityTone tone,
        string detail, string importance, string description, string explain)
        => new()
        {
            Id = id,
            Title = title,
            Glyph = glyph,
            StatusText = status,
            Tone = tone,
            Detail = detail,
            Importance = importance,
            Description = description,
            Explain = explain
        };

    private static string BoolStatus(bool? v) => v switch
    {
        true => T("Security.Status.On"),
        false => T("Security.Status.Off"),
        _ => Na
    };

    private static string BoolLabel(bool? v) => v switch
    {
        true => T("Security.Yes"),
        false => T("Security.No"),
        _ => Na
    };

    private static SecurityTone ToneOf(bool? v, bool warnIfOff = false) => v switch
    {
        true => SecurityTone.Ok,
        false => warnIfOff ? SecurityTone.Warn : SecurityTone.Bad,
        _ => SecurityTone.Unknown
    };

    private static string FirewallDetail(SecurityInfo s)
    {
        if (s.FirewallDomainOn is null && s.FirewallPrivateOn is null && s.FirewallPublicOn is null)
            return T("Security.FirewallDetailSimple", BoolLabel(s.FirewallEnabled));
        return T("Security.FirewallDetailProfiles",
            BoolLabel(s.FirewallDomainOn),
            BoolLabel(s.FirewallPrivateOn),
            BoolLabel(s.FirewallPublicOn));
    }

    private static string TpmDetail(SecurityInfo s)
    {
        var ver = string.IsNullOrWhiteSpace(s.TpmVersion) ? Na : s.TpmVersion;
        var mfr = string.IsNullOrWhiteSpace(s.TpmManufacturer) ? Na : s.TpmManufacturer;
        return T("Security.TpmDetail", BoolLabel(s.TpmPresent), ver, mfr);
    }

    private static string BitLockerStatus(SecurityInfo s) => s.BitLockerActive switch
    {
        true => T("Security.Status.Protected"),
        false => T("Security.Status.Unprotected"),
        _ => Na
    };

    private static SecurityTone BitLockerTone(SecurityInfo s) => s.BitLockerActive switch
    {
        true => SecurityTone.Ok,
        false => SecurityTone.Warn,
        _ => SecurityTone.Unknown
    };

    private static string BitLockerDetail(SecurityInfo s)
        => string.IsNullOrWhiteSpace(s.BitLockerDetail)
            ? T("Security.BitLockerDetailNa")
            : T("Security.BitLockerDetailVolumes", s.BitLockerDetail);

    private static string UacDetail(SecurityInfo s)
    {
        var level = s.UacLevel switch
        {
            "NeverNotify" => T("Security.Uac.NeverNotify"),
            "PromptCredentials" => T("Security.Uac.PromptCredentials"),
            "PromptConsent" => T("Security.Uac.PromptConsent"),
            "Configured" => T("Security.Uac.Configured"),
            _ => Na
        };
        return T("Security.UacDetail", BoolLabel(s.UacEnabled), level);
    }

    private static string Win11Ready(SecurityInfo s)
    {
        if (s.TpmPresent is null && s.SecureBootEnabled is null) return Na;
        var tpmOk = s.TpmPresent == true;
        var sbOk = s.SecureBootEnabled == true;
        if (tpmOk && sbOk) return T("Security.Yes");
        if (s.TpmPresent == false || s.SecureBootEnabled == false) return T("Security.No");
        return Na;
    }
}
