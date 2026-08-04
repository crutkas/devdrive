using System.Collections.Immutable;

namespace DevDriveStorage;

/// <summary>What a segment of a volume's capacity bar represents.</summary>
public enum CapacitySegmentKind
{
    /// <summary>Space in use that we have no better claim about.</summary>
    Used,

    /// <summary>Space in use that a reclaim scan says can be given back.</summary>
    Reclaimable,
}

/// <summary>
/// One band of a capacity bar, as a fraction of the whole volume.
/// </summary>
/// <param name="Kind">What the band represents.</param>
/// <param name="Fraction">Share of total capacity, 0..1.</param>
/// <param name="Bytes">The band's size, kept alongside the fraction so a caller can label it.</param>
public readonly record struct CapacitySegment(CapacitySegmentKind Kind, double Fraction, long Bytes);

/// <summary>
/// The capacity model behind the volume context strip.
/// </summary>
/// <remarks>
/// This lives in the UI-agnostic library and returns normalised fractions, the same split the
/// treemap uses: the geometry is computed and tested here, and WinUI only renders it.
/// <para>
/// The invariant worth stating: reclaimable space is <i>carved out of</i> used space, never added to
/// it. Learning that 150 GB is deletable does not make the volume fuller, and a bar that grew when a
/// scan finished would be actively misleading at the exact moment the user is deciding what to trust.
/// </para>
/// </remarks>
public static class VolumeCapacity
{
    /// <summary>Builds the bands for one volume.</summary>
    /// <param name="volume">The volume to describe.</param>
    /// <param name="reclaimableBytes">
    /// Bytes a reclaim scan found on this volume, or 0 when nothing has been scanned. Clamped to the
    /// used space, because a scan can finish against a volume whose free space moved underneath it
    /// and a band wider than its track reads as a rendering bug rather than a stale number.
    /// </param>
    public static VolumeCapacityResult ForVolume(StorageVolume volume, long reclaimableBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (volume.CapacityBytes <= 0)
        {
            return new VolumeCapacityResult([], 0);
        }

        double total = volume.CapacityBytes;
        long used = Math.Clamp(volume.UsedBytes, 0, volume.CapacityBytes);
        long reclaimable = Math.Clamp(reclaimableBytes, 0, used);
        long plainUsed = used - reclaimable;

        ImmutableArray<CapacitySegment>.Builder segments =
            ImmutableArray.CreateBuilder<CapacitySegment>(2);

        if (plainUsed > 0)
        {
            segments.Add(new CapacitySegment(CapacitySegmentKind.Used, plainUsed / total, plainUsed));
        }

        if (reclaimable > 0)
        {
            segments.Add(new CapacitySegment(
                CapacitySegmentKind.Reclaimable, reclaimable / total, reclaimable));
        }

        long free = Math.Clamp(volume.CapacityBytes - used, 0, volume.CapacityBytes);
        return new VolumeCapacityResult(segments.ToImmutable(), free / total);
    }

    /// <summary>
    /// The strip's second line: the filesystem, plus the one thing that makes this volume different.
    /// </summary>
    /// <param name="volume">The volume to caption.</param>
    /// <param name="isSystemVolume">
    /// Whether this is the volume Windows booted from. Passed in rather than sniffed here so the
    /// caption is deterministic under test and the library stays free of machine queries.
    /// </param>
    public static string CaptionFor(StorageVolume volume, bool isSystemVolume = false)
    {
        ArgumentNullException.ThrowIfNull(volume);

        // Only one qualifier is shown. Trusted implies Dev Drive, and a Dev Drive is never the system
        // volume, so these cannot collide — and stacking them would cost the strip its density.
        string? qualifier = volume switch
        {
            { IsDevDrive: true, IsTrusted: true } => "Trusted",
            { IsDevDrive: true } => "Dev Drive",
            _ when isSystemVolume => "System",
            _ => null,
        };

        return qualifier is null ? volume.FileSystem : $"{volume.FileSystem} · {qualifier}";
    }
}

/// <summary>The bands of one volume's capacity bar, plus whatever is left free.</summary>
/// <param name="Segments">Bands in draw order, each a fraction of total capacity.</param>
/// <param name="FreeFraction">Share of total capacity that is free, 0..1.</param>
public readonly record struct VolumeCapacityResult(
    ImmutableArray<CapacitySegment> Segments,
    double FreeFraction);
