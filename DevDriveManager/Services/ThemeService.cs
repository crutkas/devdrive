using Microsoft.UI.Xaml;

namespace DevDriveManager.Services;

/// <summary>
/// Owns the app's theme override (System / Light / Dark) — the one appearance setting the redesign keeps
/// (there is deliberately no comfortable/compact density toggle; the app "just works"). The choice is
/// persisted across launches and applied to the live window's root element, so it takes effect immediately
/// and survives a restart.
/// </summary>
/// <remarks>
/// Persistence uses the packaged app's <see cref="Windows.Storage.ApplicationData"/> local settings, wrapped
/// in try/catch so a missing package identity (e.g. a unit-test host) degrades to in-memory rather than
/// throwing. <see cref="ElementTheme.Default"/> means "follow the OS".
/// </remarks>
public static class ThemeService
{
    private const string SettingKey = "AppThemeMode";

    /// <summary>The current theme override. <see cref="ElementTheme.Default"/> follows the OS.</summary>
    public static ElementTheme Mode { get; private set; } = ElementTheme.Default;

    /// <summary>Load the persisted theme. Call once at startup before the window content is shown.</summary>
    public static void Initialize()
    {
        Mode = Read();
    }

    /// <summary>Persist and apply a new theme override.</summary>
    public static void SetMode(ElementTheme theme)
    {
        Mode = theme;
        Write(theme);
        Apply();
    }

    /// <summary>Apply the current theme to the live window's root element.</summary>
    public static void Apply()
    {
        if (App.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Mode;
        }
    }

    private static ElementTheme Read()
    {
        try
        {
            object? value = Windows.Storage.ApplicationData.Current.LocalSettings.Values[SettingKey];
            if (value is string text && Enum.TryParse(text, out ElementTheme parsed))
            {
                return parsed;
            }
        }
        catch
        {
            // No package identity (or settings unavailable): fall back to following the OS.
        }

        return ElementTheme.Default;
    }

    private static void Write(ElementTheme theme)
    {
        try
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[SettingKey] = theme.ToString();
        }
        catch
        {
            // Best-effort persistence; an in-memory override still applies for this session.
        }
    }
}
