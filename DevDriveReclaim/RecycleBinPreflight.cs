using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DevDriveReclaim;

/// <summary>Whether the shell will put an item in the Recycle Bin or delete it outright.</summary>
public enum RecycleBinVerdict
{
    /// <summary>The volume has a working bin with room for the item.</summary>
    WillRecycle,

    /// <summary>The shell would delete the item permanently.</summary>
    WillNotRecycle,

    /// <summary>The settings could not be read. The shell is still asked to warn before nuking.</summary>
    Unknown,
}

/// <summary>The answer for one item, with the reason worded for the confirmation and the row.</summary>
public sealed record RecycleBinCheck(RecycleBinVerdict Verdict, string Reason)
{
    public static readonly RecycleBinCheck Recycles = new(RecycleBinVerdict.WillRecycle, string.Empty);

    /// <summary>True when recycling this item would in fact delete it permanently.</summary>
    public bool WillDeletePermanently => Verdict == RecycleBinVerdict.WillNotRecycle;
}

/// <summary>
/// The per-volume settings that decide whether <c>FOF_ALLOWUNDO</c> is honoured.
/// </summary>
/// <param name="VolumeRoot">E.g. <c>C:\</c>.</param>
/// <param name="HasRecycleBin">False for removable, network and other volumes the shell never recycles on.</param>
/// <param name="RecyclingDisabled">The bin is set to delete immediately, by the user or by policy.</param>
/// <param name="MaxCapacityBytes">The bin's size limit, or null when Windows has not recorded one.</param>
public sealed record RecycleBinVolumeSettings(
    string VolumeRoot,
    bool HasRecycleBin,
    bool RecyclingDisabled,
    long? MaxCapacityBytes);

/// <summary>
/// Predicts, before anything is removed, whether the shell will recycle an item or silently delete
/// it permanently.
/// </summary>
/// <remarks>
/// <para>
/// <c>FOF_ALLOWUNDO</c> is a request. An item larger than the bin's limit, a volume whose bin is set
/// to "don't move files to the Recycle Bin", a policy that disables recycling, or a volume type that
/// has no bin at all are each deleted outright, and the call still reports success. The confirmation
/// promises restorability per item, so that promise has to be checked against these settings
/// <em>before</em> it is made — and again immediately before the shell call, because settings can
/// change while the dialog is open.
/// </para>
/// <para>
/// The settings are the ones Recycle Bin Properties writes:
/// <c>HKCU\…\Explorer\BitBucket\Volume\{volume GUID}</c> holds <c>MaxCapacity</c> (MB) and
/// <c>NukeOnDelete</c>, and the <c>NoRecycleFiles</c> policy turns recycling off everywhere. This is a
/// prediction, not a guarantee — the shell can still decline for reasons it does not publish — which
/// is why the executor also asks the shell to warn before any permanent delete it did not plan.
/// </para>
/// </remarks>
public static class RecycleBinPreflight
{
    private const string BitBucketVolumes = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";
    private const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    /// <summary>Applies <paramref name="settings"/> to an item of <paramref name="sizeBytes"/>.</summary>
    public static RecycleBinCheck Evaluate(long sizeBytes, RecycleBinVolumeSettings? settings)
    {
        if (settings is null)
        {
            return new RecycleBinCheck(
                RecycleBinVerdict.Unknown, "the Recycle Bin settings could not be read");
        }

        string volume = settings.VolumeRoot.TrimEnd('\\');

        if (!settings.HasRecycleBin)
        {
            return new RecycleBinCheck(RecycleBinVerdict.WillNotRecycle, $"{volume} has no Recycle Bin");
        }

        if (settings.RecyclingDisabled)
        {
            return new RecycleBinCheck(
                RecycleBinVerdict.WillNotRecycle, $"the Recycle Bin is turned off on {volume}");
        }

        if (settings.MaxCapacityBytes is long max && sizeBytes > max)
        {
            return new RecycleBinCheck(
                RecycleBinVerdict.WillNotRecycle,
                $"too large for the Recycle Bin on {volume}, which holds up to " +
                DevDriveCore.ByteSizeFormatter.Format((ulong)Math.Max(0, max)));
        }

        return RecycleBinCheck.Recycles;
    }

    /// <summary>
    /// A checker that reads each volume's settings at most once. Create one per confirmation or run:
    /// the settings are cheap to read but are read per root, and a selection can hold hundreds.
    /// </summary>
    public static Func<ReclaimCandidate, RecycleBinCheck> CreateChecker(
        Func<string, RecycleBinVolumeSettings?>? readSettings = null)
    {
        Func<string, RecycleBinVolumeSettings?> read = readSettings ?? ReadSettings;
        var cache = new Dictionary<string, RecycleBinVolumeSettings?>(StringComparer.OrdinalIgnoreCase);

        return candidate =>
        {
            ArgumentNullException.ThrowIfNull(candidate);

            string root = candidate.VolumeRoot;
            if (!cache.TryGetValue(root, out RecycleBinVolumeSettings? settings))
            {
                settings = read(root);
                cache[root] = settings;
            }

            return Evaluate(candidate.SizeBytes, settings);
        };
    }

    /// <summary>Reads this machine's Recycle Bin settings for <paramref name="volumeRoot"/>. Read-only.</summary>
    /// <returns>Null when they cannot be determined.</returns>
    public static RecycleBinVolumeSettings? ReadSettings(string volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            return null;
        }

        try
        {
            string root = volumeRoot.EndsWith('\\') ? volumeRoot : volumeRoot + '\\';

            DriveType type = new DriveInfo(root).DriveType;
            if (type is DriveType.Unknown or DriveType.NoRootDirectory)
            {
                return null;
            }

            if (type != DriveType.Fixed)
            {
                return new RecycleBinVolumeSettings(root, HasRecycleBin: false, RecyclingDisabled: false, null);
            }

            bool policyOff = PolicySaysNoRecycle(Registry.CurrentUser) || PolicySaysNoRecycle(Registry.LocalMachine);

            bool nuke = false;
            long? max = null;
            if (TryGetVolumeGuid(root) is string guid)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{BitBucketVolumes}\{guid}");
                if (key is not null)
                {
                    nuke = ReadDword(key, "NukeOnDelete") == 1;
                    if (ReadDword(key, "MaxCapacity") is int megabytes and > 0)
                    {
                        max = megabytes * 1024L * 1024L;
                    }
                }
            }

            return new RecycleBinVolumeSettings(root, HasRecycleBin: true, policyOff || nuke, max);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool PolicySaysNoRecycle(RegistryKey hive)
    {
        using RegistryKey? key = hive.OpenSubKey(ExplorerPolicies);
        return key is not null && ReadDword(key, "NoRecycleFiles") == 1;
    }

    private static int? ReadDword(RegistryKey key, string name) =>
        key.GetValue(name) is int value ? value : null;

    /// <summary>"{GUID}" for a mounted volume root, the form the BitBucket keys are named with.</summary>
    private static string? TryGetVolumeGuid(string root)
    {
        char[] buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(root, buffer, buffer.Length))
        {
            return null;
        }

        // \\?\Volume{xxxxxxxx-...}\
        string name = new string(buffer).TrimEnd('\0');
        int open = name.IndexOf('{');
        int close = name.IndexOf('}');
        return open >= 0 && close > open ? name[open..(close + 1)] : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint, [Out] char[] volumeName, int bufferLength);
}
