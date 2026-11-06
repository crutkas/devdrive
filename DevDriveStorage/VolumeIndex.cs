using DevDriveStorage.Live;

namespace DevDriveStorage;

/// <summary>What a folder's listing said about an entry named <c>.git</c>.</summary>
public enum GitEntry
{
    None,

    /// <summary>A repository's own git folder.</summary>
    Folder,

    /// <summary>A worktree's pointer to its repository's git folder.</summary>
    File,
}

/// <summary>A subfolder as its parent's listing showed it.</summary>
public readonly record struct VolumeIndexFolder(string Name, FileAttributes Attributes);

/// <summary>A folder as one walk listed it: its subfolders (junctions included) and its <c>.git</c> entry.</summary>
public sealed record VolumeIndexListing(IReadOnlyList<VolumeIndexFolder> Folders, GitEntry Git);

/// <summary>
/// What one walk of a volume read, for code that would otherwise read the same folders again: the Reclaim detectors measure
/// repositories, build outputs and caches, look for repositories and list large files under the same roots the Space walk
/// just read.
/// </summary>
/// <remarks>
/// Each answer is the one the matching single-purpose walker would give (<see cref="DirectoryMeasurer"/>,
/// <see cref="FastDirectoryWalker"/>, a folder listing): counted the way it counts, hard links included and reparse folders
/// not followed. A question about a folder the walk didn't read completely (denied, cut short, or outside the walk) is
/// declined, so the caller reads the disk and meets the denial itself.
/// </remarks>
public interface IVolumeIndex
{
    /// <summary>The folder the walk started at, such as <c>C:\</c>.</summary>
    string Root { get; }

    /// <summary>The smallest file <see cref="TryFindLargeFiles"/> lists.</summary>
    long LargeFileBytes { get; }

    /// <summary><paramref name="path"/>'s measurement as <see cref="DirectoryMeasurer.Measure"/> takes it.</summary>
    bool TryMeasure(string path, out DirectoryMeasurement measurement);

    /// <summary><paramref name="path"/>'s subfolders and <c>.git</c> entry.</summary>
    bool TryList(string path, out VolumeIndexListing listing);

    /// <summary>
    /// Every file of at least <see cref="LargeFileBytes"/> beneath <paramref name="path"/>, as
    /// <see cref="FastDirectoryWalker.EnumerateFiles"/> reaches them.
    /// </summary>
    bool TryFindLargeFiles(string path, out IReadOnlyList<(string FullPath, FastDirEntry Entry)> files);
}

/// <summary>The index over <see cref="ParallelVolumeScanner"/>'s finished walk (after <see cref="ParallelVolumeScanner.RollupIndex"/>).</summary>
internal sealed class ScannedVolumeIndex(ScanFolder root, long largeFileBytes) : IVolumeIndex
{
    private const FileAttributes JunctionAttributes = FileAttributes.Directory | FileAttributes.ReparsePoint;

    public string Root => root.Path;

    public long LargeFileBytes => largeFileBytes;

    public bool TryMeasure(string path, out DirectoryMeasurement measurement)
    {
        measurement = DirectoryMeasurement.Empty;
        if (Find(path) is not { TreeComplete: true } folder)
        {
            return false;
        }

        // DirectoryMeasurer refuses a reparse point at the root: its bytes live somewhere else.
        if (IsReparse(folder))
        {
            return true;
        }

        measurement = new DirectoryMeasurement(
            folder.TreeAllocatedAll,
            folder.TreeLogicalAll,
            folder.TreeFiles,
            folder.TreeFolders,
            folder.TreeNewestFileTicks == 0 ? null : new DateTimeOffset(folder.TreeNewestFileTicks, TimeSpan.Zero));
        return true;
    }

    public bool TryList(string path, out VolumeIndexListing listing)
    {
        listing = new VolumeIndexListing([], GitEntry.None);
        if (Find(path) is not { IsMeasured: true, Incomplete: false } folder)
        {
            return false;
        }

        var folders = new List<VolumeIndexFolder>((folder.Subfolders?.Count ?? 0) + (folder.ReparseLeaves?.Count ?? 0));
        foreach (ScanFolder child in folder.Subfolders ?? [])
        {
            folders.Add(new VolumeIndexFolder(child.Name, child.Attributes | FileAttributes.Directory));
        }

        foreach (ScanLeaf leaf in folder.ReparseLeaves ?? [])
        {
            folders.Add(new VolumeIndexFolder(leaf.Name, JunctionAttributes));
        }

        listing = new VolumeIndexListing(folders, folder.Git);
        return true;
    }

    public bool TryFindLargeFiles(string path, out IReadOnlyList<(string FullPath, FastDirEntry Entry)> files)
    {
        files = [];
        if (Find(path) is not { TreeComplete: true } start || IsReparse(start))
        {
            return false;
        }

        var found = new List<(string, FastDirEntry)>();
        var stack = new Stack<ScanFolder>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            ScanFolder folder = stack.Pop();
            foreach (ScanLeaf file in folder.LargeFiles ?? [])
            {
                found.Add((System.IO.Path.Combine(folder.Path, file.Name), new FastDirEntry(
                    file.Name, FileAttributes.Normal, file.Allocated, file.Logical, new DateTimeOffset(file.LastWriteTicks, TimeSpan.Zero))));
            }

            foreach (ScanFolder child in folder.Subfolders ?? [])
            {
                if (!IsReparse(child))
                {
                    stack.Push(child);
                }
            }
        }

        files = found;
        return true;
    }

    private static bool IsReparse(ScanFolder folder) => (folder.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>The walk's folder at <paramref name="path"/>, or null when the walk didn't reach it.</summary>
    private ScanFolder? Find(string path)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        string rootPath = root.Path.TrimEnd('\\', '/');
        string trimmed = full.TrimEnd('\\', '/');
        if (trimmed.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (!trimmed.StartsWith(rootPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ScanFolder? current = root;
        foreach (string segment in trimmed[(rootPath.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar))
        {
            current = current.Subfolders?.Find(f => f.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
