using DevDriveReclaim;

namespace DevDriveCore.Tests;

/// <summary>
/// Recycle Bin settings for tests, so nothing here depends on how the developer's own bins are set.
/// </summary>
internal static class TestBins
{
    /// <summary>A bin that takes anything.</summary>
    public static RecycleBinVolumeSettings? Recycles(string volumeRoot) =>
        new(volumeRoot, HasRecycleBin: true, RecyclingDisabled: false, MaxCapacityBytes: null);

    /// <summary>A bin that holds at most <paramref name="maxBytes"/>.</summary>
    public static Func<string, RecycleBinVolumeSettings?> HoldsUpTo(long maxBytes) =>
        root => new(root, HasRecycleBin: true, RecyclingDisabled: false, MaxCapacityBytes: maxBytes);
}
