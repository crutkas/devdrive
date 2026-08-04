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
/// The pill / ratio / drive-glyph / band brushes are resolved imperatively against the live
/// <see cref="Application.Resources"/> so they pick up the current theme dictionary. Rows are rebuilt on
/// a theme switch (the shell reloads), so a OneTime/OneWay bind to these is correct after a reload.
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
    /// Background brush for a package-cache status pill, keyed by status. Dev Drive = the user's bright
    /// base accent; system drive = a darker accent shade (one accent family, never grey) so the Dev Drive
    /// reads as the brighter one; not-found = a quiet neutral.
    /// </summary>
    public static Brush? PillBackground(string statusKind) => Resource(statusKind switch
    {
        "dev" => "PerfDevDriveBarBrush",
        "system" => "PerfBaselineBarBrush",
        _ => "ControlFillColorSecondaryBrush",
    });

    /// <summary>Foreground brush for a package-cache status pill, keyed by status (paired for contrast on each fill).</summary>
    public static Brush? PillForeground(string statusKind) => Resource(statusKind switch
    {
        "dev" => "PerfDevDriveForegroundBrush",
        "system" => "PerfBaselineForegroundBrush",
        _ => "TextFillColorSecondaryBrush",
    });

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
    /// Themed foreground for a volume's header glyph: accent for a Dev Drive, the default text color for a
    /// normal volume. Returns a resolved brush in both cases (a <c>null</c> Foreground would render the
    /// icon transparent rather than inheriting).
    /// </summary>
    public static Brush? DriveGlyphBrush(bool isDevDrive) =>
        Resource(isDevDrive ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    /// <summary>Foreground brush for a speed-test ratio: success green when the Dev Drive wins (or ties), neutral otherwise.</summary>
    public static Brush? RatioBrush(bool isFavorable) =>
        Resource(isFavorable ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");

    /// <summary>Cache overview accent: informational without a Dev Drive, caution when movable, success otherwise.</summary>
    public static Brush? CacheOverviewBrush(
        bool hasDevDrive,
        bool hasCachesOnSystemDrive,
        bool hasDetectedCaches,
        bool allDetectedCachesOnDevDrive) =>
        Resource(!hasDevDrive
            ? "AccentTextFillColorPrimaryBrush"
            : hasCachesOnSystemDrive
                ? "SystemFillColorCautionBrush"
                : hasDetectedCaches
                    ? allDetectedCachesOnDevDrive
                        ? "SystemFillColorSuccessBrush"
                        : "SystemFillColorCautionBrush"
                    : "TextFillColorSecondaryBrush");

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
