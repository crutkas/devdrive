namespace DevDriveStorage.Live;

/// <summary>One directory entry, exposed for callers outside this assembly.</summary>
/// <param name="Name">File or folder name, no path.</param>
/// <param name="Attributes">Win32 attributes — check for Directory and ReparsePoint.</param>
/// <param name="AllocatedBytes">On-disk size including cluster slack. What deleting actually returns.</param>
/// <param name="ApparentBytes">Logical length. What the file claims to be.</param>
/// <param name="LastWriteUtc">Last write time.</param>
public readonly record struct FastDirEntry(
    string Name,
    FileAttributes Attributes,
    long AllocatedBytes,
    long ApparentBytes,
    DateTimeOffset LastWriteUtc)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}

/// <summary>
/// The public face of the fast one-syscall-per-directory enumeration used by the scanner.
/// </summary>
/// <remarks>
/// Exposed because every consumer that needs to walk a tree — the reclaim detectors, and whatever
/// subsystem comes next — should get the same on-disk numbers the Space room reports. Two walkers
/// with two different ideas of a file's size would eventually disagree in front of a user, and the
/// one that says "we found 40 GB" has to match the one that says "you now have 40 GB more".
/// </remarks>
public static class FastDirectoryWalker
{
    /// <summary>
    /// Lists the immediate children of <paramref name="path"/>. Returns false when the directory
    /// cannot be opened — callers should treat that as "skip", never as an error.
    /// </summary>
    public static bool TryList(string path, out IReadOnlyList<FastDirEntry> entries)
    {
        if (NativeDirectoryEnumerator.TryEnumerate(path, out List<NativeDirEntry> native))
        {
            entries =
            [
                .. native.Select(e => new FastDirEntry(
                    e.Name,
                    e.Attributes,
                    e.AllocationSize,
                    e.EndOfFile,
                    new DateTimeOffset(e.LastWriteTimeUtcTicks, TimeSpan.Zero)))
            ];
            return true;
        }

        return TryListManaged(path, out entries);
    }

    /// <summary>
    /// Walks every file beneath <paramref name="root"/>, yielding full paths and sizes. Reparse
    /// points are never followed, so a junction cannot cause a loop or double-count bytes that
    /// physically live somewhere else.
    /// </summary>
    public static IEnumerable<(string FullPath, FastDirEntry Entry)> EnumerateFiles(
        string root,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = stack.Pop();

            if (!TryList(current, out IReadOnlyList<FastDirEntry> entries))
            {
                continue;
            }

            foreach (FastDirEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!entry.IsReparsePoint)
                    {
                        stack.Push(Path.Combine(current, entry.Name));
                    }

                    continue;
                }

                yield return (Path.Combine(current, entry.Name), entry);
            }
        }
    }

    private static bool TryListManaged(string path, out IReadOnlyList<FastDirEntry> entries)
    {
        try
        {
            var results = new List<FastDirEntry>();
            foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                long apparent = info is FileInfo file ? file.Length : 0;
                long allocated = info is FileInfo f
                    ? NativeFileSize.GetAllocatedBytesOrApparent(f.FullName, f.Length)
                    : 0;

                results.Add(new FastDirEntry(
                    info.Name,
                    info.Attributes,
                    allocated,
                    apparent,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }

            entries = results;
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException or PathTooLongException)
        {
            entries = [];
            return false;
        }
    }
}
