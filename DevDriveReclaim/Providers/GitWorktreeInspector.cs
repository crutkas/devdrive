using System.Diagnostics;
using System.Text;

namespace DevDriveReclaim.Providers;

/// <summary>What git says about a working tree — the only thing that can tell two identical-looking folders apart.</summary>
public sealed record WorktreeState(
    string? Branch,
    bool HasUncommittedChanges,
    int ChangedFileCount,
    bool HasUnpushedCommits,
    int UnpushedCommitCount,
    bool IsMerged,
    bool HasUpstream = true,
    IReadOnlyList<string>? LocalOnlyIgnoredFiles = null,
    bool IsDetached = false)
{
    /// <summary>
    /// Gitignored paths that cannot be regenerated — a <c>.env</c>, a signing certificate, a
    /// credential file. Empty is the overwhelmingly common case; see <see cref="LocalOnlyContent"/>
    /// for why the rule that produces it is deliberately narrow.
    /// </summary>
    public IReadOnlyList<string> LocalOnlyIgnoredFiles { get; init; } = LocalOnlyIgnoredFiles ?? [];

    public bool HasLocalOnlyIgnoredFiles => LocalOnlyIgnoredFiles.Count > 0;

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

    /// <summary>How many stashes the repository holds, or 0 when it cannot be told.</summary>
    /// <remarks>
    /// Separate from <see cref="Inspect"/> because only the dormant-project path can use it, and it
    /// costs a git invocation per repository. Deleting a <b>worktree</b> folder cannot lose a stash:
    /// <c>refs/stash</c> and the object database are shared with the parent repository, so the stash
    /// is still there afterwards. Deleting a whole clone destroys both.
    /// <para>
    /// Defaulting to 0 is safe rather than optimistic: the only caller reaches this after three git
    /// commands have already succeeded, so a repository this cannot be answered for has already
    /// been graded Careful by <see cref="WorktreeState.Unknown"/>.
    /// </para>
    /// </remarks>
    int StashCount(string repositoryPath, CancellationToken cancellationToken) => 0;
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

            // git reports a detached HEAD as the literal string "HEAD". That is not a branch name,
            // and the difference decides whether deleting the folder loses anything: a worktree on a
            // branch has its commits held by a ref shared with the parent repository, whereas a
            // detached one is reachable only through this worktree's own HEAD, which goes away with
            // it. Blanked so the messages fall back to the folder name rather than printing "HEAD".
            bool detached = string.Equals(branch, "HEAD", StringComparison.Ordinal);
            if (detached)
            {
                branch = null;
            }

            // --ignored is the only way to see content git is deliberately silent about. It uses
            // the traditional (collapsing) form, so a fully-ignored directory costs one line rather
            // than one line per file — measured at ~180ms per worktree against plain status, which
            // is affordable against a worktree pass already measured in tens of seconds.
            string? status = Run(worktreePath, "status --porcelain --ignored", cancellationToken);
            if (status is null)
            {
                return WorktreeState.Unknown;
            }

            string[] lines = status
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string[] changed = lines.Where(static line => !line.StartsWith("!!", StringComparison.Ordinal)).ToArray();

            string[] localOnly = lines
                .Where(static line => line.StartsWith("!!", StringComparison.Ordinal))
                .Select(static line => Unquote(line[2..].Trim()))
                .Where(LocalOnlyContent.IsIrreplaceable)
                .ToArray();

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
                HasUpstream: hasUpstream,
                LocalOnlyIgnoredFiles: localOnly,
                IsDetached: detached);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return WorktreeState.Unknown;
        }
    }

    /// <inheritdoc />
    public int StashCount(string repositoryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return 0;
        }

        string? list = Run(repositoryPath, "stash list", cancellationToken);

        return list is null
            ? 0
            : list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
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

    /// <summary>
    /// Strips the quoting git applies to a path containing spaces or non-ASCII characters.
    /// </summary>
    /// <remarks>
    /// Only the surrounding quotes are removed. The C-style escapes git also emits inside them
    /// are left alone deliberately: the result is used to decide whether a name looks like a
    /// secret, and an escape sequence that survives simply fails to match, which errs towards
    /// treating the file as regenerable rather than inventing a name that is not on disk.
    /// </remarks>
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>Runs git and returns stdout, or null when git failed, was missing, or timed out.</summary>
    /// <remarks>
    /// <para>
    /// The reads are asynchronous and the wait is bounded, and both of those are load-bearing.
    /// Calling <c>StandardOutput.ReadToEnd()</c> before <c>WaitForExit(timeout)</c> — the obvious
    /// way to write this — deadlocks twice over. <c>ReadToEnd</c> blocks until git closes stdout,
    /// so a git that hangs (an index lock, a credential prompt, a dead network mount) never
    /// returns and the timeout below it is unreachable dead code. And with stderr redirected but
    /// never drained, a git chatty enough to fill the ~4 KB pipe buffer blocks writing to it,
    /// which means it never exits, which means stdout never reaches EOF either.
    /// </para>
    /// <para>
    /// <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c> drain both pipes on the thread pool, so
    /// <c>WaitForExit</c> is reached immediately and its timeout is real. Stderr is discarded, but
    /// it has to be <i>read</i> to be discarded safely.
    /// </para>
    /// <para>
    /// This runs once per worktree — 41 times per scan on the machine this was written against —
    /// so a single hang stalls the whole Reclaim scan indefinitely.
    /// </para>
    /// </remarks>
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

            // Never let git stop to ask a human something. A credential or passphrase prompt on a
            // background scan thread is invisible and waits forever, which is the exact hang the
            // timeout exists to bound — better to fail fast and grade the worktree Careful.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = new StringBuilder();

            // Append under a lock: OutputDataReceived is raised on thread-pool threads and there is
            // no ordering guarantee that one callback completes before the next begins.
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (output)
                {
                    output.Append(e.Data).Append('\n');
                }
            };

            // Read and drop. Draining is not optional even when the content is unwanted: an unread
            // stderr pipe is a deadlock, not merely wasted output.
            process.ErrorDataReceived += static (_, _) => { };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!WaitForExit(process, cancellationToken))
            {
                Kill(process);
                return null;
            }

            // The process has exited; this second wait has no timeout because it is only flushing
            // the asynchronous read callbacks, which are guaranteed to complete once the pipes hit
            // EOF. Without it the last lines of output can still be in flight.
            process.WaitForExit();

            cancellationToken.ThrowIfCancellationRequested();

            lock (output)
            {
                return process.ExitCode == 0 ? output.ToString() : null;
            }
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

    /// <summary>
    /// Waits for git, giving up at <see cref="Timeout"/> or as soon as the scan is cancelled.
    /// </summary>
    /// <remarks>
    /// Polling in short slices rather than one long <c>WaitForExit(10000)</c> so that cancelling a
    /// scan does not have to wait out a hung git first. A scan cancelled by the user should stop
    /// now, not in ten seconds' time, and there are as many of these calls as there are worktrees.
    /// </remarks>
    private static bool WaitForExit(Process process, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            if (process.WaitForExit((int)Math.Min(remaining, 100)))
            {
                return true;
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already exited between the check and the kill, or the platform will not walk the
            // tree. Either way there is nothing left to do and nothing worth reporting.
        }
    }
}
