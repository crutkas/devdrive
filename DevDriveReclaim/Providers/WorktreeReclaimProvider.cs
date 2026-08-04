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

            IEnumerable<DiscoveredRepository> worktrees = RepositoryWalker
                .Discover(context.SourceRoots, cancellationToken)
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

            return candidates;
        }, cancellationToken);
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
