namespace DevDriveReclaim.Providers;

/// <summary>A git repository found beneath a source root.</summary>
public sealed record DiscoveredRepository(string Path, string Name, bool IsWorktree);

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
