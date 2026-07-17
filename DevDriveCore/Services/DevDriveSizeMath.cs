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

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);
}
