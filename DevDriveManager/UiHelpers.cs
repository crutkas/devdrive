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
    /// Themed foreground for a volume's header glyph: accent for a Dev Drive, the default text color for a
    /// normal volume. Returns a resolved brush in both cases (a <c>null</c> Foreground would render the
    /// icon transparent rather than inheriting).
    /// </summary>
    public static Brush? DriveGlyphBrush(bool isDevDrive) =>
        Resource(isDevDrive ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

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

    /// <summary>Resolves a named <see cref="Brush"/> from the merged application resources for the current theme.</summary>
    public static Brush? Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : null;
}
