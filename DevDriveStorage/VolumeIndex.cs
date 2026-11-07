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

/// <summary>
/// The index over <see cref="ParallelVolumeScanner"/>'s finished walk (after <see cref="ParallelVolumeScanner.RollupIndex"/>),
/// copied into flat arrays so the walk's folders can go once the snapshot is built: a folder's answers take about fifty bytes
/// here, a <see cref="ScanFolder"/> about three hundred, and the index lives while the Reclaim scan reads it.
/// </summary>
/// <remarks>
/// Folders are numbered depth first, so a folder's subtree is the run of <see cref="_subtree"/> folders that starts at it:
/// its first child is the next folder, and each next child follows the previous child's subtree.
/// </remarks>
internal sealed class ScannedVolumeIndex : IVolumeIndex
{
    private const FileAttributes JunctionAttributes = FileAttributes.Directory | FileAttributes.ReparsePoint;

    private const byte Measured = 1, Incomplete = 2, TreeComplete = 4, GitFolder = 8, GitFile = 16;

    private readonly string _root;
    private readonly string[] _names;
    private readonly int[] _subtree;
    private readonly FileAttributes[] _attributes;
    private readonly byte[] _flags;
    private readonly long[] _allocated;
    private readonly long[] _logical;
    private readonly long[] _newest;
    private readonly int[] _files;
    private readonly int[] _folders;
    private readonly Dictionary<int, string[]> _reparseLeaves = [];
    private readonly Dictionary<int, ScanLeaf[]> _largeFiles = [];

    public ScannedVolumeIndex(ScanFolder root, long largeFileBytes)
    {
        _root = root.Path;
        LargeFileBytes = largeFileBytes;

        var order = new List<ScanFolder>();
        var parents = new List<int>();
        var stack = new Stack<(ScanFolder Folder, int Parent)>();
        stack.Push((root, -1));
        while (stack.Count > 0)
        {
            (ScanFolder folder, int parent) = stack.Pop();
            parents.Add(parent);
            order.Add(folder);
            int index = order.Count - 1;
            List<ScanFolder>? children = folder.Subfolders;
            for (int i = (children?.Count ?? 0) - 1; i >= 0; i--)
            {
                stack.Push((children![i], index));
            }
        }

        int count = order.Count;
        _names = new string[count];
        _subtree = new int[count];
        _attributes = new FileAttributes[count];
        _flags = new byte[count];
        _allocated = new long[count];
        _logical = new long[count];
        _newest = new long[count];
        _files = new int[count];
        _folders = new int[count];
        for (int i = 0; i < count; i++)
        {
            ScanFolder folder = order[i];
            _names[i] = folder.Name;
            _subtree[i] = 1;
            _attributes[i] = folder.Attributes;
            _flags[i] = (byte)((folder.IsMeasured ? Measured : 0) | (folder.Incomplete ? Incomplete : 0) | (folder.TreeComplete ? TreeComplete : 0)
                | folder.Git switch { GitEntry.Folder => GitFolder, GitEntry.File => GitFile, _ => 0 });
            _allocated[i] = folder.TreeAllocatedAll;
            _logical[i] = folder.TreeLogicalAll;
            _newest[i] = folder.TreeNewestFileTicks;
            _files[i] = folder.TreeFiles;
            _folders[i] = folder.TreeFolders;
            if (folder.ReparseLeaves is { Count: > 0 } leaves)
            {
                _reparseLeaves[i] = [.. leaves.Select(l => l.Name)];
            }

            if (folder.LargeFiles is { Count: > 0 } large)
            {
                _largeFiles[i] = [.. large];
            }
        }

        for (int i = count - 1; i > 0; i--)
        {
            _subtree[parents[i]] += _subtree[i];
        }
    }

    public string Root => _root;

    public long LargeFileBytes { get; }

    public bool TryMeasure(string path, out DirectoryMeasurement measurement)
    {
        measurement = DirectoryMeasurement.Empty;
        if (Find(path) is not int folder || (_flags[folder] & TreeComplete) == 0)
        {
            return false;
        }

        // DirectoryMeasurer refuses a reparse point at the root: its bytes live somewhere else.
        if (IsReparse(folder))
        {
            return true;
        }

        measurement = new DirectoryMeasurement(
            _allocated[folder],
            _logical[folder],
            _files[folder],
            _folders[folder],
            _newest[folder] == 0 ? null : new DateTimeOffset(_newest[folder], TimeSpan.Zero));
        return true;
    }

    public bool TryList(string path, out VolumeIndexListing listing)
    {
        listing = new VolumeIndexListing([], GitEntry.None);
        if (Find(path) is not int folder || (_flags[folder] & (Measured | Incomplete)) != Measured)
        {
            return false;
        }

        var folders = new List<VolumeIndexFolder>();
        foreach (int child in Children(folder))
        {
            folders.Add(new VolumeIndexFolder(_names[child], _attributes[child] | FileAttributes.Directory));
        }

        foreach (string leaf in _reparseLeaves.GetValueOrDefault(folder) ?? [])
        {
            folders.Add(new VolumeIndexFolder(leaf, JunctionAttributes));
        }

        GitEntry git = (_flags[folder] & GitFolder) != 0 ? GitEntry.Folder : (_flags[folder] & GitFile) != 0 ? GitEntry.File : GitEntry.None;
        listing = new VolumeIndexListing(folders, git);
        return true;
    }

    public bool TryFindLargeFiles(string path, out IReadOnlyList<(string FullPath, FastDirEntry Entry)> files)
    {
        files = [];
        if (Find(path) is not int start || (_flags[start] & TreeComplete) == 0 || IsReparse(start))
        {
            return false;
        }

        var found = new List<(string, FastDirEntry)>();
        var stack = new Stack<(int Folder, string Path)>();
        stack.Push((start, System.IO.Path.GetFullPath(path).TrimEnd('\\', '/')));
        while (stack.Count > 0)
        {
            (int folder, string folderPath) = stack.Pop();
            foreach (ScanLeaf file in _largeFiles.GetValueOrDefault(folder) ?? [])
            {
                found.Add((System.IO.Path.Combine(folderPath, file.Name), new FastDirEntry(
                    file.Name, FileAttributes.Normal, file.Allocated, file.Logical, new DateTimeOffset(file.LastWriteTicks, TimeSpan.Zero))));
            }

            foreach (int child in Children(folder))
            {
                if (!IsReparse(child))
                {
                    stack.Push((child, System.IO.Path.Combine(folderPath, _names[child])));
                }
            }
        }

        files = found;
        return true;
    }

    private IEnumerable<int> Children(int folder)
    {
        int end = folder + _subtree[folder];
        for (int child = folder + 1; child < end; child += _subtree[child])
        {
            yield return child;
        }
    }

    private bool IsReparse(int folder) => (_attributes[folder] & FileAttributes.ReparsePoint) != 0;

    /// <summary>The walk's folder at <paramref name="path"/>, or null when the walk didn't reach it.</summary>
    private int? Find(string path)
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

        string rootPath = _root.TrimEnd('\\', '/');
        string trimmed = full.TrimEnd('\\', '/');
        if (trimmed.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (!trimmed.StartsWith(rootPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int current = 0;
        foreach (string segment in trimmed[(rootPath.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar))
        {
            int? next = null;
            foreach (int child in Children(current))
            {
                if (_names[child].Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is not int found)
            {
                return null;
            }

            current = found;
        }

        return current;
    }
}