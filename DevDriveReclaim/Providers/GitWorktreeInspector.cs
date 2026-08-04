using System.Diagnostics;

namespace DevDriveReclaim.Providers;

/// <summary>What git says about a working tree — the only thing that can tell two identical-looking folders apart.</summary>
public sealed record WorktreeState(
    string? Branch,
    bool HasUncommittedChanges,
    int ChangedFileCount,
    bool HasUnpushedCommits,
    int UnpushedCommitCount,
    bool IsMerged,
    bool HasUpstream = true)
{
    /// <summary>
    /// What we assume when git cannot be reached. Deliberately the most cautious answer available:
    /// if we cannot prove a worktree is safe to delete, it is not safe to delete.
    /// </summary>
    public static WorktreeState Unknown => new(null, true, 0, true, 0, false, HasUpstream: false);
}

/// <summary>Abstracted so the risk-grading logic can be tested without a real git repository.</summary>
public interface IWorktreeInspector
{
    WorktreeState Inspect(string worktreePath, CancellationToken cancellationToken);
}

/// <summary>
/// Asks the real <c>git</c> executable about a working tree.
/// </summary>
/// <remarks>
/// Shelling out to git rather than parsing <c>.git</c> by hand is the right trade here: git's own
/// answer to "is this dirty" accounts for gitignore, assume-unchanged, submodules and line-ending
/// normalisation, and a hand-rolled reimplementation that got any of those wrong would mislabel a
/// worktree as safe and delete someone's work.
/// <para>
/// Every failure path returns <see cref="WorktreeState.Unknown"/>, which grades as Careful. Being
/// unable to check is treated as a reason for caution, never as permission.
/// </para>
/// </remarks>
public sealed class GitWorktreeInspector : IWorktreeInspector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public WorktreeState Inspect(string worktreePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return WorktreeState.Unknown;
        }

        try
        {
            string? branch = Run(worktreePath, "rev-parse --abbrev-ref HEAD", cancellationToken)?.Trim();

            string? status = Run(worktreePath, "status --porcelain", cancellationToken);
            if (status is null)
            {
                return WorktreeState.Unknown;
            }

            string[] changed = status
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // "@{u}" resolves the upstream branch. A non-null result with content means commits
            // exist locally that the remote has never seen.
            string? ahead = Run(worktreePath, "log --oneline @{u}..HEAD", cancellationToken);
            bool hasUpstream = ahead is not null;
            string[] unpushed = (ahead ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            bool merged = branch is not null && IsMergedIntoDefault(worktreePath, branch, cancellationToken);

            return new WorktreeState(
                branch,
                HasUncommittedChanges: changed.Length > 0,
                ChangedFileCount: changed.Length,
                // No upstream at all is itself an unpushed state: there is no remote copy of this
                // branch, so the distinction is tracked separately rather than reported as a commit
                // count of zero — "0 unpushed commits, therefore careful" reads like a bug.
                HasUnpushedCommits: !hasUpstream || unpushed.Length > 0,
                UnpushedCommitCount: unpushed.Length,
                IsMerged: merged,
                HasUpstream: hasUpstream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return WorktreeState.Unknown;
        }
    }

    private static bool IsMergedIntoDefault(
        string worktreePath, string branch, CancellationToken cancellationToken)
    {
        foreach (string candidate in (string[])["origin/main", "origin/master", "main", "master"])
        {
            string? merged = Run(
                worktreePath, $"branch --merged {candidate} --list {branch}", cancellationToken);
            if (!string.IsNullOrWhiteSpace(merged))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs git and returns stdout, or null when git failed, was missing, or timed out.</summary>
    private static string? Run(string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the timeout and the kill — nothing to do.
                }

                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
