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
    /// False when another program has the file open (an exclusive open is refused with a sharing violation). A program that
    /// only maps it, as llama.cpp does, isn't seen here; <see cref="DeleteTogether"/> sees it. Opens and closes at once.
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

    /// <summary>
    /// Deletes a folder tree and <paramref name="also"/> together or not at all (<see cref="DeleteTogether"/>), removing links
    /// rather than what they point to. Returns null when they're gone, or the file a program is using when nothing was deleted.
    /// </summary>
    /// <exception cref="IOException">A file couldn't be deleted for another reason, and nothing was; or an emptied folder couldn't be.</exception>
    public static string? DeleteFolder(string folder, IEnumerable<string>? also = null)
    {
        List<string> entries = [], folders = [];
        Collect(Path.TrimEndingDirectorySeparator(folder), entries, folders);
        if (DeleteTogether([.. entries, .. also ?? []]) is { } held)
        {
            return held;
        }

        foreach (string directory in folders)
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }

            info.Delete();
        }

        return null;
    }

    /// <summary>
    /// Deletes files and links (never what a link points to) together or not at all. Returns null when they're gone, or the
    /// one a program is using, and then deletes none. Each is opened exclusively and marked with the classic delete
    /// disposition, which Windows refuses while a program has the file mapped: llama.cpp maps a loaded model and closes its
    /// handle, and then an exclusive open, a rename and File.Delete's POSIX delete all succeed.
    /// </summary>
    /// <exception cref="IOException">One couldn't be opened for another reason (access denied, say); none was deleted.</exception>
    public static string? DeleteTogether(IEnumerable<string> paths)
    {
        var marked = new List<SafeFileHandle>();
        try
        {
            foreach (string path in paths)
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }

                SafeFileHandle handle = CreateFileW(path, Delete, 0, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error is SharingViolation or LockViolation)
                    {
                        return path;
                    }

                    string message = $"{Path.GetFileName(path)}: {new System.ComponentModel.Win32Exception(error).Message}";
                    throw error == AccessDenied ? new UnauthorizedAccessException(message) : new IOException(message, unchecked((int)0x80070000) | error);
                }

                marked.Add(handle);
                if (!SetDisposition(handle, true))
                {
                    marked.Remove(handle);
                    handle.Dispose();
                    return path;
                }
            }

            // Every one is marked: closing the handles deletes them.
            foreach (SafeFileHandle handle in marked)
            {
                handle.Dispose();
            }

            marked.Clear();
            return null;
        }
        finally
        {
            foreach (SafeFileHandle handle in marked)
            {
                SetDisposition(handle, false);
                handle.Dispose();
            }
        }
    }

    /// <summary>Files and links under <paramref name="folder"/>, and its folders deepest first; a linked folder is a link.</summary>
    private static void Collect(string folder, List<string> entries, List<string> folders)
    {
        var info = new DirectoryInfo(folder);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            entries.Add(info.FullName);
            return;
        }

        foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry is DirectoryInfo directory && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                Collect(directory.FullName, entries, folders);
            }
            else
            {
                entries.Add(entry.FullName);
            }
        }

        folders.Add(info.FullName);
    }

    private static bool SetDisposition(SafeFileHandle handle, bool delete)
    {
        byte value = delete ? (byte)1 : (byte)0;
        return SetFileInformationByHandle(handle, FileDispositionInfo, ref value, 1);
    }

    private static SafeFileHandle OpenForAttributes(string path) =>
        CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);

    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileDispositionInfo = 4;
    private const int AccessDenied = 5;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
