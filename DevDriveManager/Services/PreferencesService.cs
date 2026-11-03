using DevDriveCore.Models;

namespace DevDriveManager.Services;

/// <summary>
/// Owns the app's preferences: reads them once at startup, persists every change, and tells whoever
/// is listening that something moved.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="ThemeService"/> and for the same reason — persistence goes through the
/// packaged app's <see cref="Windows.Storage.ApplicationData"/> local settings, wrapped so a missing
/// package identity (a unit-test host, an unpackaged run) degrades to in-memory rather than throwing
/// on the first read.
/// </para>
/// <para>
/// Values are clamped on the way out, not on the way in. The store is a plain dictionary that a
/// future version can leave anything in, and a preference read back out of range should be corrected
/// rather than trusted.
/// </para>
/// </remarks>
public static class PreferencesService
{
    private const string LowFreeKey = "LowFreePercent";
    private const string RollupKey = "CacheRollupThreshold";
    private const string WatchCachesKey = "WatchCachesOffDevDrive";
    private const string RecordHistoryKey = "RecordFreeSpaceHistory";
    private const string RetentionKey = "HistoryRetentionDays";
    private const string PreferResizeKey = "PreferResizeOverVhdx";

    private static AppPreferences _current = AppPreferences.Default;

    /// <summary>Raised after any preference changes, on the thread that changed it.</summary>
    public static event EventHandler? Changed;

    /// <summary>The preferences in force. Always normalized.</summary>
    public static AppPreferences Current => _current;

    /// <summary>Load the persisted preferences. Call once at startup.</summary>
    public static void Initialize() => _current = Read().Normalized();

    /// <summary>Persist a new set of preferences and notify listeners.</summary>
    public static void Update(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        AppPreferences normalized = preferences.Normalized();
        if (normalized == _current)
        {
            return;
        }

        _current = normalized;
        Write(normalized);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Persist one change without the caller having to restate the rest.</summary>
    public static void Update(Func<AppPreferences, AppPreferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Update(change(_current));
    }

    /// <summary>Restore every preference to its shipped default.</summary>
    public static void Reset() => Update(AppPreferences.Default);

    private static AppPreferences Read()
    {
        try
        {
            Windows.Storage.ApplicationDataContainer store =
                Windows.Storage.ApplicationData.Current.LocalSettings;

            return new AppPreferences
            {
                LowFreePercent = ReadInt(store, LowFreeKey, AppPreferences.Default.LowFreePercent),
                CacheRollupThreshold = ReadInt(store, RollupKey, AppPreferences.Default.CacheRollupThreshold),
                WatchCachesOffDevDrive = ReadBool(store, WatchCachesKey, AppPreferences.Default.WatchCachesOffDevDrive),
                RecordFreeSpaceHistory = ReadBool(store, RecordHistoryKey, AppPreferences.Default.RecordFreeSpaceHistory),
                HistoryRetentionDays = ReadInt(store, RetentionKey, AppPreferences.Default.HistoryRetentionDays),
                PreferResizeOverVhdx = ReadBool(store, PreferResizeKey, AppPreferences.Default.PreferResizeOverVhdx),
            };
        }
        catch
        {
            // No package identity (or settings unavailable): the shipped defaults still apply.
            return AppPreferences.Default;
        }
    }

    private static void Write(AppPreferences p)
    {
        try
        {
            Windows.Storage.ApplicationDataContainer store =
                Windows.Storage.ApplicationData.Current.LocalSettings;

            store.Values[LowFreeKey] = p.LowFreePercent;
            store.Values[RollupKey] = p.CacheRollupThreshold;
            store.Values[WatchCachesKey] = p.WatchCachesOffDevDrive;
            store.Values[RecordHistoryKey] = p.RecordFreeSpaceHistory;
            store.Values[RetentionKey] = p.HistoryRetentionDays;
            store.Values[PreferResizeKey] = p.PreferResizeOverVhdx;
        }
        catch
        {
            // Best-effort persistence; the in-memory value still applies for this session.
        }
    }

    private static int ReadInt(Windows.Storage.ApplicationDataContainer store, string key, int fallback) =>
        store.Values[key] is int value ? value : fallback;

    private static bool ReadBool(Windows.Storage.ApplicationDataContainer store, string key, bool fallback) =>
        store.Values[key] is bool value ? value : fallback;
}
