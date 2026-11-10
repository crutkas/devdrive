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
    bool IsDetached = false,
    string? Head = null)
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

    /// <summary>True for <see cref="Unknown"/>: git couldn't be asked, so nothing here was read from the tree.</summary>
    public bool IsUnknown => Equals(Unknown);
}

/// <summary>
/// What git said about a working tree when the scan graded it, kept with the row so a run can ask git again and tell whether the
/// answer got riskier in between (<see cref="ReclaimRecheck"/>).
/// </summary>
/// <param name="State">The inspection the grade was made from.</param>
/// <param name="StashCount">
/// The repository's stashes, when the scan asked (a dormant clone otherwise clean and pushed, where a stash decides the grade);
/// null when it didn't. Deleting a whole clone takes its stashes with it; deleting a worktree doesn't.
/// </param>
public sealed record GitEvidence(WorktreeState State, int? StashCount = null);

/// <summary>How a git command ended: its exit code and what it printed.</summary>
internal sealed record GitResult(int ExitCode, string Output);

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

    /// <summary>
    /// Which of <paramref name="folders"/>, inside the working tree at <paramref name="repositoryPath"/>, hold a file git
    /// tracks; null when git couldn't be asked.
    /// </summary>
    /// <remarks>
    /// For the build-output provider, which finds folders by name: a <c>build</c> or <c>bin</c> folder someone committed
    /// files to is not generated output, whatever it's called. Null by default, which that provider reads cautiously (it
    /// doesn't grade the folders Safe), so an inspector that can't answer never makes a folder look regenerable.
    /// </remarks>
    IReadOnlySet<string>? TrackedFolders(
        string repositoryPath, IReadOnlyCollection<string> folders, CancellationToken cancellationToken) => null;
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

    private readonly Func<string, string, CancellationToken, GitResult?> _git;

    public GitWorktreeInspector() : this("git")
    {
    }

    /// <summary>Test seam: runs <paramref name="executable"/> in place of git.</summary>
    internal GitWorktreeInspector(string executable)
        : this((directory, arguments, cancellationToken) => Run(executable, directory, arguments, cancellationToken))
    {
    }

    /// <summary>Test seam: answers in place of the git executable.</summary>
    internal GitWorktreeInspector(Func<string, string, CancellationToken, GitResult?> git) => _git = git;

    public WorktreeState Inspect(string worktreePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return WorktreeState.Unknown;
        }

        try
        {
            // One status answers what four commands did (branch, commit, upstream and ahead count, and the entries): each git
            // start costs ~50 ms, and a lived-in PC has a hundred worktrees and repos. --ignored keeps its traditional,
            // collapsing form here too, so a fully-ignored directory costs one line rather than one line per file.
            GitResult? status = _git(worktreePath, "status --porcelain=v2 --branch --ignored", cancellationToken);
            if (status is not { ExitCode: 0 })
            {
                return WorktreeState.Unknown;
            }

            string[] lines = status.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string? Header(string key) => lines
                .Where(line => line.StartsWith($"# {key} ", StringComparison.Ordinal))
                .Select(line => line[(key.Length + 3)..])
                .FirstOrDefault();

            // A repository with no commit yet has no HEAD to grade: Unknown, as rev-parse's failure made it before. A status
            // without the branch headers (a git too old for --branch in v2) says nothing about HEAD either.
            string? branch = Header("branch.head");
            string? oid = Header("branch.oid");
            if (string.IsNullOrEmpty(branch) || oid is null or "(initial)")
            {
                return WorktreeState.Unknown;
            }

            // A detached HEAD isn't a branch, and the difference decides whether deleting the folder loses anything: a worktree on
            // a branch has its commits held by a ref shared with the parent repository, whereas a detached one is reachable only
            // through this worktree's own HEAD, which goes away with it. Blanked so the messages fall back to the folder name.
            bool detached = string.Equals(branch, "(detached)", StringComparison.Ordinal);
            if (detached)
            {
                branch = null;
            }

            // The commit, so a run can tell that the tree moved since the scan even when every other answer reads the same.
            string commitId = oid;

            string[] changed = lines.Where(static line => !line.StartsWith('#') && !line.StartsWith('!')).ToArray();

            string[] localOnly = lines
                .Where(static line => line.StartsWith("! ", StringComparison.Ordinal))
                .Select(static line => Unquote(line[2..].Trim()))
                .Where(LocalOnlyContent.IsIrreplaceable)
                .ToArray();

            // branch.ab is there only when the upstream exists: none configured, or one whose remote branch is gone, is no remote
            // copy of this branch (as "log @{u}..HEAD" failing was). "+N" is the commits the remote has never seen.
            string? ab = Header("branch.ab");
            bool hasUpstream = ab is not null;
            int unpushed = hasUpstream && ab!.Split(' ')[0] is { Length: > 1 } plus && plus[0] == '+'
                && int.TryParse(plus[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count)
                ? count : 0;

            bool merged = branch is not null && IsMergedIntoDefault(worktreePath, branch, cancellationToken);

            return new WorktreeState(
                branch,
                HasUncommittedChanges: changed.Length > 0,
                ChangedFileCount: changed.Length,
                // No upstream at all is itself an unpushed state: there is no remote copy of this
                // branch, so the distinction is tracked separately rather than reported as a commit
                // count of zero — "0 unpushed commits, therefore careful" reads like a bug.
                HasUnpushedCommits: !hasUpstream || unpushed > 0,
                UnpushedCommitCount: unpushed,
                IsMerged: merged,
                HasUpstream: hasUpstream,
                LocalOnlyIgnoredFiles: localOnly,
                IsDetached: detached,
                Head: commitId);
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

        GitResult? list = _git(repositoryPath, "stash list", cancellationToken);

        return list is not { ExitCode: 0 }
            ? 0
            : list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One <c>git ls-files</c> for all of a repository's folders (a few calls if the list is long), each folder a literal,
    /// case-insensitive pathspec, so a folder is tracked when any file git lists sits under it. It lists what's in the index,
    /// ignored or not, so an ignored <c>bin</c> with nothing committed in it lists nothing.
    /// </para>
    /// <para>
    /// A folder whose <c>.git</c> isn't a repository to git (no <c>HEAD</c>, or a <c>.git</c> file naming a gitdir that has
    /// none) has nothing git tracks, and git isn't asked. A git that fails or times out is null: not an answer.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string>? TrackedFolders(
        string repositoryPath, IReadOnlyCollection<string> folders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folders.Count == 0 || string.IsNullOrWhiteSpace(repositoryPath) || !IsRepository(repositoryPath))
        {
            return tracked;
        }

        // As git prints paths: relative to the top of the working tree, with forward slashes.
        List<(string Folder, string Relative)> pending =
        [
            .. folders.Select(f => (f, Path.GetRelativePath(repositoryPath, f).Replace('\\', '/').TrimEnd('/'))),
        ];

        foreach (List<(string Folder, string Relative)> batch in Batches(pending))
        {
            string pathspecs = string.Join(' ', batch.Select(p => $"\":(literal,icase){p.Relative}\""));
            GitResult? listed = _git(repositoryPath, $"ls-files -z -- {pathspecs}", cancellationToken);
            if (listed is not { ExitCode: 0 })
            {
                return null;
            }

            foreach (string file in listed.Output.Split('\0'))
            {
                string name = file.Trim('\n');
                foreach ((string folder, string relative) in batch)
                {
                    if (name.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        tracked.Add(folder);
                    }
                }
            }
        }

        return tracked;
    }

    /// <summary>Keeps each command line well inside Windows' limit when a repository has many output folders.</summary>
    private static IEnumerable<List<(string Folder, string Relative)>> Batches(List<(string Folder, string Relative)> all)
    {
        const int MaxPathspecChars = 8_000;
        List<(string Folder, string Relative)> batch = [];
        int length = 0;
        foreach ((string Folder, string Relative) item in all)
        {
            if (batch.Count > 0 && length + item.Relative.Length + 20 > MaxPathspecChars)
            {
                yield return batch;
                batch = [];
                length = 0;
            }

            batch.Add(item);
            length += item.Relative.Length + 20;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Whether git sees a repository at <paramref name="path"/>: a <c>.git</c> folder with a <c>HEAD</c>, or a <c>.git</c> file
    /// naming a gitdir that has one, which is what git itself checks. When that can't be read, it's assumed to be one, so git
    /// is asked and a git that can't answer is read cautiously.
    /// </summary>
    internal static bool IsRepository(string path)
    {
        string dotGit = Path.Combine(path, ".git");
        try
        {
            if (Directory.Exists(dotGit))
            {
                return HasHead(dotGit);
            }

            if (!File.Exists(dotGit))
            {
                return false;
            }

            const string Prefix = "gitdir:";
            string line = File.ReadLines(dotGit).FirstOrDefault() ?? string.Empty;
            return line.StartsWith(Prefix, StringComparison.Ordinal) &&
                HasHead(Path.GetFullPath(Path.Combine(path, line[Prefix.Length..].Trim())));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return true;
        }

        static bool HasHead(string gitDir)
        {
            try
            {
                File.GetAttributes(Path.Combine(gitDir, "HEAD"));
                return true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }
    }

    private bool IsMergedIntoDefault(
        string worktreePath, string branch, CancellationToken cancellationToken)
    {
        foreach (string candidate in (string[])["origin/main", "origin/master", "main", "master"])
        {
            GitResult? merged = _git(
                worktreePath, $"branch --merged {candidate} --list {branch}", cancellationToken);
            if (merged is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(merged.Output))
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

    /// <summary>
    /// Runs git and returns its exit code and stdout, or null when git was missing, failed to start,
    /// or timed out — which is not an answer, unlike an exit code.
    /// </summary>
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
    private static GitResult? Run(
        string executable, string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,

                // git writes paths as UTF-8 (ls-files -z doesn't quote them), not in the console's code page.
                StandardOutputEncoding = Encoding.UTF8,
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
                return new GitResult(process.ExitCode, output.ToString());
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
