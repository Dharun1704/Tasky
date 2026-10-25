using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tasky;

/// <summary>
/// The dock's colours, as application resources ("T.*") that its XAML binds to dynamically, so
/// switching between dark and light restyles everything in place.
/// </summary>
internal static class Theme
{
    public static bool IsLight { get; private set; }

    /// <summary>Whether Windows' taskbar and Start use the light theme ("Choose your Windows mode").</summary>
    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Applies "Dark", "Light" or "System".</summary>
    public static void Apply(string mode)
    {
        IsLight = mode switch { "Light" => true, "Dark" => false, _ => SystemIsLight() };
        var r = Application.Current.Resources;
        void Set(string key, uint argb) => r[key] = Frozen(argb);

        if (IsLight)
        {
            r["T.PillColor"] = Color.FromRgb(0xF3, 0xF3, 0xF3);
            Set("T.Fg", 0xFF1A1A1A);
            Set("T.Glyph", 0xE61A1A1A);
            Set("T.Sub", 0xB31A1A1A);
            Set("T.Faint", 0x991A1A1A);
            Set("T.PinGlyph", 0xCC1A1A1A);
            Set("T.Stroke", 0x29000000);
            Set("T.Sep", 0x2E000000);
            Set("T.Divider", 0x24000000);
            Set("T.Hover", 0x17000000);
            Set("T.ItemHover", 0x12000000);
            Set("T.Indicator", 0x99000000);
            Set("T.CapsuleBg", 0x0F000000);
            Set("T.CapsuleStroke", 0x14000000);
            Set("T.MenuBg", 0xF7F9F9F9);
            Set("T.MenuStroke", 0x24000000);
            Set("T.MenuHover", 0x14000000);
            Set("T.MenuSep", 0x1F000000);
            Set("T.LabelBg", 0xF5FBFBFB);
            Set("T.PreviewBg", 0xFFF3F3F3);
        }
        else
        {
            r["T.PillColor"] = Color.FromRgb(0x20, 0x20, 0x20);
            Set("T.Fg", 0xFFFFFFFF);
            Set("T.Glyph", 0xE6FFFFFF);
            Set("T.Sub", 0xB3FFFFFF);
            Set("T.Faint", 0x99FFFFFF);
            Set("T.PinGlyph", 0xCCFFFFFF);
            Set("T.Stroke", 0x2EFFFFFF);
            Set("T.Sep", 0x33FFFFFF);
            Set("T.Divider", 0x26FFFFFF);
            Set("T.Hover", 0x1FFFFFFF);
            Set("T.ItemHover", 0x14FFFFFF);
            Set("T.Indicator", 0xB3FFFFFF);
            Set("T.CapsuleBg", 0x12FFFFFF);
            Set("T.CapsuleStroke", 0x14FFFFFF);
            Set("T.MenuBg", 0xF2262626);
            Set("T.MenuStroke", 0x33FFFFFF);
            Set("T.MenuHover", 0x26FFFFFF);
            Set("T.MenuSep", 0x26FFFFFF);
            Set("T.LabelBg", 0xEB262626);
            Set("T.PreviewBg", 0xFF242424);
        }
    }

    static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}
