using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevDriveManager;

/// <summary>
/// Centralised <c>x:Bind</c> helper functions shared by every page in the NavigationView shell.
/// The app deliberately uses no <see cref="Microsoft.UI.Xaml.Data.IValueConverter"/> — pages reference
/// these statics directly (e.g. <c>local:UiHelpers.BoolToVisibility(...)</c>), so the conversion logic
/// lives in exactly one place and stays consistent across the Dashboard, Package caches, Benchmarks,
/// Drives and Settings pages.
/// </summary>
/// <remarks>
/// The functions that return a <see cref="Style"/> are safe across a theme switch: the Style object
/// is theme-independent and its <c>{ThemeResource}</c> setters re-resolve when the theme changes.
/// The ones that return a <see cref="Brush"/> are NOT — they hand back the brush instance that was
/// live at the moment the row was realised, and nothing re-runs an <c>x:Bind</c> function binding on
/// a theme change, so the row keeps its old ink. Prefer a Style for anything themed; the remaining
/// brush helpers are legacy and should each become a Style as their room is restyled.
/// </remarks>
public static class UiHelpers
{
    /// <summary>Shows the element when <paramref name="value"/> is true.</summary>
    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Inverse of <see cref="BoolToVisibility"/> — shows the element when <paramref name="value"/> is false.</summary>
    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>True when not busy — for enabling a command button while its command runs.</summary>
    public static bool IsNotBusy(bool isBusy) => !isBusy;

    /// <summary>Boolean negation for x:Bind.</summary>
    public static bool Not(bool value) => !value;

    /// <summary>Dims a suite row's label while it is queued (matches the mock's dimmed "Queued" rows).</summary>
    public static double QueuedOpacity(bool isQueued) => isQueued ? 0.5 : 1.0;

    /// <summary>
    /// The Storage Manager status pill: an outline over a wash of its own colour rather than a solid
    /// block. On the Dev Drive is the good state; still on the system drive is the one asking for
    /// attention; undetected is muted, because "we didn't find it" is information, not a problem.
    /// <para>
    /// Returns a <see cref="Style"/>, not a <see cref="Brush"/>. A brush resolved here is whichever
    /// instance was live when the binding ran, and an <c>x:Bind</c> function binding has nothing to
    /// re-evaluate on when the theme changes — so the row would keep its old-theme colours and, going
    /// dark to light, paint near-white text onto a white page. A style's setters use
    /// <c>ThemeResource</c> and re-resolve on the theme change itself.
    /// </para>
    /// </summary>
    public static Style? PillStyle(string statusKind) => Resource<Style>(statusKind switch
    {
        "dev" => "SmPillGoodStyle",
        "system" => "SmPillWarnStyle",
        _ => "SmPillMuteStyle",
    });

    /// <summary>Text style for a Storage Manager status pill — the colour that carries the meaning.</summary>
    public static Style? PillTextStyle(string statusKind) => Resource<Style>(statusKind switch
    {
        "dev" => "SmPillGoodTextStyle",
        "system" => "SmPillWarnTextStyle",
        _ => "SmPillMuteTextStyle",
    });

    /// <summary>
    /// The swatch style for an ecosystem, from the theme's six-colour category ramp. Wraps rather than
    /// clamps so a longer tool list still gets a spread instead of a run of one colour at the end.
    /// </summary>
    public static Style? SwatchStyle(int index) =>
        Resource<Style>($"SmSwatch{((index % 6) + 6) % 6}Style");

    /// <summary>Quietens the "Redirected by" cell when nothing is actually redirecting the tool.</summary>
    public static Style? RedirectCellStyle(bool isRedirected) =>
        Resource<Style>(isRedirected ? "SmMonoCellTextStyle" : "SmMonoFaintCellTextStyle");

    /// <summary>Text style for a tab label, keyed by whether it is the selected tab.</summary>
    public static Style? TabTextStyle(bool isSelected) =>
        Resource<Style>(isSelected ? "SmTabSelectedTextStyle" : "SmTabNormalTextStyle");

    /// <summary>
    /// The border of one node in the Drives room's I/O path stack, keyed by <c>"on"</c> (a filter that
    /// runs on this volume), <c>"off"</c> (one that is skipped) or <c>"end"</c> (either end of the write).
    /// </summary>
    public static Style? StackNodeStyle(string kind) => Resource<Style>(kind switch
    {
        "on" => "SmStackNodeOnStyle",
        "end" => "SmStackNodeEndStyle",
        _ => "SmStackNodeOffStyle",
    });

    /// <summary>
    /// The title inside a stack node. A skipped filter is quietened and unbolded, so the difference
    /// between running and skipped survives high contrast and reads without relying on colour.
    /// </summary>
    public static Style? StackNodeTitleStyle(string kind) =>
        Resource<Style>(kind == "off" ? "SmStackNodeTitleFaintTextStyle" : "SmStackNodeTitleTextStyle");

    /// <summary>
    /// Text style for one status-bar fact, keyed by how strongly it should read. Returns a Style
    /// rather than a Brush so the ink re-resolves when the theme changes — see the styles themselves.
    /// </summary>
    public static Style? StatusFactStyle(Controls.StatusEmphasis emphasis) => Resource<Style>(emphasis switch
    {
        Controls.StatusEmphasis.Good => "SmStatusFactGoodStyle",
        Controls.StatusEmphasis.Warn => "SmStatusFactWarnStyle",
        Controls.StatusEmphasis.Bad => "SmStatusFactBadStyle",
        _ => "SmStatusFactStyle",
    });

    /// <summary>Outline of a Reclaim risk chip, keyed by the risk it carries.</summary>
    public static Style? RiskChipStyle(Controls.StatusEmphasis emphasis) => Resource<Style>(emphasis switch
    {
        Controls.StatusEmphasis.Good => "SmRiskChipGoodStyle",
        Controls.StatusEmphasis.Warn => "SmRiskChipWarnStyle",
        Controls.StatusEmphasis.Bad => "SmRiskChipBadStyle",
        _ => "SmRiskChipStyle",
    });

    /// <summary>Label inside a Reclaim risk chip, keyed by the risk it carries.</summary>
    public static Style? RiskChipTextStyle(Controls.StatusEmphasis emphasis) => Resource<Style>(emphasis switch
    {
        Controls.StatusEmphasis.Good => "SmRiskChipTextGoodStyle",
        Controls.StatusEmphasis.Warn => "SmRiskChipTextWarnStyle",
        Controls.StatusEmphasis.Bad => "SmRiskChipTextBadStyle",
        _ => "SmRiskChipTextStyle",
    });

    /// <summary>
    /// The disc behind an Overview signal's glyph, keyed by <c>"gain"</c>, <c>"warn"</c>,
    /// <c>"bad"</c> or <c>"info"</c>.
    /// </summary>
    public static Style? SignalDotStyle(string kind) => Resource<Style>(kind switch
    {
        "gain" => "SmSignalDotGainStyle",
        "warn" => "SmSignalDotWarnStyle",
        "bad" => "SmSignalDotBadStyle",
        _ => "SmSignalDotInfoStyle",
    });

    /// <summary>The glyph inside an Overview signal's dot, keyed by the same four kinds.</summary>
    public static Style? SignalGlyphStyle(string kind) => Resource<Style>(kind switch
    {
        "gain" => "SmSignalGlyphGainStyle",
        "warn" => "SmSignalGlyphWarnStyle",
        "bad" => "SmSignalGlyphBadStyle",
        _ => "SmSignalGlyphInfoStyle",
    });

    /// <summary>
    /// The right-aligned impact figure on an Overview row. Only a gain is coloured — see the style.
    /// </summary>
    public static Style? ImpactTextStyle(string kind) => Resource<Style>(kind switch
    {
        "gain" => "SmImpactGainTextStyle",
        "warn" => "SmImpactWarnTextStyle",
        "bad" => "SmImpactBadTextStyle",
        _ => "SmImpactTextStyle",
    });

    /// <summary>Foreground brush for a speed-test ratio: success green when the Dev Drive wins (or ties), neutral otherwise.</summary>
    public static Brush? RatioBrush(bool isFavorable) =>
        Resource(isFavorable ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");

    /// <summary>
    /// Alternating-row ("banded") background so the eye flows across a multi-row grid. The base row uses
    /// the standard card fill; the alternate row uses the secondary card fill — one subtle, theme-correct
    /// step apart in Light, Dark and High Contrast.
    /// </summary>
    public static Brush? BandBrush(bool isAlternate) =>
        Resource(isAlternate ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");

    /// <summary>
    /// Resolves a named <see cref="Brush"/> from the merged application resources.
    /// </summary>
    /// <remarks>
    /// The instance returned is the one in whichever theme dictionary is live at the moment of the
    /// call, and it does not follow later theme changes — an <c>x:Bind</c> function binding has no
    /// dependency to re-evaluate on. That is only correct where the bound element is rebuilt on a
    /// theme switch. Prefer <see cref="Resource{T}"/> with a style whose setters use
    /// <c>ThemeResource</c> for anything that must survive one.
    /// </remarks>
    public static Brush? Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : null;

    /// <summary>
    /// Resolves a named resource of any type. Used for styles, which live outside the theme
    /// dictionaries and therefore stay valid across a theme change even though the lookup is static.
    /// </summary>
    private static T? Resource<T>(string key)
        where T : class =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as T : null;
}
