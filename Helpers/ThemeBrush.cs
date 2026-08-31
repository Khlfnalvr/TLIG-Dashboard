using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TLIGDashboard.Helpers;

/// <summary>
/// Resolves Fluent theme brushes (TextFillColorPrimaryBrush, CardBackground…,
/// SystemFillColor…) for a <b>specific</b> <see cref="ElementTheme"/>.
///
/// <para>Why this exists: the app applies its light/dark choice on the window's
/// root element (<c>Content.RequestedTheme</c>) — see <c>MainWindow.ApplyTheme</c>
/// — and never touches <c>Application.RequestedTheme</c>. Because of that, looking
/// a theme brush up through <c>Application.Current.Resources[key]</c> always
/// returns the variant for the app's <i>startup/system</i> theme, which is wrong
/// the moment the user toggles the in-app theme. UI that is built in code-behind
/// and snapshots a brush that way ends up white-on-white (light mode) or
/// dark-on-dark, i.e. invisible.</para>
///
/// <para><see cref="Get"/> returns the brush that matches the live
/// <c>ActualTheme</c> of the page instead, so code-built content stays legible in
/// both themes. Call it fresh whenever content is (re)built, and rebuild that
/// content on <c>ActualThemeChanged</c>.</para>
/// </summary>
internal static class ThemeBrush
{
    /// <summary>Fluent theme brush <paramref name="key"/> resolved for <paramref name="theme"/>.</summary>
    public static Brush Get(string key, ElementTheme theme)
    {
        // Deterministic first: exact Windows App SDK theme values for the keys this
        // app uses. This does NOT depend on how the merged resource dictionaries are
        // structured at runtime (walking those proved unreliable — it could hand back
        // the dark variant for a light request), so a light request always yields the
        // light colour and vice-versa.
        if (Fallback(key, theme) is Brush fb)
            return fb;

        // Unknown key (e.g. an accent-derived brush not in the table above): resolve
        // it for the requested theme from the merged theme dictionaries…
        string wanted = theme == ElementTheme.Light ? "Light" : "Default";
        if (FindThemedBrush(Application.Current.Resources, key, wanted) is Brush b)
            return b;

        // …and if even that fails, whatever the app resources hold.
        return (Brush)Application.Current.Resources[key];
    }

    // ── Resource-dictionary walk ──────────────────────────────────────────────
    private static Brush? FindThemedBrush(ResourceDictionary dict, string key, string themeKey)
    {
        var themed = dict.ThemeDictionaries;
        if (themed is not null)
        {
            if (TryTheme(themed, themeKey, key) is Brush b) return b;
            // A few dictionaries key the dark variant as "Dark" rather than "Default".
            if (themeKey == "Default" && TryTheme(themed, "Dark", key) is Brush b2) return b2;
        }

        foreach (var md in dict.MergedDictionaries)
            if (FindThemedBrush(md, key, themeKey) is Brush b) return b;

        return null;
    }

    private static Brush? TryTheme(IDictionary<object, object> themeDicts, string themeKey, string key)
    {
        if (themeDicts.TryGetValue(themeKey, out var o) &&
            o is ResourceDictionary rd &&
            rd.TryGetValue(key, out var v) &&
            v is Brush b)
            return b;
        return null;
    }

    // ── Hardcoded Fluent values (Windows App SDK) ─────────────────────────────
    private static Brush? Fallback(string key, ElementTheme theme)
    {
        bool dark = theme != ElementTheme.Light;
        static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
        Brush S(Color light, Color darkC) => new SolidColorBrush(dark ? darkC : light);

        return key switch
        {
            "TextFillColorPrimaryBrush"              => S(C(0xE4, 0, 0, 0),          C(0xFF, 0xFF, 0xFF, 0xFF)),
            "TextFillColorSecondaryBrush"            => S(C(0x9E, 0, 0, 0),          C(0xC5, 0xFF, 0xFF, 0xFF)),
            "TextFillColorTertiaryBrush"             => S(C(0x72, 0, 0, 0),          C(0x87, 0xFF, 0xFF, 0xFF)),
            "TextFillColorDisabledBrush"             => S(C(0x5C, 0, 0, 0),          C(0x5D, 0xFF, 0xFF, 0xFF)),

            "CardBackgroundFillColorDefaultBrush"    => S(C(0xB3, 0xFF, 0xFF, 0xFF), C(0x0D, 0xFF, 0xFF, 0xFF)),
            "CardBackgroundFillColorSecondaryBrush"  => S(C(0x80, 0xF6, 0xF6, 0xF6), C(0x08, 0xFF, 0xFF, 0xFF)),
            "CardStrokeColorDefaultBrush"            => S(C(0x0F, 0, 0, 0),          C(0x19, 0, 0, 0)),

            "SubtleFillColorSecondaryBrush"          => S(C(0x09, 0, 0, 0),          C(0x0F, 0xFF, 0xFF, 0xFF)),
            "SubtleFillColorTertiaryBrush"           => S(C(0x06, 0, 0, 0),          C(0x0A, 0xFF, 0xFF, 0xFF)),

            "SystemFillColorSuccessBrush"            => S(C(0xFF, 0x0F, 0x7B, 0x0F), C(0xFF, 0x6C, 0xCB, 0x5F)),
            "SystemFillColorSuccessBackgroundBrush"  => S(C(0xFF, 0xDF, 0xF6, 0xDD), C(0xFF, 0x39, 0x3D, 0x1B)),
            "SystemFillColorCautionBrush"            => S(C(0xFF, 0x9D, 0x5D, 0x00), C(0xFF, 0xFC, 0xE1, 0x00)),
            "SystemFillColorCautionBackgroundBrush"  => S(C(0xFF, 0xFF, 0xF4, 0xCE), C(0xFF, 0x43, 0x35, 0x19)),
            "SystemFillColorCriticalBrush"           => S(C(0xFF, 0xC4, 0x2B, 0x1C), C(0xFF, 0xFF, 0x99, 0xA4)),
            "SystemFillColorCriticalBackgroundBrush" => S(C(0xFF, 0xFD, 0xE7, 0xE9), C(0xFF, 0x44, 0x27, 0x26)),

            // Accent variants depend on the system accent colour; the resource walk
            // above handles the exact value. These are safe, readable defaults.
            "AccentTextFillColorPrimaryBrush"        => S(C(0xFF, 0x00, 0x3E, 0x92), C(0xFF, 0x99, 0xEB, 0xFF)),
            "AccentFillColorDefaultBrush"            => S(C(0xFF, 0x00, 0x5F, 0xB8), C(0xFF, 0x60, 0xCD, 0xFF)),

            _ => null
        };
    }
}
