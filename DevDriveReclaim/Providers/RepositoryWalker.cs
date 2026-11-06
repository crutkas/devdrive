using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace DevDriveReclaim.Providers;

/// <summary>A git repository found beneath a source root.</summary>
public sealed record DiscoveredRepository(string Path, string Name, bool IsWorktree);

/// <summary>A folder to look for repositories in, and how many levels below it to look.</summary>
public sealed record WalkRoot(string Path, int MaxDepth);

/// <summary>How <see cref="RepositoryWalker.Walk"/> runs.</summary>
public sealed record RepositoryWalkOptions
{
    /// <summary>Called as each repository is found, on the walk's threads, before the walk returns.</summary>
    public Action<DiscoveredRepository>? Found { get; init; }

    /// <summary>Called after each folder is read, on the walk's threads (a progress count).</summary>
    public Action? FolderRead { get; init; }

    /// <summary>Folders the walk never enters, such as a cloud drive's sync root or the Windows folder.</summary>
    public IEnumerable<string> Excluded { get; init; } = [];

    /// <summary>How many folders are read at once.</summary>
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>A walk that already read these folders (the Space scan's): a folder it read is listed from it, not the disk.</summary>
    public DevDriveStorage.IVolumeIndex? Index { get; init; }
}

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
/// <para>
/// Each folder is read once: its listing says both whether it is a repo (a <c>.git</c> child) and
/// where to go next. Folders are read in parallel. A folder that isn't on this disk is never
/// entered: a cloud placeholder (recall on open or on data access), an offline file's folder, or a
/// junction or symbolic link, because listing a placeholder can download it and a link can lead
/// back to where the walk has been.
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

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes NotOnThisDisk = FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly record struct Entry(string Name, FileAttributes Attributes, bool IsDirectory);

    public static IReadOnlyList<DiscoveredRepository> Discover(
        IEnumerable<string> sourceRoots,
        CancellationToken cancellationToken = default,
        int maxDepth = 4,
        DevDriveStorage.IVolumeIndex? index = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);

        return Walk(
            [.. sourceRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => new WalkRoot(r, maxDepth))],
            options: index is null ? null : new RepositoryWalkOptions { Index = index },
            cancellationToken);
    }

    /// <summary>
    /// Finds the repositories under <paramref name="roots"/>, each root to its own depth, sorted by path.
    /// A folder reached from two roots is read once, to the deeper of the two depths.
    /// </summary>
    /// <exception cref="OperationCanceledException">The walk was cancelled; what it found so far went to <see cref="RepositoryWalkOptions.Found"/>.</exception>
    public static IReadOnlyList<DiscoveredRepository> Walk(
        IEnumerable<WalkRoot> roots,
        RepositoryWalkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        options ??= new RepositoryWalkOptions();

        var excluded = new HashSet<string>(options.Excluded.Select(Trim), StringComparer.OrdinalIgnoreCase);
        var work = new ConcurrentStack<(string Path, int Left)>();
        var visited = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentDictionary<string, DiscoveredRepository>(StringComparer.OrdinalIgnoreCase);
        int pending = 0;

        foreach (WalkRoot root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.Path) || excluded.Contains(Trim(root.Path)))
            {
                continue;
            }

            pending++;
            work.Push((Trim(root.Path), Math.Max(0, root.MaxDepth)));
        }

        if (pending == 0)
        {
            return [];
        }

        int workers = Math.Clamp(options.Parallelism, 1, 64);
        Parallel.For(
            0,
            workers,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            _ =>
            {
                var spin = new SpinWait();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (work.TryPop(out (string Path, int Left) item))
                    {
                        try
                        {
                            Visit(item.Path, item.Left);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref pending);
                        }

                        spin.Reset();
                    }
                    else if (Volatile.Read(ref pending) == 0)
                    {
                        return;
                    }
                    else
                    {
                        spin.SpinOnce();
                    }
                }
            });

        return [.. found.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];

        void Visit(string folder, int left)
        {
            if (!Claim(folder, left))
            {
                return;
            }

            List<Entry>? entries = List(folder, options.Index);
            options.FolderRead?.Invoke();
            if (entries is null)
            {
                return;
            }

            foreach (Entry entry in entries)
            {
                if (!entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A .git *file* rather than a folder means this working tree points at a gitdir
                // elsewhere — that is exactly what `git worktree add` produces.
                var repo = new DiscoveredRepository(folder, Path.GetFileName(Trim(folder)), IsWorktree: !entry.IsDirectory);
                if (found.TryAdd(folder, repo))
                {
                    options.Found?.Invoke(repo);
                }

                return;
            }

            if (left <= 0)
            {
                return;
            }

            foreach (Entry entry in entries)
            {
                if (!entry.IsDirectory || (entry.Attributes & NotOnThisDisk) != 0 || SkipFolders.Contains(entry.Name))
                {
                    continue;
                }

                string child = Path.Combine(folder, entry.Name);
                if (excluded.Contains(child))
                {
                    continue;
                }

                Interlocked.Increment(ref pending);
                work.Push((child, left - 1));
            }
        }

        // The first visit of a folder reads it; a later one only when it may look deeper than that did.
        bool Claim(string folder, int left)
        {
            while (true)
            {
                if (visited.TryGetValue(folder, out int had))
                {
                    if (had >= left)
                    {
                        return false;
                    }

                    if (visited.TryUpdate(folder, left, had))
                    {
                        return true;
                    }
                }
                else if (visited.TryAdd(folder, left))
                {
                    return true;
                }
            }
        }
    }

    /// <summary>A folder's entries in one read (from <paramref name="index"/> when it read the folder); null when it can't be read.</summary>
    private static List<Entry>? List(string folder, DevDriveStorage.IVolumeIndex? index)
    {
        if (index is not null && index.TryList(folder, out DevDriveStorage.VolumeIndexListing listing))
        {
            List<Entry> fromIndex = [.. listing.Folders.Select(f => new Entry(f.Name, f.Attributes, IsDirectory: true))];
            if (listing.Git == DevDriveStorage.GitEntry.File)
            {
                fromIndex.Add(new Entry(".git", FileAttributes.Normal, IsDirectory: false));
            }

            return fromIndex;
        }

        DevDriveStorage.Live.DirectoryReadTrace.Record(folder);
        try
        {
            return
            [
                .. new FileSystemEnumerable<Entry>(
                    folder,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.Attributes, e.IsDirectory),
                    ListOptions),
            ];
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string Trim(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
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
        CancellationToken cancellationToken = default,
        DevDriveStorage.IVolumeIndex? index = null)
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

            foreach (string child in SafeSubdirectories(lot.Key, index))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (HasGit(child, index))
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
    /// A denied folder is a fact to skip; it should never end a scan. A folder <paramref name="index"/>
    /// read is listed from it.
    /// </summary>
    public static IEnumerable<string> SafeSubdirectories(string path, DevDriveStorage.IVolumeIndex? index = null)
    {
        if (index is not null && index.TryList(path, out DevDriveStorage.VolumeIndexListing listing))
        {
            return [.. listing.Folders.Select(f => Path.Combine(path, f.Name))];
        }

        DevDriveStorage.Live.DirectoryReadTrace.Record(path);
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

    /// <summary>Whether <paramref name="folder"/> has a <c>.git</c> folder or file.</summary>
    private static bool HasGit(string folder, DevDriveStorage.IVolumeIndex? index)
    {
        if (index is not null && index.TryList(folder, out DevDriveStorage.VolumeIndexListing listing))
        {
            return listing.Git != DevDriveStorage.GitEntry.None;
        }

        string gitPath = Path.Combine(folder, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }
}
