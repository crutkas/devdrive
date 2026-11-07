using DevDriveReclaim.Providers;

namespace DevDriveReclaim;

/// <summary>
/// Why a run kept a row it was asked to remove, as data: something its grade rested on changed since the scan, or (for a
/// duplicate) the run itself removes the copy that was to stay.
/// </summary>
public enum ReclaimChange
{
    /// <summary>Nothing: the row is as the scan found it.</summary>
    None,

    /// <summary>git couldn't be asked about it again (missing, timed out, or the tree isn't a repository any more).</summary>
    Unknown,

    /// <summary>Another branch is checked out, or HEAD was detached or reattached.</summary>
    Branch,

    /// <summary>HEAD is on another commit.</summary>
    Head,

    /// <summary>It has uncommitted changes, or more of them.</summary>
    UncommittedChanges,

    /// <summary>Its branch lost its upstream.</summary>
    NoUpstream,

    /// <summary>It has commits no remote has, or more of them.</summary>
    UnpushedCommits,

    /// <summary>Its branch isn't merged any more.</summary>
    NotMerged,

    /// <summary>It holds a file git ignores that can't be regenerated (a <c>.env</c>, a key) the scan didn't see.</summary>
    LocalOnlyFiles,

    /// <summary>The clone holds more stashes.</summary>
    Stashes,

    /// <summary>A duplicate's kept copy isn't there any more, or can't be read.</summary>
    KeptCopyGone,

    /// <summary>A duplicate's kept copy isn't the same size as it any more.</summary>
    KeptCopyChanged,

    /// <summary>A duplicate's kept copy is being removed in the same run (it is, or is inside, another row of the run).</summary>
    KeptCopyRemovedToo,
}

/// <summary>Why a run keeps a row: what changed since the scan, as data, and in words for the row.</summary>
/// <param name="Change">What changed.</param>
/// <param name="Reason">The change in a few words, e.g. "it has uncommitted changes now".</param>
public sealed record ReclaimChanged(ReclaimChange Change, string Reason);

/// <summary>
/// What a run asks again just before it removes a row whose grade rests on something that can change after the scan.
/// </summary>
/// <remarks>
/// <para>
/// A grade is true when the scan made it. A worktree graded Safe because git said it was clean, merged and pushed is Safe only
/// until someone edits a file in it, and a run can start minutes or hours later — Create a Dev Drive's clean-up removes Safe rows
/// permanently, and nothing asks for a typed DELETE for Safe. So a row graded from git
/// (<see cref="ReclaimCandidate.GitEvidence"/>: a worktree, a dormant clone) is asked about again through the same
/// <see cref="IWorktreeInspector"/>, and kept when git's answer now is riskier than the one it was graded on. This follows
/// <see cref="Providers.Models.LocalModelRemoval"/>, which keeps a model that changed since the scan.
/// </para>
/// <para>
/// Riskier, not merely different: a row graded on <see cref="WorktreeState.Unknown"/> was already graded Careful for it, so
/// there's nothing it can have become. Anything git can't answer now is riskier than what it answered then. A different
/// branch or commit counts, because the grade described the tree that was there.
/// </para>
/// <para>
/// Reads only (<c>git status</c> runs with optional locks off), so a dry run asks it too.
/// </para>
/// <para>
/// A duplicate (<see cref="ReclaimCandidate.KeptCopy"/>) is only safe to remove while the copy kept in its place stays. So the
/// run keeps it when that copy is gone, isn't the same size any more (a cheap look; the scan compared every byte), or is going
/// in the same run, being, or being inside, another row of it.
/// </para>
/// </remarks>
public sealed class ReclaimRecheck
{
    private readonly IWorktreeInspector _git;

    /// <param name="git">What asks git. Injectable so tests describe a tree that changed without building git history.</param>
    public ReclaimRecheck(IWorktreeInspector git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <summary>The real one: this PC's git.</summary>
    public static ReclaimRecheck Default { get; } = new(new GitWorktreeInspector());

    /// <summary>
    /// What means a run should keep <paramref name="candidate"/> now, or null when it can go. A row with nothing to ask again
    /// about (no <see cref="ReclaimCandidate.GitEvidence"/> or <see cref="ReclaimCandidate.KeptCopy"/>) can always go, as far as
    /// this is concerned.
    /// </summary>
    /// <param name="candidate">The row about to be removed.</param>
    /// <param name="run">Everything the run removes, so a duplicate whose kept copy goes in the same run is kept.</param>
    /// <param name="cancellationToken">Stops a git that's being asked.</param>
    /// <exception cref="OperationCanceledException">The run was stopped while git was being asked.</exception>
    public ReclaimChanged? WhatChanged(
        ReclaimCandidate candidate, IReadOnlyCollection<ReclaimCandidate> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(run);

        if (candidate.KeptCopy is { } kept)
        {
            return KeptCopyChanged(candidate, kept, run);
        }

        if (candidate.GitEvidence is not { } before || before.State.IsUnknown)
        {
            return null;
        }

        WorktreeState now = _git.Inspect(candidate.Path, cancellationToken);
        int? stashes = before.StashCount is not null && !now.IsUnknown
            ? _git.StashCount(candidate.Path, cancellationToken)
            : null;

        return Compare(before, now, stashes);
    }

    /// <summary>
    /// The outcome for a row a run left where it was: kept, nothing attempted, nothing freed, with why in words and as data.
    /// One wording for the run and the dry run.
    /// </summary>
    public static ReclaimItemOutcome Kept(ReclaimCandidate candidate, ReclaimChanged changed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(changed);

        // A kept copy going in this same run isn't a change since the scan; it's this run.
        string why = changed.Change == ReclaimChange.KeptCopyRemovedToo
            ? changed.Reason
            : $"changed since the scan: {changed.Reason}";

        return new(candidate, ReclaimItemStatus.Failed, 0, $"kept — {why}")
        {
            Change = changed.Change,
        };
    }

    /// <summary>Why a duplicate can't go now that the copy kept in its place has moved on, or null when it can.</summary>
    private static ReclaimChanged? KeptCopyChanged(ReclaimCandidate candidate, string kept, IReadOnlyCollection<ReclaimCandidate> run)
    {
        if (run.FirstOrDefault(other => !ReferenceEquals(other, candidate) && ReclaimOverlapResolver.Covers(other.Path, kept)) is { } going)
        {
            return new(
                ReclaimChange.KeptCopyRemovedToo,
                $"the copy kept in its place, {kept}, is being removed in this run too, with {going.DisplayName}");
        }

        try
        {
            var keptFile = new FileInfo(kept);
            if (!keptFile.Exists)
            {
                return new(ReclaimChange.KeptCopyGone, $"the copy kept in its place, {kept}, isn't there any more");
            }

            var copy = new FileInfo(candidate.Path);
            if (copy.Exists && copy.Length != keptFile.Length)
            {
                return new(ReclaimChange.KeptCopyChanged, $"the copy kept in its place, {kept}, isn't the same size any more");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return new(ReclaimChange.KeptCopyGone, $"the copy kept in its place, {kept}, couldn't be read");
        }

        return null;
    }

    /// <summary>
    /// What makes <paramref name="now"/> riskier than <paramref name="before"/>, or null when it isn't. The first finding wins:
    /// the row needs one reason to stay, and the reasons are ordered so that it's the one that most explains the rest.
    /// </summary>
    internal static ReclaimChanged? Compare(GitEvidence before, WorktreeState now, int? stashesNow)
    {
        WorktreeState was = before.State;
        if (was.IsUnknown)
        {
            return null;
        }

        if (now.IsUnknown)
        {
            return new(ReclaimChange.Unknown, "git couldn't be asked about it again");
        }

        if (now.IsDetached != was.IsDetached || !string.Equals(now.Branch, was.Branch, StringComparison.Ordinal))
        {
            return new(
                ReclaimChange.Branch,
                now.IsDetached || now.Branch is null
                    ? "its HEAD is detached now"
                    : $"it's on {now.Branch} now, not {was.Branch ?? "a detached HEAD"}");
        }

        if (was.Head is not null && !string.Equals(now.Head, was.Head, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ReclaimChange.Head,
                now.Head is null ? "git couldn't say which commit it's on now" : "it's on another commit now");
        }

        if (now.HasUncommittedChanges && (!was.HasUncommittedChanges || now.ChangedFileCount > was.ChangedFileCount))
        {
            return new(
                ReclaimChange.UncommittedChanges,
                was.HasUncommittedChanges
                    ? $"it has {now.ChangedFileCount:N0} uncommitted files now, not {was.ChangedFileCount:N0}"
                    : "it has uncommitted changes now");
        }

        if (!now.HasUpstream && was.HasUpstream)
        {
            return new(ReclaimChange.NoUpstream, $"{now.Branch ?? "its branch"} has no upstream branch now");
        }

        if (was.HasUpstream &&
            (now.UnpushedCommitCount > was.UnpushedCommitCount || (now.HasUnpushedCommits && !was.HasUnpushedCommits)))
        {
            return new(
                ReclaimChange.UnpushedCommits,
                $"it has {now.UnpushedCommitCount:N0} unpushed commit{(now.UnpushedCommitCount == 1 ? string.Empty : "s")} now");
        }

        if (was.IsMerged && !now.IsMerged)
        {
            return new(ReclaimChange.NotMerged, $"{now.Branch ?? "its branch"} isn't merged now");
        }

        if (now.LocalOnlyIgnoredFiles.FirstOrDefault(
                file => !was.LocalOnlyIgnoredFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) is { } added)
        {
            return new(ReclaimChange.LocalOnlyFiles, $"it holds {added} now, which git ignores and no remote has");
        }

        if (before.StashCount is int stashes && stashesNow is int more && more > stashes)
        {
            return new(ReclaimChange.Stashes, $"it holds {more:N0} stash{(more == 1 ? string.Empty : "es")} now, not {stashes:N0}");
        }

        return null;
    }
}
