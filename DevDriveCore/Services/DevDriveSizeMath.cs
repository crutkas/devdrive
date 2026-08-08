using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Pure, deterministic math behind the Treatment&#160;A size control (the combined draggable disk-bar
/// kept in sync with a Slider, a NumberBox and a GB/MB unit dropdown). It owns every conversion and
/// clamp the UI needs so the Slider&#8596;NumberBox&#8596;disk-bar handle all stay consistent, and so the
/// behaviour is unit-testable without instantiating any XAML.
/// </summary>
/// <remarks>
/// <para>Sizes are binary (1024-based) to match Windows' "GB"/"MB" labelling and the platform's
/// <c>50&#160;GiB</c> Dev Drive minimum (DevDrive-Analysis.md: <c>c_minimumSizeForDevVolumeInBytes = 50ull &lt;&lt; 30</c>).</para>
/// <para>The disk-bar shows three segments left-to-right — <b>used + protected</b>, the
/// <b>remaining</b> free/shrinkable space, and the carved <b>Dev Drive</b> chunk — with the drag handle
/// sitting on the boundary between <i>remaining</i> and <i>Dev Drive</i>. In the model the maximum
/// selectable size equals the source's free/shrinkable space (<c>total - used</c>), so the three
/// segments always sum to the whole bar.</para>
/// </remarks>
public static class DevDriveSizeMath
{
    /// <summary>Bytes in one binary gigabyte (GiB).</summary>
    public const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    /// <summary>Bytes in one binary megabyte (MiB).</summary>
    public const double BytesPerMegabyte = 1024d * 1024d;

    /// <summary>Hard platform minimum for a Dev Drive: 50 GiB.</summary>
    public const double MinimumSizeBytes = 50d * BytesPerGigabyte;

    /// <summary>The 50 GiB minimum as an exact integer, for building byte-typed plans.</summary>
    public const ulong MinimumSizeBytesExact = 50UL * 1024UL * 1024UL * 1024UL;

    /// <summary>
    /// Extra VHD container capacity reserved for GPT metadata/alignment so the formatted partition can
    /// still equal the user-selected Dev Drive size, including at the 50 GiB minimum.
    /// </summary>
    public const ulong VhdContainerHeadroomBytesExact = 128UL * 1024UL * 1024UL;

    public const double VhdContainerHeadroomBytes = VhdContainerHeadroomBytesExact;

    /// <summary>The size we would pre-select if the source had unlimited room: 256 GiB.</summary>
    public const double PreferredSizeBytes = 256d * BytesPerGigabyte;

    /// <summary>
    /// The free space a default selection tries to leave behind on the source, in bytes: 45 GiB.
    /// </summary>
    /// <remarks>
    /// A reserve stated in absolute bytes rather than as a percentage of capacity, because the
    /// question "is there enough room left to work" does not scale with how big the disk is — 5% of
    /// a 4 TB disk is roomy and 5% of a 256 GB disk is not. 45 GiB is the point below which Windows,
    /// a page file, an update staging area and a build tree stop fitting comfortably together.
    /// </remarks>
    public const double ComfortableReserveBytes = 45d * BytesPerGigabyte;

    /// <summary>
    /// The size to pre-select for a source that can give at most <paramref name="maximumSelectableBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never "everything the volume will give". Taking the whole maximum is a valid thing to ask for
    /// and a terrible thing to arrive at by doing nothing: it leaves the source with zero free space,
    /// and a create form that opens on that value is proposing it. So the default keeps
    /// <see cref="ComfortableReserveBytes"/> behind whenever the arithmetic allows.
    /// </para>
    /// <para>
    /// When the source is small enough that the reserve and a viable Dev Drive cannot both fit, the
    /// 50 GiB minimum wins and the reserve is given up — a Dev Drive below the minimum is not a
    /// smaller Dev Drive, it is no Dev Drive. The user can still drag the slider anywhere in range;
    /// this only decides where it starts.
    /// </para>
    /// </remarks>
    public static double DefaultSizeBytes(double maximumSelectableBytes)
    {
        double max = Math.Max(0d, maximumSelectableBytes);
        double afterReserve = max - ComfortableReserveBytes;
        return ClampSizeBytes(Math.Min(PreferredSizeBytes, afterReserve), max);
    }

    /// <summary>
    /// Clamps <paramref name="requestedBytes"/> into the valid Dev Drive range
    /// <c>[<see cref="MinimumSizeBytes"/>, <paramref name="maximumSelectableBytes"/>]</c>. When the
    /// source can't even fit the minimum the minimum is returned (and callers should flag the source
    /// as too small); this mirrors the reference wizard's clamp order.
    /// </summary>
    public static double ClampSizeBytes(double requestedBytes, double maximumSelectableBytes)
    {
        double max = Math.Max(0d, maximumSelectableBytes);
        double clamped = Math.Min(requestedBytes, max);
        return Math.Max(MinimumSizeBytes, clamped);
    }

    /// <summary>True when the raw request is below the 50 GiB minimum (drives the "Minimum 50 GB" message).</summary>
    public static bool IsBelowMinimum(double requestedBytes) => requestedBytes < MinimumSizeBytes;

    /// <summary>True when the raw request exceeds the available space (drives the "Not enough space" message).</summary>
    public static bool ExceedsMaximum(double requestedBytes, double maximumSelectableBytes) =>
        requestedBytes > maximumSelectableBytes;

    /// <summary>Converts a byte count to the supplied display unit (GB or MB).</summary>
    public static double BytesToUnit(double bytes, DevDriveSizeUnit unit) =>
        bytes / BytesPerUnit(unit);

    /// <summary>Converts a value expressed in the supplied display unit back to bytes.</summary>
    public static double UnitToBytes(double value, DevDriveSizeUnit unit) =>
        value * BytesPerUnit(unit);

    /// <summary>Bytes per one of the supplied display unit.</summary>
    public static double BytesPerUnit(DevDriveSizeUnit unit) =>
        unit == DevDriveSizeUnit.Megabytes ? BytesPerMegabyte : BytesPerGigabyte;

    /// <summary>Converts a byte count to whole-number GiB (what the integer Slider tracks).</summary>
    public static double BytesToGigabytes(double bytes) => bytes / BytesPerGigabyte;

    /// <summary>Converts GiB to bytes.</summary>
    public static double GigabytesToBytes(double gigabytes) => gigabytes * BytesPerGigabyte;

    /// <summary>
    /// Space left on the source after carving out the Dev Drive: for resize this is the remaining
    /// shrinkable space on the source volume; for VHDX it is the remaining free space on the host
    /// volume. Never negative.
    /// </summary>
    public static double RemainingBytes(double maximumSelectableBytes, double selectedBytes) =>
        Math.Max(0d, maximumSelectableBytes - selectedBytes);

    // ---- Disk-bar segment geometry (fractions of the whole bar, each in [0,1]) ------------------

    /// <summary>Fraction of the bar occupied by the source's used + protected space.</summary>
    public static double UsedFraction(double totalBytes, double usedBytes) =>
        totalBytes <= 0d ? 0d : Clamp01(usedBytes / totalBytes);

    /// <summary>Fraction of the bar occupied by the remaining free/shrinkable space after carving.</summary>
    public static double RemainingFraction(double totalBytes, double maximumSelectableBytes, double selectedBytes) =>
        totalBytes <= 0d ? 0d : Clamp01(RemainingBytes(maximumSelectableBytes, selectedBytes) / totalBytes);

    /// <summary>
    /// Whether carving this Dev Drive would leave the source below the user's low-free threshold.
    /// </summary>
    /// <remarks>
    /// Drives the colour of the free segment on the size bar, so the moment a drag crosses into
    /// territory the app would otherwise warn about afterwards, the bar says so while the choice is
    /// still being made rather than once it has been committed.
    /// <para>
    /// A bar with nothing loaded reports <c>0</c> remaining, which is not the same as a source that
    /// is genuinely full — so an unloaded bar is never called low.
    /// </para>
    /// </remarks>
    /// <param name="totalBytes">Capacity of the source volume.</param>
    /// <param name="maximumSelectableBytes">Largest Dev Drive the source can give up.</param>
    /// <param name="selectedBytes">The size currently chosen.</param>
    /// <param name="lowFreeFraction">The share of capacity below which free space counts as low.</param>
    public static bool LeavesSourceLowOnSpace(
        double totalBytes, double maximumSelectableBytes, double selectedBytes, double lowFreeFraction) =>
        totalBytes > 0d
        && RemainingFraction(totalBytes, maximumSelectableBytes, selectedBytes) < lowFreeFraction;

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);
}
