using System.Management;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IStorageQuery"/> backed by Storage WMI v2
/// (<c>root\Microsoft\Windows\Storage</c>: <c>MSFT_Volume</c> / <c>MSFT_Partition</c> /
/// <c>MSFT_Disk</c>). All three queries work unelevated.
/// </summary>
public sealed class StorageQueryWmi : IStorageQuery
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    /// <inheritdoc />
    public IReadOnlyList<StorageVolumeRecord> GetVolumes() =>
        Query(
            "SELECT DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining, DriveType, Path FROM MSFT_Volume",
            mo => new StorageVolumeRecord
            {
                DriveLetter = ToDriveLetter(mo["DriveLetter"]),
                Label = mo["FileSystemLabel"] as string ?? string.Empty,
                FileSystem = mo["FileSystem"] as string ?? string.Empty,
                SizeBytes = ToUInt64(mo["Size"]),
                FreeBytes = ToUInt64(mo["SizeRemaining"]),
                DriveType = ToUInt32(mo["DriveType"]),
                Path = mo["Path"] as string,
            });

    /// <inheritdoc />
    public IReadOnlyList<StoragePartitionRecord> GetPartitions() =>
        Query(
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition",
            mo => new StoragePartitionRecord
            {
                DriveLetter = ToDriveLetter(mo["DriveLetter"]),
                DiskNumber = ToUInt32(mo["DiskNumber"]),
            });

    /// <inheritdoc />
    public IReadOnlyList<StorageDiskRecord> GetDisks() =>
        Query(
            "SELECT Number, BusType, Location, Model, FriendlyName FROM MSFT_Disk",
            mo => new StorageDiskRecord
            {
                Number = ToUInt32(mo["Number"]),
                BusType = ToUInt16(mo["BusType"]),
                Location = mo["Location"] as string,
                Model = mo["Model"] as string,
                FriendlyName = mo["FriendlyName"] as string,
            });

    /// <summary>
    /// Runs a WQL query against the Storage scope and projects each result. The searcher, the
    /// result collection and every <see cref="ManagementBaseObject"/> are disposed before returning
    /// (all are <see cref="IDisposable"/>) so the COM/WMI handles don't linger until GC.
    /// </summary>
    private static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> project)
    {
        var results = new List<T>();
        var scope = new ManagementScope(StorageScope);
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using ManagementObjectCollection collection = searcher.Get();
        foreach (ManagementBaseObject mo in collection)
        {
            using (mo)
            {
                results.Add(project(mo));
            }
        }

        return results;
    }

    /// <summary>
    /// MSFT_* expose DriveLetter as CIM char16, which System.Management may surface as
    /// <see cref="char"/>, <see cref="ushort"/>, or a string. Returns <c>null</c> for the absent
    /// value (NUL / 0 / empty).
    /// </summary>
    private static char? ToDriveLetter(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case char c:
                return c is '\0' or ' ' ? null : char.ToUpperInvariant(c);
            case ushort us:
                return us == 0 ? null : char.ToUpperInvariant((char)us);
            case string s:
                return s.Length == 0 ? null : char.ToUpperInvariant(s[0]);
            default:
                try
                {
                    var u = Convert.ToUInt16(value);
                    return u == 0 ? null : char.ToUpperInvariant((char)u);
                }
                catch
                {
                    return null;
                }
        }
    }

    private static ulong ToUInt64(object? value)
    {
        try { return value is null ? 0UL : Convert.ToUInt64(value); }
        catch { return 0UL; }
    }

    private static uint ToUInt32(object? value)
    {
        try { return value is null ? 0U : Convert.ToUInt32(value); }
        catch { return 0U; }
    }

    private static ushort ToUInt16(object? value)
    {
        try { return value is null ? (ushort)0 : Convert.ToUInt16(value); }
        catch { return 0; }
    }
}
