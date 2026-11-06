namespace DevDriveReclaim;

/// <summary>Where a removed item's bytes go.</summary>
public enum ReclaimRoute
{
    /// <summary>Into the volume's Recycle Bin. Restorable, and still occupying the volume.</summary>
    RecycleBin,

    /// <summary>Gone: deleted outright, or a bin emptied. The space is free immediately.</summary>
    Permanent,
}

/// <summary>One containment root and the route it will take.</summary>
/// <param name="Reason">Why a recyclable item is going permanently instead; null otherwise.</param>
public sealed record PlannedRemoval(ReclaimCandidate Candidate, ReclaimRoute Route, string? Reason)
{
    public bool IsPermanent => Route == ReclaimRoute.Permanent;

    /// <summary>
    /// The Recycle Bin's answer that moved a recyclable item into the permanent group, with the reason as data (so a
    /// confirmation can word it, and say a shared one once); null otherwise.
    /// </summary>
    public RecycleBinCheck? Why { get; init; }

    /// <summary>The name to show in the confirmation's list of things that cannot come back.</summary>
    public string PermanentLabel => Reason is null
        ? Candidate.DisplayName
        : $"{Candidate.DisplayName} ({Reason})";
}

/// <summary>
/// What a selection will actually do, worked out before anything is removed: which roots go to the
/// Recycle Bin, which go permanently and why, and therefore how much space each volume gets back now
/// versus when its bin is emptied.
/// </summary>
/// <remarks>
/// <para>
/// One plan feeds the confirmation, the per-volume after-picture and the run, so the three cannot
/// disagree. A root the shell would not recycle — too large for the bin, more than the room the bin has left, a bin that is
/// switched off — is moved into the permanent group <em>here</em>, so the confirmation names it as permanent before
/// the user agrees, and <see cref="ConsentedSelection"/> is what tells the executor that agreement was
/// given. Anything the plan still calls recyclable is refused rather than nuked if the shell turns out
/// to disagree at run time.
/// </para>
/// <para>
/// Recycling does not free space. <c>$Recycle.Bin</c> lives on the same volume, so moving 40 GB into it
/// leaves free space where it was until the bin is emptied. Every total here keeps the two routes apart
/// for exactly that reason.
/// </para>
/// </remarks>
public sealed class ReclaimPlan
{
    private ReclaimPlan(IReadOnlyList<PlannedRemoval> items) => Items = items;

    public static ReclaimPlan Empty { get; } = new([]);

    /// <summary>One entry per containment root, in the order the overlap resolver returns them.</summary>
    public IReadOnlyList<PlannedRemoval> Items { get; }

    public IEnumerable<PlannedRemoval> Permanent => Items.Where(i => i.IsPermanent);

    public IEnumerable<PlannedRemoval> Recycled => Items.Where(i => !i.IsPermanent);

    public long TotalBytes => Items.Sum(i => i.Candidate.SizeBytes);

    /// <summary>Bytes the volumes get back as soon as the run finishes.</summary>
    public long FreedBytes => Permanent.Sum(i => i.Candidate.SizeBytes);

    /// <summary>Bytes moved into a Recycle Bin, which free nothing until the bin is emptied.</summary>
    public long RecycledBytes => Recycled.Sum(i => i.Candidate.SizeBytes);

    public IReadOnlyDictionary<string, long> FreedBytesByVolume => ByVolume(Permanent);

    public IReadOnlyDictionary<string, long> RecycledBytesByVolume => ByVolume(Recycled);

    /// <summary>
    /// Builds the plan for <paramref name="selection"/>. <paramref name="preflight"/> is consulted only
    /// for roots that ask for the Recycle Bin, in the overlap resolver's order; see <see cref="RecycleBinPreflight.CreateBudget"/>.
    /// </summary>
    public static ReclaimPlan Create(
        IEnumerable<ReclaimCandidate> selection,
        Func<ReclaimCandidate, RecycleBinCheck> preflight)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(preflight);

        return Build(ReclaimOverlapResolver.Roots(selection), preflight);
    }

    /// <summary>
    /// Builds the plan for <paramref name="selection"/> from each volume's Recycle Bin settings, sharing a bin's room across
    /// the roots that ask for it. The room goes to what's hardest to get back first (Careful, then Check, then Safe), and
    /// smaller items first within a tier, so the most items stay restorable; what doesn't fit is in the permanent group.
    /// </summary>
    public static ReclaimPlan Create(
        IEnumerable<ReclaimCandidate> selection,
        Func<string, RecycleBinVolumeSettings?> readSettings)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(readSettings);

        IReadOnlyList<ReclaimCandidate> roots = ReclaimOverlapResolver.Roots(selection);
        Func<ReclaimCandidate, RecycleBinCheck> budget = RecycleBinPreflight.CreateBudget(roots, readSettings);
        var checks = new Dictionary<string, RecycleBinCheck>(StringComparer.OrdinalIgnoreCase);
        foreach (ReclaimCandidate root in roots.Where(r => r.SupportsRecycleBin)
            .OrderByDescending(r => r.Risk).ThenBy(r => r.SizeBytes).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            checks[root.Path] = budget(root);
        }

        return Build(roots, root => checks[root.Path]);
    }

    private static ReclaimPlan Build(IReadOnlyList<ReclaimCandidate> roots, Func<ReclaimCandidate, RecycleBinCheck> preflight)
    {
        List<PlannedRemoval> items = [];
        foreach (ReclaimCandidate root in roots)
        {
            if (!root.SupportsRecycleBin)
            {
                items.Add(new PlannedRemoval(root, ReclaimRoute.Permanent, null));
                continue;
            }

            RecycleBinCheck check = preflight(root);
            items.Add(check.WillDeletePermanently
                ? new PlannedRemoval(root, ReclaimRoute.Permanent, check.Reason) { Why = check }
                : new PlannedRemoval(root, ReclaimRoute.RecycleBin, null));
        }

        return new ReclaimPlan(items);
    }

    /// <summary>
    /// The selection to hand the executor once this plan has been confirmed. Roots the plan moved into
    /// the permanent group are marked as not recyclable, which is the executor's only licence to
    /// delete something that asked for the bin. Everything else passes through unchanged.
    /// </summary>
    public IReadOnlyList<ReclaimCandidate> ConsentedSelection(IEnumerable<ReclaimCandidate> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var consented = new HashSet<string>(
            Items.Where(i => i.IsPermanent && i.Candidate.SupportsRecycleBin).Select(i => i.Candidate.Path),
            StringComparer.OrdinalIgnoreCase);

        return [.. selection.Select(c => consented.Contains(c.Path) ? c.WithoutRecycleBin() : c)];
    }

    private static IReadOnlyDictionary<string, long> ByVolume(IEnumerable<PlannedRemoval> items) =>
        items.GroupBy(i => i.Candidate.VolumeRoot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Candidate.SizeBytes), StringComparer.OrdinalIgnoreCase);
}
