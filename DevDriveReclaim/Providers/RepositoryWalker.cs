namespace DevDriveReclaim.Providers;

/// <summary>A git repository found beneath a source root.</summary>
public sealed record DiscoveredRepository(string Path, string Name, bool IsWorktree);

/// <summary>
/// A folder sitting among worktrees that no longer has a <c>.git</c>, and so is invisible to a
/// walk that keys on one.
/// </summary>
/// <param name="SiblingWorktreeCount">
/// How many real worktrees share its parent. This is the evidence for the claim, and the row says
/// it out loud rather than asserting "this is a leftover" and asking to be believed.
/// </param>
public sealed record OrphanedWorktree(string Path, string Name, int SiblingWorktreeCount);

/// <summary>
/// Finds git repositories under the configured source roots.
/// </summary>
/// <remarks>
/// Three providers need the same answer ("where are this developer's repos"), so the walk happens
/// once here rather than three times with three subtly different definitions of "a repo".
/// <para>
/// The walk stops descending as soon as it finds a <c>.git</c>, because a repo's own subfolders are
/// not separate repos and continuing into <c>node_modules</c> to look for more is how a scan turns
/// into a five-minute stall. Nested submodules are the deliberate cost of that decision.
/// </para>
/// </remarks>
public static class RepositoryWalker
{
    /// <summary>
    /// Folder names never worth descending into while hunting for repos: they are large, deep, and
    /// by definition contain no repository the developer cloned.
    /// </summary>
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "target", "dist", "out",
        "packages", ".vs", ".vscode", "$RECYCLE.BIN", "System Volume Information",
    };

    public static IReadOnlyList<DiscoveredRepository> Discover(
        IEnumerable<string> sourceRoots,
        CancellationToken cancellationToken = default,
        int maxDepth = 4)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);

        var found = new List<DiscoveredRepository>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in sourceRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string current, int depth) = stack.Pop();

                string gitPath = Path.Combine(current, ".git");
                bool isRepo = Directory.Exists(gitPath) || File.Exists(gitPath);

                if (isRepo)
                {
                    if (seen.Add(current))
                    {
                        // A .git *file* rather than a folder means this working tree points at a
                        // gitdir elsewhere — that is exactly what `git worktree add` produces.
                        found.Add(new DiscoveredRepository(
                            current, Path.GetFileName(current), IsWorktree: File.Exists(gitPath)));
                    }

                    continue;
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (string child in SafeSubdirectories(current))
                {
                    if (SkipFolders.Contains(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    stack.Push((child, depth + 1));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Finds folders that sit among worktrees but carry no <c>.git</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Discover"/> keys on <c>.git</c>, so a worktree folder whose <c>.git</c> is gone —
    /// deleted by hand, or left behind when the parent repo dropped the registration — is invisible
    /// to the provider whose entire job is finding reclaimable worktrees. On the machine this was
    /// written against that blind spot hid the single largest reclaimable item on the drive: 35 GB
    /// of build output in a folder with no <c>.git</c> at all.
    /// <para>
    /// Detection is positional, because the folder itself carries no evidence: a worktree parking
    /// lot is a directory whose git-bearing children are overwhelmingly worktrees, and a childless
    /// sibling in one of those is a leftover. The bar is deliberately high — at least two worktrees
    /// and a strict majority — because a general source root that happens to contain one worktree
    /// would otherwise indict every ordinary project folder beside it. On this machine that guard is
    /// what separates the two real parking lots (9 of 9 and 20 of 20 git-bearing children being
    /// worktrees) from a source root where the figure was 1 of 11.
    /// </para>
    /// <para>
    /// This consumes an already-completed walk rather than doing its own, so orphan detection costs
    /// one directory listing per parking lot and no second traversal.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<OrphanedWorktree> FindOrphanedWorktrees(
        IReadOnlyList<DiscoveredRepository> discovered,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        var orphans = new List<OrphanedWorktree>();

        IEnumerable<IGrouping<string, DiscoveredRepository>> byParent = discovered
            .Select(r => (Repo: r, Parent: SafeParent(r.Path)))
            .Where(x => x.Parent is not null)
            .GroupBy(x => x.Parent!, x => x.Repo, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, DiscoveredRepository> lot in byParent)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int worktrees = lot.Count(r => r.IsWorktree);
            if (worktrees < 2 || worktrees * 2 <= lot.Count())
            {
                continue;
            }

            foreach (string child in SafeSubdirectories(lot.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string gitPath = Path.Combine(child, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    continue;
                }

                // A folder with repos underneath it is a container, not a leftover. Deleting it
                // would take live repositories with it, which is the one mistake this must not make.
                if (discovered.Any(r => IsAtOrUnder(r.Path, child)))
                {
                    continue;
                }

                orphans.Add(new OrphanedWorktree(child, Path.GetFileName(child), worktrees));
            }
        }

        return orphans;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="ancestor"/> or sits beneath it.
    /// Compares on a separator boundary so <c>C:\work\app2</c> is not read as being inside
    /// <c>C:\work\app</c>.
    /// </summary>
    private static bool IsAtOrUnder(string candidate, string ancestor)
    {
        if (candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = ancestor.EndsWith(Path.DirectorySeparatorChar)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeParent(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates subdirectories, returning empty rather than throwing when a folder cannot be read.
    /// A denied folder is a fact to skip; it should never end a scan.
    /// </summary>
    public static IEnumerable<string> SafeSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException or PathTooLongException)
        {
            return [];
        }
    }
}
