using DevDriveStorage.Live;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Finds repositories nobody has touched in a long time.
/// </summary>
/// <remarks>
/// Dormancy is the only category here where the machine genuinely cannot know the answer. A repo
/// untouched for two years might be abandoned, or it might be the one thing you must not lose. So
/// this provider never grades anything <see cref="ReclaimRisk.Safe"/>: the best case is
/// <see cref="ReclaimRisk.Check"/>, and anything with uncommitted or unpushed work is
/// <see cref="ReclaimRisk.Careful"/> regardless of age.
/// <para>
/// Age is measured from the newest write anywhere in the tree, not from the folder's own timestamp.
/// A directory's timestamp only moves when its immediate children change, so a repo whose deepest
/// source file was edited this morning can look untouched for a year — and that error points the
/// wrong way, toward deleting something active.
/// </para>
/// </remarks>
public sealed class DormantProjectReclaimProvider(IWorktreeInspector? inspector = null) : IReclaimProvider
{
    private readonly IWorktreeInspector _inspector = inspector ?? new GitWorktreeInspector();

    /// <summary>Below this a repo is simply in use, and listing it would be noise.</summary>
    private const int DormantAfterDays = 180;

    public static readonly ReclaimCategory ReclaimCategory = new(
        "dormant-projects",
        "Dormant projects",
        "Repositories nothing has touched in months. Only you know whether they are finished — nothing here is preselected.",
        "\uE8F1",
        order: 4);

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

            IEnumerable<DiscoveredRepository> repositories = RepositoryWalker
                .Discover(context.SourceRoots, cancellationToken)
                .Where(r => !r.IsWorktree);

            foreach (DiscoveredRepository repository in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReclaimScanProgress(
                    ReclaimCategory.Id, $"Checking {repository.Name}", candidates.Count));

                DirectoryMeasurement measurement =
                    DirectoryMeasurer.Measure(repository.Path, cancellationToken);

                if (measurement.NewestWriteUtc is not { } newest)
                {
                    continue;
                }

                int idleDays = Math.Max(0, (int)(DateTimeOffset.UtcNow - newest).TotalDays);
                if (idleDays < DormantAfterDays)
                {
                    continue;
                }

                WorktreeState state = _inspector.Inspect(repository.Path, cancellationToken);
                (ReclaimRisk risk, string reason, string recovery, string detail) =
                    Grade(state, repository, idleDays);

                candidates.Add(new ReclaimCandidate(
                    ReclaimCategory.Id,
                    repository.Path,
                    repository.Name,
                    measurement.AllocatedBytes,
                    risk,
                    reason,
                    recovery,
                    lastUsedUtc: newest,
                    itemCount: measurement.FileCount,
                    detail: detail));
            }

            return candidates;
        }, cancellationToken);
    }

    private static (ReclaimRisk Risk, string Reason, string Recovery, string Detail) Grade(
        WorktreeState state, DiscoveredRepository repository, int idleDays)
    {
        string age = idleDays >= 365
            ? $"{idleDays / 365} year{(idleDays / 365 == 1 ? string.Empty : "s")}"
            : $"{idleDays / 30} months";

        if (state.HasUncommittedChanges)
        {
            return (
                ReclaimRisk.Careful,
                $"Untouched for {age}, but it has uncommitted changes. Those edits exist only in " +
                    "this folder — being old does not make them recoverable.",
                "Nothing. Commit and push before deleting anything here.",
                $"idle {age} · {state.ChangedFileCount:N0} uncommitted files");
        }

        if (state.HasUnpushedCommits)
        {
            return (
                ReclaimRisk.Careful,
                $"Untouched for {age}, and it has commits no remote has. Deleting the folder deletes " +
                    "the only copy of those commits.",
                "Nothing until you push. Then a fresh clone brings it all back.",
                $"idle {age} · {state.UnpushedCommitCount:N0} unpushed commits");
        }

        return (
            ReclaimRisk.Check,
            $"Untouched for {age}, clean, and fully pushed — so a clone gets it all back. Whether " +
                "you are actually done with it is a question only you can answer.",
            $"git clone the remote again into {repository.Name}, then rebuild.",
            $"idle {age} · clean and pushed");
    }
}
