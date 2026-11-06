using DevDriveReclaim.Providers;

namespace DevDriveReclaim;

/// <summary>What changed since the scan that made a run keep a row it was asked to remove, as data.</summary>
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
    /// What changed since the scan that means a run should keep <paramref name="candidate"/>, or null when it can go. A row with
    /// nothing to ask again about (no <see cref="ReclaimCandidate.GitEvidence"/>) can always go, as far as this is concerned.
    /// </summary>
    /// <exception cref="OperationCanceledException">The run was stopped while git was being asked.</exception>
    public ReclaimChanged? WhatChanged(ReclaimCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

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
    /// The outcome for a row a run left where it was because it changed since the scan: kept, nothing attempted, nothing freed,
    /// with what changed in words and as data. One wording for the run and the dry run.
    /// </summary>
    public static ReclaimItemOutcome Kept(ReclaimCandidate candidate, ReclaimChanged changed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(changed);
        return new(candidate, ReclaimItemStatus.Failed, 0, $"kept — changed since the scan: {changed.Reason}")
        {
            Change = changed.Change,
        };
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
