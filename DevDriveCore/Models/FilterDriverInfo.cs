namespace DevDriveCore.Models;

/// <summary>
/// One filter driver as the Drives room shows it: its name, where it sits in the I/O path, whether it
/// is currently attached to the Dev Drive, and a one-line description of what it does.
/// </summary>
/// <remarks>
/// The three facts come from three different places, with three different confidence levels, and the
/// room says so rather than blending them:
/// <list type="bullet">
///   <item><description>
///     <see cref="Name"/> and <see cref="IsAttached"/> come from <c>fsutil devdrv query</c>, which
///     needs elevation. Without it there is no filter list at all.
///   </description></item>
///   <item><description>
///     <see cref="Altitude"/> comes from the filter's own service registration, which any user can
///     read. It is null when the filter has no registered instance on this machine.
///   </description></item>
///   <item><description>
///     <see cref="Description"/> is a static description of a known Windows minifilter, not a reading.
///     It is empty for anything we do not recognise, which is honest — a third-party filter's purpose
///     is not ours to guess.
///   </description></item>
/// </list>
/// </remarks>
public sealed record FilterDriverInfo
{
    /// <summary>The minifilter's service name, e.g. <c>WdFilter</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The filter's altitude — its fixed position in the I/O path. Lower runs closer to the
    /// filesystem. Null when the filter has no registered instance we can read.
    /// </summary>
    public double? Altitude { get; init; }

    /// <summary>True when the filter is currently attached to the volume.</summary>
    public bool IsAttached { get; init; }

    /// <summary>
    /// True when policy allows the filter to attach to a Dev Drive, whether or not it currently is.
    /// </summary>
    public bool IsAllowed { get; init; }

    /// <summary>What the filter does, for the filters we know. Empty for anything else.</summary>
    public string Description { get; init; } = string.Empty;
}
