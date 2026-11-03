#if DEBUG
using DevDriveStorage;

namespace DevDriveManager.Services;

/// <summary>Opt-in, synthetic picker data for read-only Space UI regression tests.</summary>
internal sealed class SpaceUiFixtureVolumes : IVolumeProvider
{
    public IReadOnlyList<StorageVolume> GetFixedVolumes() =>
    [
        Volume("baseline", "A:", "Synthetic complete"),
        Volume("partial", "B:", "Synthetic partial"),
        Volume("failure", "C:", "Synthetic failure"),
        Volume("scanning", "D:", "Synthetic slow scan"),
        Volume("large", "E:", "Synthetic large tree"),
        Volume("empty", "F:", "Synthetic unknown capacity") with { IsSizeKnown = false },
    ];

    private static StorageVolume Volume(string scenario, string letter, string label) =>
        new(scenario, letter, label, "Synthetic", 1_000_000_000, 250_000_000, false, false, false);
}
#endif
