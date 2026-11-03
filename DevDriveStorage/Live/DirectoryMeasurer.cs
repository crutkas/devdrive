namespace DevDriveStorage.Live;

/// <summary>What a folder costs on disk, plus the freshness signal that drives dormancy.</summary>
public readonly record struct DirectoryMeasurement(
    long AllocatedBytes,
    long ApparentBytes,
    int FileCount,
    int FolderCount,
    DateTimeOffset? NewestWriteUtc)
{
    public static DirectoryMeasurement Empty => new(0, 0, 0, 0, null);
}

/// <summary>
/// Measures a folder subtree using the same one-syscall-per-directory fast path as the full
/// scanner, without building a node graph.
/// </summary>
/// <remarks>
/// This exists because "how big is this folder, and when was it last touched" is the single
/// question every reclaim detector asks, and answering it by running the full snapshot scanner
/// would allocate a <c>StorageNode</c> per file (~1 KB each) to then throw all of them away. Here
/// the walk keeps four longs and a timestamp.
/// <para>
/// The newest write time is deliberately computed over the whole subtree rather than read off the
/// folder itself: a directory's own timestamp only changes when its immediate children change, so
/// a repo whose deepest source file was edited this morning can look untouched for a year.
/// </para>
/// </remarks>
public static class DirectoryMeasurer
{
    /// <summary>
    /// Walks <paramref name="path"/> and returns its on-disk cost. Unreadable subfolders are
    /// skipped rather than thrown — a measurement that fails on one denied folder is still a far
    /// better answer than no measurement.
    /// </summary>
    public static DirectoryMeasurement Measure(
        string path,
        CancellationToken cancellationToken = default,
        int maxDepth = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return DirectoryMeasurement.Empty;
        }

        // A junction at the root is refused outright, exactly as one below the root already is.
        // Everything underneath lives on the other side of the link — frequently on another
        // volume — so measuring through it attributes someone else's bytes to this folder. Every
        // caller is a reclaim detector deciding what to offer for deletion and how much space to
        // promise back, and a redirected package cache is the ordinary way this happens: moving a
        // cache to the Dev Drive by hand leaves a junction behind on C: pointing at G:.
        if (IsReparsePoint(path))
        {
            return DirectoryMeasurement.Empty;
        }

        long allocated = 0;
        long apparent = 0;
        int files = 0;
        int folders = 0;
        DateTimeOffset? newest = null;

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((path, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string current, int depth) = stack.Pop();

            if (!NativeDirectoryEnumerator.TryEnumerate(current, out List<NativeDirEntry> entries))
            {
                MeasureManaged(current, ref allocated, ref apparent, ref files,
                    ref folders, ref newest, stack, depth, maxDepth);
                continue;
            }

            foreach (NativeDirEntry entry in entries)
            {
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                bool isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

                if (isDirectory)
                {
                    folders++;

                    // Reparse points are recorded but never followed: following a junction both
                    // risks an infinite loop and double-counts bytes that live somewhere else.
                    if (isReparsePoint || depth >= maxDepth)
                    {
                        continue;
                    }

                    stack.Push((Path.Combine(current, entry.Name), depth + 1));
                    continue;
                }

                files++;
                allocated += entry.AllocationSize;
                apparent += entry.EndOfFile;
                var written = new DateTimeOffset(entry.LastWriteTimeUtcTicks, TimeSpan.Zero);
                if (newest is null || written > newest)
                {
                    newest = written;
                }
            }
        }

        return new DirectoryMeasurement(allocated, apparent, files, folders, newest);
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> is a reparse point. An unreadable path answers
    /// <see langword="false"/> so the caller still attempts the walk, which degrades gracefully
    /// to an empty measurement on its own rather than through a second code path.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException or PathTooLongException)
        {
            return false;
        }
    }

    private static void MeasureManaged(
        string current,
        ref long allocated,
        ref long apparent,
        ref int files,
        ref int folders,
        ref DateTimeOffset? newest,
        Stack<(string Path, int Depth)> stack,
        int depth,
        int maxDepth)
    {
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    folders++;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || depth >= maxDepth)
                    {
                        continue;
                    }

                    stack.Push((info.FullName, depth + 1));
                    continue;
                }

                if (info is FileInfo file)
                {
                    files++;
                    allocated += NativeFileSize.GetAllocatedBytesOrApparent(file.FullName, file.Length);
                    apparent += file.Length;
                    var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                    if (newest is null || written > newest)
                    {
                        newest = written;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException or PathTooLongException)
        {
            // Whatever was counted before the failure is kept: a partial measurement is a far
            // better answer than none.
        }
    }
}
