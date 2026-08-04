namespace DevDriveReclaim;

/// <summary>
/// How much a user stands to lose by deleting a candidate. This is the single most important field
/// in the model: it drives preselection, the confirmation gate, and the composition bar.
/// </summary>
/// <remarks>
/// The tiers are deliberately about <em>consequence</em>, not about size or confidence. A 200 GB
/// build output is <see cref="Safe"/> because a rebuild reproduces it; a 4 KB stash is
/// <see cref="Careful"/> because nothing else in the universe has that data. Sorting a delete tool
/// by size teaches users to click the big number; sorting by consequence teaches them to think.
/// </remarks>
public enum ReclaimRisk
{
    /// <summary>Regenerable from something that still exists. Nothing is lost. Preselected.</summary>
    Safe = 0,

    /// <summary>Almost certainly fine, but worth a glance first. Never preselected.</summary>
    Check = 1,

    /// <summary>
    /// Can destroy work that exists nowhere else. Never preselected, and gated behind typing DELETE.
    /// </summary>
    Careful = 2,
}

/// <summary>
/// A stable identity for a reclaim category. Categories are data, not an enum, so a new detector
/// ships as one new <see cref="IReclaimProvider"/> without touching the engine, the shell, or the
/// room — the extension point the whole design turns on.
/// </summary>
public sealed record ReclaimCategory
{
    public ReclaimCategory(string id, string title, string description, string glyph, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(glyph);

        Id = id;
        Title = title;
        Description = description;
        Glyph = glyph;
        Order = order;
    }

    /// <summary>Stable machine identity, e.g. <c>recycle-bin</c>. Persisted in settings and receipts.</summary>
    public string Id { get; }

    /// <summary>Rail label, e.g. "Recycle Bin".</summary>
    public string Title { get; }

    /// <summary>One sentence explaining what this category is, shown above the table.</summary>
    public string Description { get; }

    /// <summary>Segoe Fluent Icons glyph for the rail.</summary>
    public string Glyph { get; }

    /// <summary>Rail ordering. Lower sorts first.</summary>
    public int Order { get; }
}

/// <summary>
/// One reclaimable thing, with everything the inspector needs to answer the same four questions in
/// the same order for every row: what is this, why is it safe (or not), what breaks, and what it
/// costs to get it back.
/// </summary>
/// <remarks>
/// <see cref="RecoveryHint"/> is required rather than optional on purpose. A delete tool that cannot
/// say how to undo a deletion has no business offering it, and making the field mandatory means a
/// new provider cannot quietly skip the hardest question.
/// </remarks>
public sealed record ReclaimCandidate
{
    public ReclaimCandidate(
        string categoryId,
        string path,
        string displayName,
        long sizeBytes,
        ReclaimRisk risk,
        string reason,
        string recoveryHint,
        DateTimeOffset? lastUsedUtc = null,
        int itemCount = 0,
        string? detail = null,
        bool supportsRecycleBin = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryHint);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);

        CategoryId = categoryId;
        Path = path;
        DisplayName = displayName;
        SizeBytes = sizeBytes;
        Risk = risk;
        Reason = reason;
        RecoveryHint = recoveryHint;
        LastUsedUtc = lastUsedUtc;
        ItemCount = itemCount;
        Detail = detail;
        SupportsRecycleBin = supportsRecycleBin;
    }

    public string CategoryId { get; }

    /// <summary>Full physical path. The identity of the candidate and what actually gets deleted.</summary>
    public string Path { get; }

    /// <summary>Short label for the table, usually relative to a meaningful root.</summary>
    public string DisplayName { get; }

    /// <summary>On-disk bytes reclaimed by deleting this.</summary>
    public long SizeBytes { get; }

    public ReclaimRisk Risk { get; }

    /// <summary>Why this row carries the risk tier it does. Shown in the inspector.</summary>
    public string Reason { get; }

    /// <summary>
    /// How to get this back — ideally the literal command. Required: see the type remarks.
    /// </summary>
    public string RecoveryHint { get; }

    /// <summary>Most recent activity, when a provider can determine it. Drives the dormancy story.</summary>
    public DateTimeOffset? LastUsedUtc { get; }

    /// <summary>Files contained, when known.</summary>
    public int ItemCount { get; }

    /// <summary>Optional extra evidence, e.g. "3 unpushed commits".</summary>
    public string? Detail { get; }

    /// <summary>
    /// False for things the Recycle Bin cannot hold (the Recycle Bin itself, and anything large
    /// enough that recycling it would reclaim nothing). Drives whether deletion is reversible.
    /// </summary>
    public bool SupportsRecycleBin { get; }

    /// <summary>The volume root this path lives on, e.g. <c>C:\</c>. Drives the per-volume projection.</summary>
    public string VolumeRoot =>
        System.IO.Path.GetPathRoot(Path) ?? string.Empty;

    /// <summary>Days since last activity, or null when the provider could not tell.</summary>
    public int? DaysSinceLastUse => LastUsedUtc is { } used
        ? Math.Max(0, (int)(DateTimeOffset.UtcNow - used).TotalDays)
        : null;
}
