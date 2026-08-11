using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using WinApp = System.Windows.Application;

namespace AetherPC.App.Services;

/// <summary>
/// Aplica Dark / Light / Auto a Wpf.Ui y a todos los brushes AetherPC
/// mutando las mismas instancias (compatible con StaticResource y DynamicResource).
/// </summary>
public static class ThemeService
{
    private static bool _prepared;

    public static bool IsDark { get; private set; } = true;

    private static readonly string[] BrushKeys =
    {
        "BrushBg", "BrushPanel", "BrushAccent", "BrushOk", "BrushWarn", "BrushDanger",
        "BrushAi", "BrushText", "BrushMuted", "BrushSecondaryText",
        "BrushSurface", "BrushSurfaceStrong", "BrushSurfaceSoft", "BrushRow",
        "BrushNav", "BrushFooter", "BrushBorder", "BrushBorderSubtle",
        "BrushHover", "BrushChip", "BrushInputBg", "BrushTableHeader",
        // Botones
        "BrushBtnPrimaryBg", "BrushBtnPrimaryHover", "BrushBtnPrimaryPressed", "BrushBtnPrimaryFg",
        "BrushBtnSuccessBg", "BrushBtnSuccessHover", "BrushBtnSuccessPressed", "BrushBtnSuccessFg",
        "BrushBtnSecondaryBg", "BrushBtnSecondaryHover", "BrushBtnSecondaryPressed",
        "BrushBtnSecondaryFg", "BrushBtnSecondaryBorder",
        "BrushBtnNeutralBg", "BrushBtnNeutralHover", "BrushBtnNeutralFg", "BrushBtnNeutralBorder",
        "BrushBtnTertiaryHover", "BrushBtnTertiaryFg", "BrushBtnTertiaryBorder",
        "BrushBtnDangerBg", "BrushBtnDangerHover", "BrushBtnDangerFg", "BrushBtnDangerBorder",
        "BrushBtnWarningBg", "BrushBtnWarningHover", "BrushBtnWarningFg", "BrushBtnWarningBorder",
        "BrushBtnFilterBg", "BrushBtnFilterHover", "BrushBtnFilterBorder", "BrushBtnFilterFg",
        "BrushBtnFilterActiveBg", "BrushBtnFilterActiveBorder", "BrushBtnFilterActiveFg",
        "BrushBtnActionBg", "BrushBtnActionHover", "BrushBtnActionFg", "BrushBtnActionBorder",
        "BrushBtnSortBg", "BrushBtnSortHover", "BrushBtnSortFg", "BrushBtnSortBorder",
        "BrushBtnSortActiveBg", "BrushBtnSortActiveFg", "BrushBtnSortActiveBorder",
        "BrushBtnDisabledBg", "BrushBtnDisabledFg", "BrushBtnDisabledBorder",
        // Nav
        "BrushNavActiveBg", "BrushNavActiveFg", "BrushNavHoverBg", "BrushNavInactiveFg", "BrushNavActiveBar", "BrushNavActiveBorder"
    };

    public static void Apply(string? theme)
    {
        EnsureMutableBrushes();
        IsDark = ResolveIsDark(theme);

        try
        {
            ApplicationThemeManager.Apply(IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        }
        catch
        {
            // Wpf.Ui opcional
        }

        if (IsDark) ApplyDarkPalette();
        else ApplyLightPalette();
    }

    private static bool ResolveIsDark(string? theme)
    {
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(theme, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light;
            }
            catch
            {
                return true;
            }
        }

        return true;
    }

    private static void EnsureMutableBrushes()
    {
        if (_prepared) return;
        var res = WinApp.Current.Resources;
        foreach (var key in BrushKeys)
        {
            if (res[key] is SolidColorBrush b)
            {
                if (b.IsFrozen)
                    res[key] = b.Clone();
            }
            else
            {
                res[key] = new SolidColorBrush(Colors.Transparent);
            }
        }

        _prepared = true;
    }

    private static void Set(string key, Color color)
    {
        var res = WinApp.Current.Resources;
        if (res[key] is SolidColorBrush b)
        {
            if (b.IsFrozen)
            {
                var clone = b.Clone();
                clone.Color = color;
                res[key] = clone;
            }
            else
            {
                b.Color = color;
            }
        }
        else
        {
            res[key] = new SolidColorBrush(color);
        }
    }

    private static void SetGradient(params Color[] stops)
    {
        var res = WinApp.Current.Resources;
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        for (var i = 0; i < stops.Length; i++)
        {
            var offset = stops.Length == 1 ? 0 : (double)i / (stops.Length - 1);
            g.GradientStops.Add(new GradientStop(stops[i], offset));
        }

        res["HeroGradient"] = g;
    }

    private static void ApplyDarkPalette()
    {
        // Superficies (sin cambiar identidad de fondo)
        Set("BrushBg", Color.FromRgb(0x0B, 0x12, 0x20));
        Set("BrushPanel", Color.FromArgb(0xCC, 0x12, 0x1A, 0x2B));
        Set("BrushAccent", Color.FromRgb(0x3B, 0xA4, 0xFF));
        Set("BrushOk", Color.FromRgb(0x3D, 0xDC, 0x97));
        Set("BrushWarn", Color.FromRgb(0xF0, 0xA2, 0x02));
        Set("BrushDanger", Color.FromRgb(0xFF, 0x5C, 0x5C));
        Set("BrushAi", Color.FromRgb(0x9B, 0x7B, 0xFF));
        Set("BrushText", Color.FromRgb(0xE8, 0xEE, 0xF8));
        Set("BrushMuted", Color.FromRgb(0x8B, 0x98, 0xB0));
        Set("BrushSecondaryText", Color.FromRgb(0xB8, 0xC4, 0xD8));
        Set("BrushSurface", Color.FromArgb(0x66, 0x12, 0x1A, 0x2B));
        Set("BrushSurfaceStrong", Color.FromArgb(0xB3, 0x12, 0x1A, 0x2B));
        Set("BrushSurfaceSoft", Color.FromArgb(0x22, 0x12, 0x1A, 0x2B));
        Set("BrushRow", Color.FromArgb(0x99, 0x12, 0x1A, 0x2B));
        Set("BrushNav", Color.FromArgb(0xAA, 0x0A, 0x10, 0x20));
        Set("BrushFooter", Color.FromArgb(0xE6, 0x12, 0x1A, 0x2B));
        Set("BrushBorder", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        Set("BrushBorderSubtle", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Set("BrushHover", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        Set("BrushChip", Color.FromArgb(0x33, 0x1F, 0x6F, 0xEB));
        Set("BrushInputBg", Color.FromArgb(0x33, 0x12, 0x1A, 0x2B));
        Set("BrushTableHeader", Color.FromArgb(0x44, 0x12, 0x1A, 0x2B));

        // Primary #2563EB
        Set("BrushBtnPrimaryBg", Color.FromRgb(0x25, 0x63, 0xEB));
        Set("BrushBtnPrimaryHover", Color.FromRgb(0x3B, 0x82, 0xF6));
        Set("BrushBtnPrimaryPressed", Color.FromRgb(0x1D, 0x4E, 0xD8));
        Set("BrushBtnPrimaryFg", Colors.White);

        // Success
        Set("BrushBtnSuccessBg", Color.FromRgb(0x19, 0x87, 0x54));
        Set("BrushBtnSuccessHover", Color.FromRgb(0x20, 0xA0, 0x64));
        Set("BrushBtnSuccessPressed", Color.FromRgb(0x14, 0x6C, 0x43));
        Set("BrushBtnSuccessFg", Colors.White);

        // SecondaryBlue — claramente azul vs card/fondo (NO gris)
        Set("BrushBtnSecondaryBg", Color.FromRgb(0x24, 0x4A, 0x78));
        Set("BrushBtnSecondaryHover", Color.FromRgb(0x2F, 0x5F, 0x96));
        Set("BrushBtnSecondaryPressed", Color.FromRgb(0x1B, 0x3A, 0x60));
        Set("BrushBtnSecondaryFg", Color.FromRgb(0xE8, 0xF4, 0xFF));
        Set("BrushBtnSecondaryBorder", Color.FromArgb(0xCC, 0x3B, 0xA4, 0xFF));

        // Neutral — gris elevado, sin tinte azul
        Set("BrushBtnNeutralBg", Color.FromRgb(0x2A, 0x33, 0x42));
        Set("BrushBtnNeutralHover", Color.FromRgb(0x36, 0x41, 0x52));
        Set("BrushBtnNeutralFg", Color.FromRgb(0xD0, 0xD8, 0xE4));
        Set("BrushBtnNeutralBorder", Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));

        // Tertiary
        Set("BrushBtnTertiaryHover", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        Set("BrushBtnTertiaryFg", Color.FromRgb(0x9A, 0xA8, 0xBC));
        Set("BrushBtnTertiaryBorder", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        // Danger
        Set("BrushBtnDangerBg", Color.FromArgb(0x55, 0xC4, 0x28, 0x28));
        Set("BrushBtnDangerHover", Color.FromArgb(0x77, 0xE0, 0x35, 0x35));
        Set("BrushBtnDangerFg", Color.FromRgb(0xFF, 0xD0, 0xD0));
        Set("BrushBtnDangerBorder", Color.FromArgb(0xAA, 0xFF, 0x5C, 0x5C));

        // Warning
        Set("BrushBtnWarningBg", Color.FromArgb(0x55, 0xB8, 0x7A, 0x00));
        Set("BrushBtnWarningHover", Color.FromArgb(0x77, 0xD4, 0x90, 0x00));
        Set("BrushBtnWarningFg", Color.FromRgb(0xFF, 0xD1, 0x66));
        Set("BrushBtnWarningBorder", Color.FromArgb(0xBB, 0xF0, 0xA2, 0x02));

        // Filter chips
        Set("BrushBtnFilterBg", Color.FromRgb(0x1A, 0x22, 0x30));
        Set("BrushBtnFilterHover", Color.FromRgb(0x22, 0x3A, 0x55));
        Set("BrushBtnFilterBorder", Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        Set("BrushBtnFilterFg", Color.FromRgb(0xB8, 0xC6, 0xD8));
        Set("BrushBtnFilterActiveBg", Color.FromArgb(0x66, 0x25, 0x63, 0xEB));
        Set("BrushBtnFilterActiveBorder", Color.FromRgb(0x3B, 0xA4, 0xFF));
        Set("BrushBtnFilterActiveFg", Color.FromRgb(0xFF, 0xFF, 0xFF));

        // Action chips — más azul que Filter
        Set("BrushBtnActionBg", Color.FromArgb(0x55, 0x25, 0x63, 0xEB));
        Set("BrushBtnActionHover", Color.FromArgb(0x77, 0x25, 0x63, 0xEB));
        Set("BrushBtnActionFg", Color.FromRgb(0xD6, 0xEB, 0xFF));
        Set("BrushBtnActionBorder", Color.FromArgb(0xCC, 0x3B, 0xA4, 0xFF));

        // Sort — ámbar control
        Set("BrushBtnSortBg", Color.FromRgb(0x2A, 0x28, 0x1A));
        Set("BrushBtnSortHover", Color.FromRgb(0x3A, 0x32, 0x18));
        Set("BrushBtnSortFg", Color.FromRgb(0xE8, 0xD4, 0xA0));
        Set("BrushBtnSortBorder", Color.FromArgb(0x77, 0xF0, 0xA2, 0x02));
        Set("BrushBtnSortActiveBg", Color.FromArgb(0x66, 0xF0, 0xA2, 0x02));
        Set("BrushBtnSortActiveFg", Color.FromRgb(0xFF, 0xE0, 0x8A));
        Set("BrushBtnSortActiveBorder", Color.FromRgb(0xF0, 0xA2, 0x02));

        // Disabled
        Set("BrushBtnDisabledBg", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Set("BrushBtnDisabledFg", Color.FromRgb(0x6A, 0x78, 0x90));
        Set("BrushBtnDisabledBorder", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

        // Nav — Selected = superficie sólida permanente (claramente distinta del hover)
        Set("BrushNavActiveBg", Color.FromRgb(0x2F, 0x45, 0x66));
        Set("BrushNavActiveFg", Color.FromRgb(0xF5, 0xF8, 0xFF));
        Set("BrushNavHoverBg", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        Set("BrushNavInactiveFg", Color.FromRgb(0x8B, 0x98, 0xB0));
        Set("BrushNavActiveBar", Color.FromRgb(0x3B, 0xA4, 0xFF));
        Set("BrushNavActiveBorder", Color.FromArgb(0x66, 0x3B, 0xA4, 0xFF));

        SetGradient(
            Color.FromRgb(0x0B, 0x12, 0x20),
            Color.FromRgb(0x13, 0x20, 0x38),
            Color.FromRgb(0x1A, 0x27, 0x40));
    }

    private static void ApplyLightPalette()
    {
        Set("BrushBg", Color.FromRgb(0xF3, 0xF6, 0xFB));
        Set("BrushPanel", Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        Set("BrushAccent", Color.FromRgb(0x1F, 0x6F, 0xEB));
        Set("BrushOk", Color.FromRgb(0x0F, 0x9F, 0x6E));
        Set("BrushWarn", Color.FromRgb(0xC4, 0x7E, 0x00));
        Set("BrushDanger", Color.FromRgb(0xD9, 0x3B, 0x3B));
        Set("BrushAi", Color.FromRgb(0x6B, 0x4E, 0xD9));
        Set("BrushText", Color.FromRgb(0x14, 0x1C, 0x2B));
        Set("BrushMuted", Color.FromRgb(0x5A, 0x6A, 0x82));
        Set("BrushSecondaryText", Color.FromRgb(0x3D, 0x4B, 0x61));
        Set("BrushSurface", Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
        Set("BrushSurfaceStrong", Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
        Set("BrushSurfaceSoft", Color.FromArgb(0xCC, 0xE8, 0xEE, 0xF5));
        Set("BrushRow", Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        Set("BrushNav", Color.FromArgb(0xF0, 0xEB, 0xF0, 0xF6));
        Set("BrushFooter", Color.FromArgb(0xF5, 0xEB, 0xF0, 0xF6));
        Set("BrushBorder", Color.FromArgb(0x33, 0x14, 0x1C, 0x2B));
        Set("BrushBorderSubtle", Color.FromArgb(0x22, 0x14, 0x1C, 0x2B));
        Set("BrushHover", Color.FromArgb(0x22, 0x1F, 0x6F, 0xEB));
        Set("BrushChip", Color.FromArgb(0x28, 0x1F, 0x6F, 0xEB));
        Set("BrushInputBg", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        Set("BrushTableHeader", Color.FromArgb(0xE0, 0xE8, 0xEE, 0xF5));

        Set("BrushBtnPrimaryBg", Color.FromRgb(0x25, 0x63, 0xEB));
        Set("BrushBtnPrimaryHover", Color.FromRgb(0x3B, 0x82, 0xF6));
        Set("BrushBtnPrimaryPressed", Color.FromRgb(0x1D, 0x4E, 0xD8));
        Set("BrushBtnPrimaryFg", Colors.White);

        Set("BrushBtnSuccessBg", Color.FromRgb(0x19, 0x87, 0x54));
        Set("BrushBtnSuccessHover", Color.FromRgb(0x20, 0xA0, 0x64));
        Set("BrushBtnSuccessPressed", Color.FromRgb(0x14, 0x6C, 0x43));
        Set("BrushBtnSuccessFg", Colors.White);

        Set("BrushBtnSecondaryBg", Color.FromRgb(0xD6, 0xE8, 0xFF));
        Set("BrushBtnSecondaryHover", Color.FromRgb(0xBF, 0xDB, 0xFE));
        Set("BrushBtnSecondaryPressed", Color.FromRgb(0xA8, 0xCE, 0xFC));
        Set("BrushBtnSecondaryFg", Color.FromRgb(0x0B, 0x2F, 0x66));
        Set("BrushBtnSecondaryBorder", Color.FromRgb(0x3B, 0x82, 0xF6));

        Set("BrushBtnNeutralBg", Color.FromRgb(0xE2, 0xE6, 0xEC));
        Set("BrushBtnNeutralHover", Color.FromRgb(0xD4, 0xDA, 0xE2));
        Set("BrushBtnNeutralFg", Color.FromRgb(0x24, 0x30, 0x42));
        Set("BrushBtnNeutralBorder", Color.FromArgb(0x55, 0x14, 0x1C, 0x2B));

        Set("BrushBtnTertiaryHover", Color.FromRgb(0xE2, 0xE6, 0xEC));
        Set("BrushBtnTertiaryFg", Color.FromRgb(0x5A, 0x6A, 0x82));
        Set("BrushBtnTertiaryBorder", Color.FromArgb(0x44, 0x14, 0x1C, 0x2B));

        Set("BrushBtnDangerBg", Color.FromRgb(0xFE, 0xE2, 0xE2));
        Set("BrushBtnDangerHover", Color.FromRgb(0xFE, 0xCA, 0xCA));
        Set("BrushBtnDangerFg", Color.FromRgb(0x99, 0x1B, 0x1B));
        Set("BrushBtnDangerBorder", Color.FromRgb(0xEF, 0x44, 0x44));

        Set("BrushBtnWarningBg", Color.FromRgb(0xFE, 0xF3, 0xC7));
        Set("BrushBtnWarningHover", Color.FromRgb(0xFD, 0xE6, 0x8A));
        Set("BrushBtnWarningFg", Color.FromRgb(0x78, 0x50, 0x00));
        Set("BrushBtnWarningBorder", Color.FromRgb(0xE0, 0xA0, 0x00));

        Set("BrushBtnFilterBg", Color.FromRgb(0xE8, 0xEC, 0xF2));
        Set("BrushBtnFilterHover", Color.FromRgb(0xDB, 0xEA, 0xFE));
        Set("BrushBtnFilterBorder", Color.FromArgb(0x55, 0x14, 0x1C, 0x2B));
        Set("BrushBtnFilterFg", Color.FromRgb(0x3D, 0x4B, 0x61));
        Set("BrushBtnFilterActiveBg", Color.FromRgb(0xBF, 0xDB, 0xFE));
        Set("BrushBtnFilterActiveBorder", Color.FromRgb(0x25, 0x63, 0xEB));
        Set("BrushBtnFilterActiveFg", Color.FromRgb(0x0B, 0x2F, 0x66));

        Set("BrushBtnActionBg", Color.FromRgb(0xBF, 0xDB, 0xFE));
        Set("BrushBtnActionHover", Color.FromRgb(0x93, 0xC5, 0xFD));
        Set("BrushBtnActionFg", Color.FromRgb(0x1E, 0x40, 0xAF));
        Set("BrushBtnActionBorder", Color.FromRgb(0x3B, 0x82, 0xF6));

        Set("BrushBtnSortBg", Color.FromRgb(0xFE, 0xF3, 0xC7));
        Set("BrushBtnSortHover", Color.FromRgb(0xFD, 0xE6, 0x8A));
        Set("BrushBtnSortFg", Color.FromRgb(0x78, 0x50, 0x00));
        Set("BrushBtnSortBorder", Color.FromRgb(0xE0, 0xA0, 0x00));
        Set("BrushBtnSortActiveBg", Color.FromRgb(0xFD, 0xE6, 0x8A));
        Set("BrushBtnSortActiveFg", Color.FromRgb(0x78, 0x50, 0x00));
        Set("BrushBtnSortActiveBorder", Color.FromRgb(0xC4, 0x7E, 0x00));

        Set("BrushBtnDisabledBg", Color.FromRgb(0xF0, 0xF2, 0xF5));
        Set("BrushBtnDisabledFg", Color.FromRgb(0x9A, 0xA5, 0xB5));
        Set("BrushBtnDisabledBorder", Color.FromArgb(0x22, 0x14, 0x1C, 0x2B));

        Set("BrushNavActiveBg", Color.FromRgb(0xB8, 0xC8, 0xE0));
        Set("BrushNavActiveFg", Color.FromRgb(0x0C, 0x14, 0x22));
        Set("BrushNavHoverBg", Color.FromRgb(0xE2, 0xE8, 0xF0));
        Set("BrushNavInactiveFg", Color.FromRgb(0x5A, 0x6A, 0x82));
        Set("BrushNavActiveBar", Color.FromRgb(0x1F, 0x6F, 0xEB));
        Set("BrushNavActiveBorder", Color.FromArgb(0x88, 0x1F, 0x6F, 0xEB));

        SetGradient(
            Color.FromRgb(0xEE, 0xF2, 0xF8),
            Color.FromRgb(0xE4, 0xEB, 0xF5),
            Color.FromRgb(0xD9, 0xE3, 0xF2));
    }
}
