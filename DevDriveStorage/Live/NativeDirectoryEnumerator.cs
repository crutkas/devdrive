using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevDriveStorage.Live;

/// <summary>One directory entry as returned by a single directory-enumeration syscall.</summary>
/// <param name="Name">The file or subdirectory name (no path).</param>
/// <param name="Attributes">The Win32 file attributes (directory, reparse point, etc.).</param>
/// <param name="AllocationSize">Bytes actually allocated on disk, including cluster slack.</param>
/// <param name="EndOfFile">The apparent (logical) length of the file.</param>
/// <param name="LastWriteTimeUtcTicks">Last-write time as UTC ticks.</param>
internal readonly record struct NativeDirEntry(
    string Name,
    FileAttributes Attributes,
    long AllocationSize,
    long EndOfFile,
    long LastWriteTimeUtcTicks);

/// <summary>
/// Enumerates a directory's immediate children with <c>GetFileInformationByHandleEx</c>, which
/// returns each entry's name, attributes, apparent length and — crucially — its on-disk
/// <c>AllocationSize</c> in a single syscall per directory. This replaces both
/// <see cref="DirectoryInfo.EnumerateFileSystemInfos()"/> and the per-file
/// <c>GetCompressedFileSizeW</c> call: it is roughly 2.5x faster and, unlike
/// <c>GetCompressedFileSizeW</c>, it captures cluster slack (real "size on disk"). Sparse and
/// compressed files still report a small allocation, so the analyzer's headline distinction is
/// preserved.
/// </summary>
internal static class NativeDirectoryEnumerator
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    // FILE_INFO_BY_HANDLE_CLASS values.
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;

    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreFiles = 18;

    // FILE_ID_BOTH_DIR_INFO field offsets (x64 layout), verified against the SDK header.
    private const int OffsetNextEntry = 0;
    private const int OffsetLastWriteTime = 24;
    private const int OffsetEndOfFile = 40;
    private const int OffsetAllocationSize = 48;
    private const int OffsetFileAttributes = 56;
    private const int OffsetFileNameLength = 60;
    private const int OffsetFileName = 104;

    private const int BufferSize = 64 * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int fileInformationClass, IntPtr lpFileInformation, uint dwBufferSize);

    /// <summary>
    /// Enumerates the immediate children of <paramref name="directoryPath"/>. Returns
    /// <see langword="false"/> when the directory cannot be opened or read (denied, gone, or an
    /// unexpected marshalling failure) so the caller can fall back to managed enumeration; on
    /// failure <paramref name="entries"/> is empty and nothing is partially reported.
    /// </summary>
    public static bool TryEnumerate(string directoryPath, out List<NativeDirEntry> entries)
    {
        entries = [];

        SafeFileHandle handle = CreateFile(
            directoryPath,
            FileListDirectory,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var collected = new List<NativeDirEntry>();
            int infoClass = FileIdBothDirectoryRestartInfo;
            while (GetFileInformationByHandleEx(handle, infoClass, buffer, BufferSize))
            {
                infoClass = FileIdBothDirectoryInfo;
                ParseBuffer(buffer, collected);
            }

            int error = Marshal.GetLastWin32Error();
            if (error is not (ErrorNoMoreFiles or ErrorSuccess))
            {
                return false;
            }

            entries = collected;
            return true;
        }
        catch (Exception)
        {
            // Any unexpected marshalling failure falls back to the fully-correct managed path.
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            handle.Dispose();
        }
    }

    private static void ParseBuffer(IntPtr buffer, List<NativeDirEntry> entries)
    {
        int offset = 0;
        while (true)
        {
            IntPtr record = buffer + offset;
            int nextEntry = Marshal.ReadInt32(record, OffsetNextEntry);
            long lastWrite = Marshal.ReadInt64(record, OffsetLastWriteTime);
            long endOfFile = Marshal.ReadInt64(record, OffsetEndOfFile);
            long allocation = Marshal.ReadInt64(record, OffsetAllocationSize);
            uint attributes = unchecked((uint)Marshal.ReadInt32(record, OffsetFileAttributes));
            int nameLengthBytes = Marshal.ReadInt32(record, OffsetFileNameLength);

            string name = nameLengthBytes > 0
                ? Marshal.PtrToStringUni(record + OffsetFileName, nameLengthBytes / 2) ?? string.Empty
                : string.Empty;

            if (name is not ("" or "." or ".."))
            {
                entries.Add(new NativeDirEntry(
                    name,
                    (FileAttributes)attributes,
                    Math.Max(0, allocation),
                    Math.Max(0, endOfFile),
                    SafeFileTimeToUtcTicks(lastWrite)));
            }

            if (nextEntry == 0)
            {
                return;
            }

            offset += nextEntry;
        }
    }

    private static long SafeFileTimeToUtcTicks(long fileTime)
    {
        if (fileTime <= 0)
        {
            return DateTimeOffset.UnixEpoch.UtcTicks;
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch.UtcTicks;
        }
    }
}
