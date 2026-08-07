using DevDriveStorage.Live;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Finds git worktrees and grades them by whether abandoning one would lose work.
/// </summary>
/// <remarks>
/// Worktrees are the clearest case for the three-tier model. Every one of them looks identical on
/// disk — a folder with a <c>.git</c> file — but a clean worktree whose branch is merged costs
/// nothing to delete, while a worktree with uncommitted edits holds the only copy of that work
/// anywhere. Size cannot distinguish them; only inspecting the git state can. So this provider
/// runs <c>git status</c> and grades on the answer, and a dirty worktree is
/// <see cref="ReclaimRisk.Careful"/> no matter how big or how old it is.
/// </remarks>
public sealed class WorktreeReclaimProvider(IWorktreeInspector? inspector = null) : IReclaimProvider
{
    private readonly IWorktreeInspector _inspector = inspector ?? new GitWorktreeInspector();

    public static readonly ReclaimCategory ReclaimCategory = new(
        "worktrees",
        "Worktrees",
        "Extra working copies created by git worktree add. Clean ones cost nothing to remove; dirty ones hold the only copy of that work.",
        "\uE8B7",
        order: 3);

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.Run<IReadOnlyList<ReclaimCandidate>>(() =>
        {
            var candidates = new List<ReclaimCandidate>();

            IEnumerable<DiscoveredRepository> worktrees = context
                .Repositories(cancellationToken)
                .Where(r => r.IsWorktree);

            foreach (DiscoveredRepository worktree in worktrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReclaimScanProgress(
                    ReclaimCategory.Id, $"Checking {worktree.Name}", candidates.Count));

                WorktreeState state = _inspector.Inspect(worktree.Path, cancellationToken);
                DirectoryMeasurement measurement =
                    DirectoryMeasurer.Measure(worktree.Path, cancellationToken);

                if (measurement.AllocatedBytes <= 0)
                {
                    continue;
                }

                (ReclaimRisk risk, string reason, string detail) = Grade(state, worktree.Name);

                candidates.Add(new ReclaimCandidate(
                    ReclaimCategory.Id,
                    worktree.Path,
                    worktree.Name,
                    measurement.AllocatedBytes,
                    risk,
                    reason,
                    RecoveryHint(state, worktree.Name),
                    lastUsedUtc: measurement.NewestWriteUtc,
                    itemCount: measurement.FileCount,
                    detail: detail));
            }

            AddOrphans(context, candidates, progress, cancellationToken);

            return candidates;
        }, cancellationToken);
    }

    /// <summary>
    /// Adds folders that sit among worktrees but have lost their <c>.git</c>.
    /// </summary>
    /// <remarks>
    /// These belong in this category rather than one of their own: they are worktree folders, the
    /// user recognises them as such, and a rail entry that usually holds a single row would be
    /// clutter charging rent. What differs is the risk and the remedy, and those the row states.
    /// <para>
    /// The category's own <see cref="ReclaimScanContext.MinimumCandidateBytes"/> floor does useful
    /// work here for free. Most orphans are empty directories left by <c>git worktree remove</c> —
    /// eleven of the twelve found on the machine this was written against were 0 bytes. They are
    /// clutter, not space, and a room about reclaiming space should not list them.
    /// </para>
    /// </remarks>
    private static void AddOrphans(
        ReclaimScanContext context,
        List<ReclaimCandidate> candidates,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OrphanedWorktree> orphans = RepositoryWalker.FindOrphanedWorktrees(
            context.Repositories(cancellationToken), cancellationToken);

        foreach (OrphanedWorktree orphan in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ReclaimScanProgress(
                ReclaimCategory.Id, $"Checking {orphan.Name}", candidates.Count));

            DirectoryMeasurement measurement =
                DirectoryMeasurer.Measure(orphan.Path, cancellationToken);

            if (measurement.AllocatedBytes < context.MinimumCandidateBytes)
            {
                continue;
            }

            candidates.Add(new ReclaimCandidate(
                ReclaimCategory.Id,
                orphan.Path,
                orphan.Name,
                measurement.AllocatedBytes,
                ReclaimRisk.Careful,
                "This sits among worktrees but has no .git, so git knows nothing about it. " +
                    "Nothing here can be checked — not a branch, not a remote, not whether " +
                    "anything is uncommitted.",
                "Nothing. With no .git there is no branch to re-create it from, so anything " +
                    "here that matters must be copied out before the folder goes.",
                lastUsedUtc: measurement.NewestWriteUtc,
                itemCount: measurement.FileCount,
                detail: $"no .git · beside {orphan.SiblingWorktreeCount:N0} worktree" +
                    $"{(orphan.SiblingWorktreeCount == 1 ? string.Empty : "s")}"));
        }
    }

    /// <summary>
    /// Test seam for the grading table. Exposed because this wording <em>is</em> the product here —
    /// a row whose text contradicts its own risk grade teaches users to distrust every grade.
    /// </summary>
    internal static (ReclaimRisk Risk, string Reason, string Detail) GradeForTest(
        WorktreeState state, string name) => Grade(state, name);

    /// <summary>Test seam for the recovery text.</summary>
    internal static string RecoveryHintForTest(WorktreeState state, string name) =>
        RecoveryHint(state, name);

    /// <summary>
    /// How to get this worktree back. The honest answer depends entirely on where the commits live:
    /// <c>git worktree add</c> only restores what a remote already has, so promising it for
    /// never-pushed work would be exactly the kind of false reassurance this field exists to prevent.
    /// </summary>
    private static string RecoveryHint(WorktreeState state, string name)
    {
        if (state.HasUncommittedChanges)
        {
            return "Nothing. Uncommitted edits exist only here — commit or stash them first.";
        }

        if (!state.HasUpstream)
        {
            return $"Nothing, unless you push first. {state.Branch ?? name} has no remote, so " +
                   "deleting this worktree deletes the only copy of its commits. " +
                   $"Run git push -u origin {state.Branch ?? name} and this becomes recoverable.";
        }

        if (state.HasLocalOnlyIgnoredFiles)
        {
            // The commits are recoverable and the ignored files are not, so the hint has to say
            // both. Offering only the git command would read as "fully recoverable".
            return $"git worktree add {name} {state.Branch ?? "<branch>"} restores the code, but not " +
                   $"{string.Join(", ", state.LocalOnlyIgnoredFiles.Take(3))} — copy those out first.";
        }

        return $"git worktree add {name} {state.Branch ?? "<branch>"} recreates it, then rebuild.";
    }

    private static (ReclaimRisk Risk, string Reason, string Detail) Grade(
        WorktreeState state, string name)
    {
        if (state.HasUncommittedChanges)
        {
            return (
                ReclaimRisk.Careful,
                "This worktree has uncommitted changes. Deleting it destroys edits that exist " +
                    "nowhere else — not on a branch, not on a remote.",
                $"{state.ChangedFileCount:N0} uncommitted file" +
                    $"{(state.ChangedFileCount == 1 ? string.Empty : "s")}");
        }

        if (state.HasUnpushedCommits)
        {
            // Two genuinely different situations reach this branch, and conflating them produced
            // the nonsense "0 unpushed commits" on a row graded Careful. Both are still Careful —
            // in each case this machine holds the only copy — but the row has to say which.
            if (!state.HasUpstream)
            {
                return (
                    ReclaimRisk.Careful,
                    $"{state.Branch ?? name} has never been pushed — it has no remote tracking " +
                        "branch at all. Whatever is committed here exists only on this machine.",
                    $"{state.Branch ?? name} · no upstream branch");
            }

            return (
                ReclaimRisk.Careful,
                "This worktree has commits that are not on any remote. The work is committed, but " +
                    "this machine is the only place it exists.",
                $"{state.UnpushedCommitCount:N0} unpushed commit" +
                    $"{(state.UnpushedCommitCount == 1 ? string.Empty : "s")}");
        }

        if (state.HasLocalOnlyIgnoredFiles)
        {
            // Reached only by a worktree that is clean and fully pushed, which is precisely the
            // one that would otherwise be graded Safe and ticked automatically. Git is silent
            // about these files by design, so nothing else in the room would ever mention them.
            string first = state.LocalOnlyIgnoredFiles[0];
            int extra = state.LocalOnlyIgnoredFiles.Count - 1;

            return (
                ReclaimRisk.Careful,
                "This worktree holds local configuration that git ignores, so it was never " +
                    "committed and is on no remote. The code here is safe; these files are not.",
                extra == 0 ? first : $"{first} and {extra:N0} more not in git");
        }

        if (state.IsMerged)
        {
            return (
                ReclaimRisk.Safe,
                $"Clean, and {state.Branch ?? name} is already merged. Everything here is in the " +
                    "main branch's history.",
                $"{state.Branch ?? name} · merged and clean");
        }

        return (
            ReclaimRisk.Check,
            "Clean and fully pushed, so nothing is lost — but the branch is not merged yet, so " +
                "check you are actually finished with it.",
            $"{state.Branch ?? name} · pushed, not merged");
    }
}
