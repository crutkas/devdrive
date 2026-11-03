namespace DevDriveCore.Models;

/// <summary>How a signal reads: what colour its dot is and what it implies.</summary>
public enum SignalKind
{
    /// <summary>Space you can get back. The reason most people opened the app.</summary>
    Gain,

    /// <summary>Something that will bite soon. A volume running out, a cache in the wrong place.</summary>
    Warn,

    /// <summary>Something already broken.</summary>
    Bad,

    /// <summary>Worth knowing, costs nothing to ignore.</summary>
    Info,
}

/// <summary>
/// One row of the Overview room: a fact about this machine, and the room that acts on it.
/// </summary>
/// <remarks>
/// Every signal names a destination, because Overview is a router and a row that goes nowhere is a
/// row that belongs in whichever room owns it. That constraint is also the test the comp describes:
/// if a signal has no room to land in, either the signal or the room is missing.
/// </remarks>
public sealed record AttentionSignal
{
    /// <summary>Stable identity, used for the automation id and to keep selection across a rebuild.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The fact, stated as a fact. "218.3 GB can be reclaimed", not "Reclaimable space".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The second line: why the fact is true, or what it is made of.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Where on the machine, in monospace. A path, a drive letter, a file.</summary>
    public string Where { get; init; } = string.Empty;

    /// <summary>The right-aligned figure: "+218.3 GB", "14% free", "4 issues".</summary>
    public string Impact { get; init; } = string.Empty;

    /// <summary>Bytes recoverable, for ranking. Zero for signals that are not about bytes.</summary>
    public long ImpactBytes { get; init; }

    /// <summary>How the row reads.</summary>
    public SignalKind Kind { get; init; }

    /// <summary>Room tag to navigate to. Matches <c>RoomRegistry</c> tags.</summary>
    public string RoomTag { get; init; } = string.Empty;

    /// <summary>Room name for the chip.</summary>
    public string RoomLabel { get; init; } = string.Empty;

    /// <summary>
    /// True when this outranks byte gains. Set for a volume already past its free-space floor: that
    /// is the one signal whose cost is not measured in the bytes it would return.
    /// </summary>
    public bool IsUrgent { get; init; }

    /// <summary>
    /// True when the signal reports what this app has not measured yet rather than something about
    /// the machine. Ranked below everything else, because a gap in the tool's own knowledge is never
    /// more pressing than a finding about the disk — and on a fresh install it would otherwise sort
    /// alphabetically into the middle of real findings.
    /// </summary>
    public bool IsAdvisory { get; init; }
}
