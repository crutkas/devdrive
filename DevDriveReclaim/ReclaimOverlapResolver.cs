namespace DevDriveReclaim;

/// <summary>
/// Works out which candidates live inside other candidates, so the same bytes are never promised
/// twice.
/// </summary>
/// <remarks>
/// Categories overlap by nature: a worktree row covers the whole working copy, and a build-output
/// row covers the <c>obj</c> folder inside that same working copy. Both rows are legitimate and both
/// should be offered — deleting just the build output while keeping the branch is a real thing to
/// want, and so is deleting the whole worktree. What is <em>not</em> legitimate is adding the two
/// numbers together, which on a real machine inflated a 497-second scan's headline from roughly
/// 500 GB to 812 GB.
/// <para>
/// So nothing is dropped. Each candidate is simply told whether some other candidate contains it,
/// and every total is computed over containment roots only. A user who selects a worktree and its
/// own <c>obj</c> sees the worktree's number, once.
/// </para>
/// </remarks>
public static class ReclaimOverlapResolver
{
    /// <summary>
    /// Returns the subset of <paramref name="candidates"/> that no other member contains — the set
    /// whose sizes can be summed without counting a byte twice.
    /// </summary>
    public static IReadOnlyList<ReclaimCandidate> Roots(IEnumerable<ReclaimCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Sorting by path length puts every possible container before the things it contains, so a
        // single forward pass is enough and the check never has to look backwards.
        List<ReclaimCandidate> ordered = [.. candidates.OrderBy(c => c.Path.Length).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];
        var roots = new List<ReclaimCandidate>();

        foreach (ReclaimCandidate candidate in ordered)
        {
            if (!roots.Any(root => Contains(root.Path, candidate.Path)))
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    /// <summary>
    /// Bytes actually reclaimed by deleting everything in <paramref name="selection"/>, counting
    /// nested selections once. This is the number the before/after bars must use: it is the only
    /// one that will still be true after the deletion runs.
    /// </summary>
    public static long ReclaimableBytes(IEnumerable<ReclaimCandidate> selection) =>
        Roots(selection).Sum(c => c.SizeBytes);

    /// <summary>Reclaimable bytes per volume, nesting resolved.</summary>
    public static IReadOnlyDictionary<string, long> ReclaimableBytesByVolume(
        IEnumerable<ReclaimCandidate> selection) =>
        Roots(selection)
            .GroupBy(c => c.VolumeRoot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.SizeBytes), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For each candidate, the containing candidate's path when one exists. Lets a row say
    /// "already included in crutkas-solid-winner" rather than silently contributing nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ContainerByPath(
        IEnumerable<ReclaimCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<ReclaimCandidate> ordered = [.. candidates.OrderBy(c => c.Path.Length)];
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (ReclaimCandidate candidate in ordered)
        {
            ReclaimCandidate? container = ordered
                .FirstOrDefault(other => Contains(other.Path, candidate.Path));

            if (container is not null)
            {
                map[candidate.Path] = container.Path;
            }
        }

        return map;
    }

    /// <summary>
    /// True when <paramref name="childPath"/> sits strictly beneath <paramref name="parentPath"/>.
    /// The trailing separator matters: without it <c>C:\src\app</c> would appear to contain
    /// <c>C:\src\app-tests</c>, which is a different folder entirely.
    /// </summary>
    public static bool Contains(string parentPath, string childPath)
    {
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childPath))
        {
            return false;
        }

        if (string.Equals(parentPath, childPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string parent = parentPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return childPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}
