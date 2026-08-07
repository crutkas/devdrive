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
    /// a worktree's branch and objects belong to the parent repository and survive the folder, but
    /// uncommitted edits and a detached HEAD do not, and neither does anything git never tracked.
    /// </summary>
    private static string RecoveryHint(WorktreeState state, string name)
    {
        if (state.HasUncommittedChanges)
        {
            return "Nothing. Uncommitted edits exist only here — commit or stash them first.";
        }

        if (state.IsDetached && state.HasUnpushedCommits)
        {
            return "Nothing, unless you give those commits a name first. Run git branch " +
                   $"{name} HEAD in the worktree, and they survive in the parent repository.";
        }

        if (state.HasLocalOnlyIgnoredFiles)
        {
            // The commits are recoverable and the ignored files are not, so the hint has to say
            // both. Offering only the git command would read as "fully recoverable".
            return $"git worktree prune, then git worktree add {name} {state.Branch ?? "<branch>"} " +
                   "restores the code, but not " +
                   $"{string.Join(", ", state.LocalOnlyIgnoredFiles.Take(3))} — copy those out first.";
        }

        // Prune first is not pedantry: the parent repository keeps an administrative entry for a
        // worktree whose folder has gone, and git refuses to re-add at that path until it is
        // cleared. Verified by deleting a worktree and re-adding it.
        return $"git worktree prune, then git worktree add {name} " +
               $"{state.Branch ?? "<branch>"} recreates it, then rebuild.";
    }

    private static (ReclaimRisk Risk, string Reason, string Detail) Grade(
        WorktreeState state, string name)
    {
        // Everything that can genuinely lose work is tested first. Reaching a Check verdict early
        // would mask a Careful one below it -- which is how a worktree carrying an uncommittable
        // .env would have slipped through once unpushed commits stopped being Careful themselves.
        if (state.HasUncommittedChanges)
        {
            return (
                ReclaimRisk.Careful,
                "This worktree has uncommitted changes. Deleting it destroys edits that exist " +
                    "nowhere else — not on a branch, not on a remote.",
                $"{state.ChangedFileCount:N0} uncommitted file" +
                    $"{(state.ChangedFileCount == 1 ? string.Empty : "s")}");
        }

        // A detached HEAD is the one committed state that a folder delete really does destroy.
        // Nothing but this worktree's own HEAD points at those commits, so once the folder and its
        // administrative entry are gone they are unreachable and git will collect them.
        if (state.IsDetached && state.HasUnpushedCommits)
        {
            return (
                ReclaimRisk.Careful,
                "This worktree is not on a branch, so nothing but the worktree itself points at " +
                    "its commits. Deleting the folder leaves them unreachable.",
                "detached HEAD · commits on no branch");
        }

        if (state.HasLocalOnlyIgnoredFiles)
        {
            // Git is silent about these files by design, so nothing else in the room would ever
            // mention them -- and without this the worktree would be graded Safe and ticked
            // automatically.
            string first = state.LocalOnlyIgnoredFiles[0];
            int extra = state.LocalOnlyIgnoredFiles.Count - 1;

            return (
                ReclaimRisk.Careful,
                "This worktree holds local configuration that git ignores, so it was never " +
                    "committed and is on no remote. The code here is safe; these files are not.",
                extra == 0 ? first : $"{first} and {extra:N0} more not in git");
        }

        if (state.HasUnpushedCommits)
        {
            // Not Careful, and this was measured rather than assumed: a worktree's branch ref and
            // every object it names live in the *parent* repository, which is shared. Deleting the
            // folder leaves the branch and its commits intact -- verified by deleting one and
            // reading the file contents back out of the parent afterwards.
            //
            // The old wording claimed this machine held "the only copy" and graded Careful, which
            // fired on 29 of the 54 worktrees here. A warning that fires on the majority of rows
            // teaches people to type DELETE to get past it, and that is the one habit this risk
            // model cannot survive.
            //
            // Reaching this line already proves the parent is present: without it every git command
            // in the worktree fails with exit 128, which grades Unknown and therefore Careful.
            if (!state.HasUpstream)
            {
                return (
                    ReclaimRisk.Check,
                    $"{state.Branch ?? name} has no remote tracking branch, so this machine holds " +
                        "the only copy — but the branch itself lives in the parent repository and " +
                        "outlives this folder.",
                    $"{state.Branch ?? name} · no upstream branch");
            }

            return (
                ReclaimRisk.Check,
                "This worktree has commits that are not on any remote. They are held by the parent " +
                    "repository rather than by this folder, so deleting it does not lose them.",
                $"{state.UnpushedCommitCount:N0} unpushed commit" +
                    $"{(state.UnpushedCommitCount == 1 ? string.Empty : "s")}");
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
