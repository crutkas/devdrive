using System.Collections.Immutable;

namespace DevDriveStorage;

/// <summary>What a segment of a volume's capacity bar represents.</summary>
public enum CapacitySegmentKind
{
    /// <summary>Space in use that we have no better claim about.</summary>
    Used,

    /// <summary>Space in use that a reclaim scan says can be given back.</summary>
    Reclaimable,

    /// <summary>
    /// Space the current selection would hand back. Distinct from <see cref="Reclaimable"/> because
    /// the two answer different questions — what <i>could</i> go versus what the user has actually
    /// ticked — and a bar showing the after-picture must not colour them the same.
    /// </summary>
    Freed,

    /// <summary>Regenerable with no decision to make.</summary>
    Safe,

    /// <summary>Almost certainly fine, but worth a glance.</summary>
    Check,

    /// <summary>Can lose work that exists nowhere else.</summary>
    Careful,
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
    /// The after-picture: one bar showing what the volume looks like once the current selection is
    /// reclaimed.
    /// </summary>
    /// <param name="volume">The volume as it stands now.</param>
    /// <param name="freedBytes">
    /// Bytes the current selection hands back on this volume. Clamped to the used space for the same
    /// reason <see cref="ForVolume"/> clamps: free space can move underneath a finished scan, and a
    /// band wider than its track reads as a rendering bug rather than a stale number.
    /// </param>
    /// <remarks>
    /// The freed band sits between the space still in use and the space that was already free, which
    /// is where it physically belongs — it is the slice about to change sides. Two separate before
    /// and after bars would make the reader diff two pictures; one bar with the delta highlighted
    /// is the same information without the diffing.
    /// </remarks>
    public static VolumeCapacityResult AfterReclaim(StorageVolume volume, long freedBytes)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (volume.CapacityBytes <= 0)
        {
            return new VolumeCapacityResult([], 0);
        }

        double total = volume.CapacityBytes;
        long used = Math.Clamp(volume.UsedBytes, 0, volume.CapacityBytes);
        long freed = Math.Clamp(freedBytes, 0, used);
        long stillUsed = used - freed;

        ImmutableArray<CapacitySegment>.Builder segments =
            ImmutableArray.CreateBuilder<CapacitySegment>(2);

        if (stillUsed > 0)
        {
            segments.Add(new CapacitySegment(CapacitySegmentKind.Used, stillUsed / total, stillUsed));
        }

        if (freed > 0)
        {
            segments.Add(new CapacitySegment(CapacitySegmentKind.Freed, freed / total, freed));
        }

        // The track is the space that was already free. Free-after is that plus the freed band, which
        // the eye reads directly off the bar rather than having to be told.
        long freeBefore = Math.Clamp(volume.CapacityBytes - used, 0, volume.CapacityBytes);
        return new VolumeCapacityResult(segments.ToImmutable(), freeBefore / total);
    }

    /// <summary>
    /// The risk mix: how a pile of reclaimable bytes splits across the three tiers.
    /// </summary>
    /// <remarks>
    /// Normalised against the three tiers rather than against a volume, so the bar always fills its
    /// track. A risk mix with a gap in it would imply a fourth tier that does not exist.
    /// </remarks>
    public static VolumeCapacityResult ByRisk(long safeBytes, long checkBytes, long carefulBytes)
    {
        long safe = Math.Max(0, safeBytes);
        long check = Math.Max(0, checkBytes);
        long careful = Math.Max(0, carefulBytes);

        double total = (double)safe + check + careful;
        if (total <= 0)
        {
            return new VolumeCapacityResult([], 1);
        }

        ImmutableArray<CapacitySegment>.Builder segments =
            ImmutableArray.CreateBuilder<CapacitySegment>(3);

        if (safe > 0)
        {
            segments.Add(new CapacitySegment(CapacitySegmentKind.Safe, safe / total, safe));
        }

        if (check > 0)
        {
            segments.Add(new CapacitySegment(CapacitySegmentKind.Check, check / total, check));
        }

        if (careful > 0)
        {
            segments.Add(new CapacitySegment(CapacitySegmentKind.Careful, careful / total, careful));
        }

        return new VolumeCapacityResult(segments.ToImmutable(), 0);
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
