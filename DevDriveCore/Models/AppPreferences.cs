namespace DevDriveCore.Models;

/// <summary>
/// Every preference the app honours, in one immutable shape.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately short. A settings page is a promise that each control changes what the app does, and
/// a toggle wired to nothing is worse than an absent one — it teaches the reader that the page is
/// decoration. Everything here is read by real code on the way to a real decision.
/// </para>
/// <para>
/// This lives in Core rather than in the app because the two consumers that matter — the Overview
/// signal builder and the free-space history — are Core services, and because the clamping rules are
/// the sort of thing that wants a test rather than a UI.
/// </para>
/// </remarks>
public sealed record AppPreferences
{
    /// <summary>The shipped defaults. Also what a corrupt or absent store falls back to.</summary>
    public static readonly AppPreferences Default = new();

    /// <summary>Lowest accepted low-space warning threshold, as a percentage.</summary>
    public const int MinLowFreePercent = 5;

    /// <summary>Highest accepted low-space warning threshold, as a percentage.</summary>
    public const int MaxLowFreePercent = 40;

    /// <summary>Shortest accepted history retention, in days.</summary>
    public const int MinRetentionDays = 7;

    /// <summary>Longest accepted history retention, in days.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>
    /// Below this percentage free, Overview calls a volume out. 15% matches the headroom Windows
    /// itself wants for servicing, and is roughly where NTFS fragmentation and ReFS allocation both
    /// start to bite.
    /// </summary>
    public int LowFreePercent { get; init; } = 15;

    /// <summary>
    /// How many caches can sit off a Dev Drive before Overview reports them as one row instead of one
    /// row each. Zero means never roll up.
    /// </summary>
    public int CacheRollupThreshold { get; init; } = 3;

    /// <summary>Whether Overview raises anything at all about caches that are not on a Dev Drive.</summary>
    public bool WatchCachesOffDevDrive { get; init; } = true;

    /// <summary>
    /// Whether the app records free space over time. Off means the Overview chart stays empty —
    /// Windows keeps no such history, so if the app does not write it, nothing else will.
    /// </summary>
    public bool RecordFreeSpaceHistory { get; init; } = true;

    /// <summary>How long a free-space reading is kept before it is dropped.</summary>
    public int HistoryRetentionDays { get; init; } = 90;

    /// <summary>
    /// Whether Create defaults to resizing an existing volume. The alternative is a VHDX, which is a
    /// file that mounts at boot rather than a real partition.
    /// </summary>
    public bool PreferResizeOverVhdx { get; init; } = true;

    /// <summary>The low-space threshold as the fraction the signal builder wants.</summary>
    public double LowFreeFraction => LowFreePercent / 100d;

    /// <summary>How long a reading is kept, as a span.</summary>
    public TimeSpan HistoryRetention => TimeSpan.FromDays(HistoryRetentionDays);

    /// <summary>
    /// Returns these preferences with every value forced into its accepted range.
    /// </summary>
    /// <remarks>
    /// Applied on read rather than on write. The store is a plain settings dictionary that a future
    /// version — or a user with a registry editor — can put anything into, and a 0% low-space
    /// threshold would silently switch the Overview room's first job off.
    /// </remarks>
    public AppPreferences Normalized() => this with
    {
        LowFreePercent = Math.Clamp(LowFreePercent, MinLowFreePercent, MaxLowFreePercent),
        CacheRollupThreshold = Math.Max(0, CacheRollupThreshold),
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, MinRetentionDays, MaxRetentionDays),
    };
}
