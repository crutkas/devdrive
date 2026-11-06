using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevDriveReclaim.Providers.Models;

/// <summary>What a model's files cost on disk, counted so that nothing is promised twice.</summary>
/// <param name="Bytes">Bytes removing them gives back: regular files with one name only.</param>
/// <param name="SharedBytes">Bytes in files that have another name elsewhere (a hard link), which removing these doesn't free.</param>
/// <param name="Files">The regular files, for the in-use check.</param>
/// <param name="Links">Symbolic links found, as (link, target) pairs; their own size is nothing.</param>
internal sealed record ModelFootprint(long Bytes, long SharedBytes, IReadOnlyList<string> Files, IReadOnlyList<(string Link, string Target)> Links);

/// <summary>File facts the model readers need: allocation, hard-link count, whether something has a file open, link targets.</summary>
internal static class ModelFiles
{
    /// <summary>Files smaller than this aren't checked for hard links or held handles: a model's weight is in its big files.</summary>
    internal const long LargeFileBytes = 1L << 20;

    /// <summary>
    /// Walks a model's folder without following links. Regular files count at their allocation, except a large file with
    /// another hard link, which counts as shared; a symbolic link counts nothing and is listed with its target.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder, or one under it, can't be read.</exception>
    public static ModelFootprint Measure(string folder, CancellationToken cancellationToken)
    {
        long bytes = 0, shared = 0;
        List<string> files = [];
        List<(string, string)> links = [];
        var pending = new Stack<string>();
        pending.Push(folder);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (FileSystemInfo entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is FileInfo && entry.LinkTarget is { } target)
                    {
                        links.Add((entry.FullName, target));
                    }

                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory.FullName);
                    continue;
                }

                var file = (FileInfo)entry;
                long size = Allocated(file.FullName, file.Length);
                files.Add(file.FullName);
                if (file.Length >= LargeFileBytes && LinkCount(file.FullName) > 1)
                {
                    shared += size;
                }
                else
                {
                    bytes += size;
                }
            }
        }

        return new ModelFootprint(bytes, shared, files, links);
    }

    /// <summary>Bytes a file occupies on disk (compressed or sparse files occupy less than their length).</summary>
    public static long Allocated(string path, long length)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        return low == uint.MaxValue && Marshal.GetLastWin32Error() != 0 ? length : ((long)high << 32) | low;
    }

    /// <summary>How many names a file has (hard links). 1 when it can't be read.</summary>
    public static int LinkCount(string path)
    {
        using SafeFileHandle handle = OpenForAttributes(path);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out ByHandleFileInformation info) ? (int)info.NumberOfLinks : 1;
    }

    /// <summary>
    /// False when another program has the file open or mapped (an exclusive open is refused with a sharing violation), so
    /// deleting it would fail or pull it from under that program. Opens and closes at once; reads nothing.
    /// </summary>
    public static bool IsFree(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, GenericRead, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        return error is not (SharingViolation or LockViolation);
    }

    /// <summary>The first of <paramref name="paths"/> that another program holds, or null when none is held.</summary>
    public static string? FirstHeld(IEnumerable<string> paths, long minimumBytes = LargeFileBytes)
    {
        foreach (string path in paths)
        {
            try
            {
                if (new FileInfo(path) is { Exists: true } info && info.Length >= minimumBytes && !IsFree(path))
                {
                    return path;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable isn't held; removing it reports its own failure.
            }
        }

        return null;
    }

    /// <summary>Deletes a folder tree, clearing read-only attributes and removing links rather than what they point to.</summary>
    public static void DeleteTree(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists)
        {
            return;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete();
            return;
        }

        foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry is DirectoryInfo directory)
            {
                DeleteTree(directory.FullName);
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            entry.Delete();
        }

        if ((info.Attributes & FileAttributes.ReadOnly) != 0)
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
        }

        info.Delete();
    }

    /// <summary>
    /// Renames <paramref name="folder"/> to a hidden sibling and returns the new path. Windows refuses to rename a folder while
    /// a program has a file in it open or mapped, so this is the gate that keeps a model in use from being half-deleted.
    /// </summary>
    public static string StageForDeletion(string folder)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(folder);
        string staged = Path.Combine(Path.GetDirectoryName(trimmed)!, $".{Path.GetFileName(trimmed)}.{Guid.NewGuid():N}.removing");
        Directory.Move(trimmed, staged);
        return staged;
    }

    private static SafeFileHandle OpenForAttributes(string path) =>
        CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
