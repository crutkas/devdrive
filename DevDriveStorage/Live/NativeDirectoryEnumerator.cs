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

    // FILE_INFO_BY_HANDLE_CLASS values. The extended classes (FILE_ID_EXTD_DIR_INFO) carry a
    // 128-bit FileId and are the ReFS-native shape; the "Both" classes carry only a 64-bit FileId,
    // which some ReFS configurations reject — dropping us silently onto the slow managed fallback.
    // We therefore try the extended class first and fall back to the Both class, then to managed.
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int FileIdExtdDirectoryInfo = 19;
    private const int FileIdExtdDirectoryRestartInfo = 20;

    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreFiles = 18;

    // Shared field offsets are byte-identical between FILE_ID_BOTH_DIR_INFO and
    // FILE_ID_EXTD_DIR_INFO through EaSize (offset 64); only the trailing FileName offset differs
    // (the extended struct has a wider FileId and no short name). Verified against the SDK header.
    private const int OffsetNextEntry = 0;
    private const int OffsetLastWriteTime = 24;
    private const int OffsetEndOfFile = 40;
    private const int OffsetAllocationSize = 48;
    private const int OffsetFileAttributes = 56;
    private const int OffsetFileNameLength = 60;
    private const int OffsetFileNameBoth = 104;
    private const int OffsetFileNameExtd = 88;

    private const int BufferSize = 64 * 1024;

    /// <summary>Ordered enumeration strategies: extended (ReFS-safe) first, then Both.</summary>
    private static readonly (int Restart, int Continue, int NameOffset)[] Strategies =
    [
        (FileIdExtdDirectoryRestartInfo, FileIdExtdDirectoryInfo, OffsetFileNameExtd),
        (FileIdBothDirectoryRestartInfo, FileIdBothDirectoryInfo, OffsetFileNameBoth),
    ];

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
            foreach ((int restart, int continueClass, int nameOffset) in Strategies)
            {
                var collected = new List<NativeDirEntry>();
                bool any = false;
                int infoClass = restart;
                while (GetFileInformationByHandleEx(handle, infoClass, buffer, BufferSize))
                {
                    any = true;
                    infoClass = continueClass;
                    ParseBuffer(buffer, nameOffset, collected);
                }

                int error = Marshal.GetLastWin32Error();
                if (any)
                {
                    // This info class was accepted. NO_MORE_FILES is the clean end; any other
                    // error mid-enumeration means an incomplete read, so fall back to managed.
                    if (error is ErrorNoMoreFiles or ErrorSuccess)
                    {
                        entries = collected;
                        return true;
                    }

                    return false;
                }

                // The very first call failed (e.g. ERROR_INVALID_PARAMETER when the filesystem
                // rejects this info class). Try the next strategy on the same handle; a restart
                // class always re-reads from the beginning.
            }

            return false;
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

    /// <summary>
    /// Walks one buffer of variable-length records. Every offset the kernel hands back is validated
    /// against the buffer before it is used: <c>NextEntryOffset</c> is a <c>ULONG</c> read as a
    /// signed <c>int</c>, so a hostile or buggy filesystem returning ≥ 0x80000000 would otherwise
    /// walk the cursor backwards, and an over-long <c>FileNameLength</c> would read past the end.
    /// Neither produces a catchable exception — <see cref="Marshal.PtrToStringUni(IntPtr, int)"/>
    /// off the end of an <see cref="Marshal.AllocHGlobal(int)"/> block raises an
    /// <see cref="AccessViolationException"/>, which .NET treats as corrupted state and does not
    /// deliver to managed handlers, so the caller's "fall back to the managed path" contract could
    /// not hold and the process would simply die mid-scan.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A record does not fit the buffer. Thrown rather than returning what was read so far, because
    /// a silently truncated directory listing undercounts sizes without ever saying so; the caller
    /// catches this and re-reads the directory through the fully-correct managed enumerator.
    /// </exception>
    private static void ParseBuffer(IntPtr buffer, int nameOffset, List<NativeDirEntry> entries)
    {
        int offset = 0;
        while (true)
        {
            // nameOffset (88 or 104) is past every fixed field, so this covers the header too.
            if (offset < 0 || offset > BufferSize - nameOffset)
            {
                throw new InvalidDataException(
                    $"Directory record at offset {offset} does not fit the {BufferSize}-byte buffer.");
            }

            IntPtr record = buffer + offset;
            int nextEntry = Marshal.ReadInt32(record, OffsetNextEntry);
            long lastWrite = Marshal.ReadInt64(record, OffsetLastWriteTime);
            long endOfFile = Marshal.ReadInt64(record, OffsetEndOfFile);
            long allocation = Marshal.ReadInt64(record, OffsetAllocationSize);
            uint attributes = unchecked((uint)Marshal.ReadInt32(record, OffsetFileAttributes));
            int nameLengthBytes = Marshal.ReadInt32(record, OffsetFileNameLength);

            if (nameLengthBytes < 0 || nameLengthBytes > BufferSize - offset - nameOffset)
            {
                throw new InvalidDataException(
                    $"Directory record at offset {offset} claims a {nameLengthBytes}-byte name.");
            }

            string name = nameLengthBytes > 0
                ? Marshal.PtrToStringUni(record + nameOffset, nameLengthBytes / 2) ?? string.Empty
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

            // Strictly forward, and strictly inside the buffer. Subtraction rather than addition so
            // a large nextEntry cannot overflow the comparison it is meant to fail.
            if (nextEntry < 0 || nextEntry > BufferSize - offset)
            {
                throw new InvalidDataException(
                    $"Directory record at offset {offset} points {nextEntry} bytes on.");
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
