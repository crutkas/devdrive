using System.Globalization;
using System.Management;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IPhysicalDiskQuery"/> backed by Storage WMI v2
/// (<c>root\Microsoft\Windows\Storage</c>: <c>MSFT_PhysicalDisk</c>), which works unelevated. Mirrors
/// the disposal discipline of <see cref="StorageQueryWmi"/>.
/// </summary>
public sealed class PhysicalDiskQueryWmi : IPhysicalDiskQuery
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    /// <inheritdoc />
    public IReadOnlyList<PhysicalDiskRecord> GetPhysicalDisks()
    {
        var results = new List<PhysicalDiskRecord>();
        var scope = new ManagementScope(StorageScope);
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT DeviceId, MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk"));
        using ManagementObjectCollection collection = searcher.Get();
        foreach (ManagementBaseObject mo in collection)
        {
            using (mo)
            {
                results.Add(new PhysicalDiskRecord
                {
                    DeviceId = ParseDeviceId(mo["DeviceId"]),
                    MediaType = ToUInt16(mo["MediaType"]),
                    BusType = ToUInt16(mo["BusType"]),
                    FriendlyName = mo["FriendlyName"] as string,
                });
            }
        }

        return results;
    }

    /// <summary><c>MSFT_PhysicalDisk.DeviceId</c> is a string (e.g. "0"); parse it to an index for matching disk numbers.</summary>
    private static uint ParseDeviceId(object? value)
    {
        switch (value)
        {
            case null:
                return 0U;
            case string s:
                return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0U;
            default:
                try { return Convert.ToUInt32(value); }
                catch { return 0U; }
        }
    }

    private static ushort ToUInt16(object? value)
    {
        try { return value is null ? (ushort)0 : Convert.ToUInt16(value); }
        catch { return 0; }
    }
}
