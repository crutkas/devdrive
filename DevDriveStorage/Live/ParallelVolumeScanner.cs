using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DevDriveStorage.Live;

/// <summary>One file (or unfollowed reparse folder) kept for display in a folder's top list.</summary>
internal readonly record struct ScanLeaf(
    string Name,
    long Allocated,
    long Logical,
    long LastWriteTicks,
    bool IsDirectory);

/// <summary>
/// One folder discovered by <see cref="ParallelVolumeScanner"/>. Files never get an object of their
/// own: each folder accumulates its direct totals and keeps only its biggest files by name.
/// </summary>
internal sealed class ScanFolder(ScanFolder? parent, string path, string name)
{
    public ScanFolder? Parent { get; } = parent;

    public string Path { get; } = path;

    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? path : name;

    /// <summary>Set once by the worker that walks this folder.</summary>
    public List<ScanFolder>? Subfolders { get; set; }

    /// <summary>Junctions, symlinks and mount points listed here but not followed. Rare.</summary>
    public List<ScanLeaf>? ReparseLeaves { get; set; }

    public TopFileList Files;

    /// <summary>Bytes of every direct file, tracked or not.</summary>
    public long SelfAllocated;

    public long SelfLogical;

    /// <summary>Direct files plus unfollowed reparse folders.</summary>
    public int SelfItemCount;

    public long SelfNewestTicks = DateTimeOffset.UnixEpoch.UtcTicks;

    public bool IsMeasured;

    // Rolled-up values. Recomputed from the Self* fields by Rollup, so it is safe to repeat.
    public long Allocated;

    public long Logical;

    public int ItemCount;

    public long NewestTicks;

    private Guid? _stableId;

    public Guid StableId => _stableId ??= LiveStorageSnapshotSource.StableId(Path);
}

/// <summary>
/// A bounded min-heap of a folder's biggest direct files. Names are only materialized when a file
/// enters the heap, so a folder with a hundred thousand entries allocates at most <c>capacity</c>
/// strings. Whatever falls out of the heap is folded into the <c>Untracked*</c> totals, which keeps
/// the folder's own sums exact.
/// </summary>
internal struct TopFileList
{
    public ScanLeaf[]? Items;
    public int Count;
    public int UntrackedCount;
    public long UntrackedAllocated;
    public long UntrackedLogical;
    public long UntrackedNewestTicks;

    public void Add(int capacity, ReadOnlySpan<char> name, long allocated, long logical, long ticks)
    {
        if (Count < capacity)
        {
            if (Items is null || Count == Items.Length)
            {
                int size = Items is null ? 8 : Math.Min(capacity, Items.Length * 2);
                Array.Resize(ref Items, size);
            }

            Items[Count] = new ScanLeaf(name.ToString(), allocated, logical, ticks, false);
            SiftUp(Count++);
            return;
        }

        if (capacity > 0 && allocated > Items![0].Allocated)
        {
            Untrack(Items[0].Allocated, Items[0].Logical, Items[0].LastWriteTicks);
            Items[0] = new ScanLeaf(name.ToString(), allocated, logical, ticks, false);
            SiftDown(0);
            return;
        }

        Untrack(allocated, logical, ticks);
    }

    private void Untrack(long allocated, long logical, long ticks)
    {
        UntrackedCount++;
        UntrackedAllocated += allocated;
        UntrackedLogical += logical;
        if (ticks > UntrackedNewestTicks)
        {
            UntrackedNewestTicks = ticks;
        }
    }

    private readonly void SiftUp(int index)
    {
        ScanLeaf[] items = Items!;
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (items[parent].Allocated <= items[index].Allocated)
            {
                return;
            }

            (items[parent], items[index]) = (items[index], items[parent]);
            index = parent;
        }
    }

    private readonly void SiftDown(int index)
    {
        ScanLeaf[] items = Items!;
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= Count)
            {
                return;
            }

            int smallest = left + 1 < Count && items[left + 1].Allocated < items[left].Allocated
                ? left + 1
                : left;
            if (items[index].Allocated <= items[smallest].Allocated)
            {
                return;
            }

            (items[index], items[smallest]) = (items[smallest], items[index]);
            index = smallest;
        }
    }
}

/// <summary>
/// Whole-tree totals produced by one scan, independent of how much of the tree is emitted.
/// </summary>
internal sealed class ScanTotals
{
    public long Files;
    public long HardLinkDuplicates;
    public long CloudOnlyFiles;
    public long CloudOnlyBytes;
    public long Allocated;
    public long Logical;
    public int FastPathFallbacks;
    public int IncompleteReads;
}

/// <summary>
/// The multi-threaded folder walk behind <see cref="LiveStorageSnapshotSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// One worker per logical core pulls folders off a shared queue. Each folder is opened once with
/// <c>CreateFileW(FILE_LIST_DIRECTORY | SYNCHRONIZE, FILE_FLAG_BACKUP_SEMANTICS)</c> and listed
/// with <c>NtQueryDirectoryFile</c> into a 256 KB per-thread buffer using
/// <c>FileIdFullDirectoryInformation</c>, falling back to <c>FileFullDirectoryInformation</c> where
/// the file system rejects the class. Every entry carries its name, length, on-disk allocation,
/// attributes, file id and (for reparse points) reparse tag, so no file is ever opened.
/// </para>
/// <para>
/// Name-surrogate reparse points (junctions, symlinks, mount points) are listed but not followed,
/// because their bytes live somewhere else. Other reparse folders are followed — OneDrive's
/// placeholders among them, whose tag reads back as zero from a directory listing. On NTFS a file
/// with several hard links is counted once by file id. Cloud-only placeholders allocate nothing and
/// are tallied separately.
/// </para>
/// <para>
/// Folder ids are handed out at discovery under one lock, so a child's id is always greater than
/// its parent's and the rollup is a single backward pass over the folder table.
/// </para>
/// </remarks>
internal sealed class ParallelVolumeScanner
{
    private const uint FileListDirectory = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const int FileFullDirectoryInformation = 2;
    private const int FileIdFullDirectoryInformation = 38;

    private const int StatusSuccess = 0;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);
    private const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
    private const int StatusInvalidParameter = unchecked((int)0xC000000D);
    private const int StatusNotSupported = unchecked((int)0xC00000BB);

    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const uint AttributeRecallOnDataAccess = 0x400000;
    private const uint ReparseTagNameSurrogate = 0x20000000;

    // FILE_FULL_DIR_INFORMATION and FILE_ID_FULL_DIR_INFORMATION share every field through EaSize.
    private const int OffsetNextEntry = 0;
    private const int OffsetLastWriteTime = 24;
    private const int OffsetEndOfFile = 40;
    private const int OffsetAllocationSize = 48;
    private const int OffsetFileAttributes = 56;
    private const int OffsetFileNameLength = 60;
    private const int OffsetEaSize = 64;
    private const int OffsetFileId = 72;
    private const int OffsetNameFull = 68;
    private const int OffsetNameIdFull = 80;

    internal const int BufferSize = 256 * 1024;

    private static readonly long EpochTicks = DateTimeOffset.UnixEpoch.UtcTicks;
    private static readonly Lazy<bool> BackupPrivilege = new(TryEnableBackupPrivilege);

    private readonly int _filesPerFolder;
    private readonly bool _forceManaged;
    private readonly bool _dedupeHardLinks;
    private readonly int _workerCount;
    private readonly CancellationToken _cancellationToken;

    private readonly List<ScanFolder> _folders = [];
    private readonly Lock _registryGate = new();
    private readonly ConcurrentQueue<ScanFolder> _queue = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly HashSet<long>[] _fileIdShards;
    private readonly ConcurrentBag<string> _deniedPaths = [];
    private readonly ScanTotals _totals = new();

    private int _pending;
    private volatile bool _done;
    private volatile bool _useIdClass = true;
    private Exception? _failure;

    public ParallelVolumeScanner(
        int filesPerFolder,
        bool forceManaged,
        bool dedupeHardLinks,
        CancellationToken cancellationToken,
        int? workerCount = null)
    {
        _filesPerFolder = filesPerFolder;
        _forceManaged = forceManaged;
        _dedupeHardLinks = dedupeHardLinks;
        _cancellationToken = cancellationToken;
        _workerCount = Math.Max(1, workerCount ?? Environment.ProcessorCount);
        _fileIdShards = dedupeHardLinks
            ? [.. Enumerable.Range(0, 64).Select(_ => new HashSet<long>())]
            : [];
    }

    /// <summary>Held shared by workers while they mutate a folder, and exclusively to freeze one.</summary>
    public ReaderWriterLockSlim? FreezeGate { get; init; }

    /// <summary>Raised on the worker thread after each folder completes, outside the freeze gate.</summary>
    public Action<ParallelVolumeScanner, ScanFolder>? FolderCompleted { get; init; }

    /// <summary>The scan root; set before any worker starts.</summary>
    public ScanFolder? Root { get; private set; }

    public IReadOnlyList<ScanFolder> Folders => _folders;

    public ScanTotals Totals => _totals;

    public IEnumerable<string> DeniedPaths => _deniedPaths;

    public int WorkerCount => _workerCount;

    /// <summary>Total bytes counted so far; safe to read while the walk runs.</summary>
    public long ProcessedBytes => Interlocked.Read(ref _totals.Allocated);

    /// <summary>Whether the process holds an enabled SeBackupPrivilege for this scan.</summary>
    public static bool HasBackupPrivilege => BackupPrivilege.Value;

    /// <summary>True when the current process token is elevated.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Walks everything beneath <paramref name="root"/>, blocking until done.</summary>
    public ScanFolder Run(string root, string rootName)
    {
        _ = BackupPrivilege.Value;

        var rootFolder = new ScanFolder(null, root, rootName);
        Root = rootFolder;
        _folders.Add(rootFolder);
        _pending = 1;
        _queue.Enqueue(rootFolder);
        _ready.Release();

        var threads = new Thread[_workerCount];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(Worker, 256 * 1024)
            {
                IsBackground = true,
                Name = $"DevDrive scan {i}",
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        if (_failure is not null)
        {
            ExceptionDispatchInfoThrow(_failure);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        return rootFolder;
    }

    /// <summary>
    /// Rolls direct totals up to every ancestor. Repeatable: each folder is reset to its own
    /// contribution first, which is what lets a streaming scan freeze and keep walking.
    /// </summary>
    public static void Rollup(IReadOnlyList<ScanFolder> folders)
    {
        foreach (ScanFolder folder in folders)
        {
            folder.Allocated = folder.SelfAllocated;
            folder.Logical = folder.SelfLogical;
            folder.ItemCount = folder.SelfItemCount;
            folder.NewestTicks = folder.SelfNewestTicks;
        }

        for (int i = folders.Count - 1; i >= 1; i--)
        {
            ScanFolder folder = folders[i];
            ScanFolder parent = folder.Parent!;
            parent.Allocated += folder.Allocated;
            parent.Logical += folder.Logical;
            parent.ItemCount += 1 + folder.ItemCount;
            if (folder.NewestTicks > parent.NewestTicks)
            {
                parent.NewestTicks = folder.NewestTicks;
            }
        }
    }

    private static void ExceptionDispatchInfoThrow(Exception exception) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();

    private void Worker()
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
        var discovered = new List<ScanFolder>();
        var local = new LocalTotals();

        try
        {
            while (true)
            {
                _ready.Wait();
                if (_done)
                {
                    break;
                }

                if (!_queue.TryDequeue(out ScanFolder? folder))
                {
                    continue;
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    Stop(null);
                    break;
                }

                FreezeGate?.EnterReadLock();
                try
                {
                    ProcessFolder(folder, buffer, discovered, ref local);
                }
                finally
                {
                    FreezeGate?.ExitReadLock();
                }

                FolderCompleted?.Invoke(this, folder);

                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    Stop(null);
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Stop(exception);
        }
        finally
        {
            local.MergeInto(_totals);
        }
    }

    private void Stop(Exception? failure)
    {
        if (failure is not null)
        {
            Interlocked.CompareExchange(ref _failure, failure, null);
        }

        _done = true;
        _ready.Release(_workerCount);
    }

    private void ProcessFolder(ScanFolder folder, byte[] buffer, List<ScanFolder> discovered, ref LocalTotals local)
    {
        discovered.Clear();
        bool listed = !_forceManaged && TryListNative(folder, buffer, discovered, ref local);
        if (!listed)
        {
            if (!TryListManaged(folder, discovered, ref local))
            {
                _deniedPaths.Add(folder.Path);
                return;
            }

            Interlocked.Increment(ref _totals.FastPathFallbacks);
        }

        folder.IsMeasured = true;
        if (discovered.Count == 0)
        {
            return;
        }

        folder.Subfolders = [.. discovered];
        lock (_registryGate)
        {
            foreach (ScanFolder child in discovered)
            {
                _folders.Add(child);
            }
        }

        Interlocked.Add(ref _pending, discovered.Count);
        foreach (ScanFolder child in discovered)
        {
            _queue.Enqueue(child);
        }

        _ready.Release(discovered.Count);
    }

    /// <summary>
    /// Lists one folder through <c>NtQueryDirectoryFile</c>. Returns false only when nothing was
    /// read, so the caller can retry through the managed enumerator.
    /// </summary>
    private bool TryListNative(ScanFolder folder, byte[] buffer, List<ScanFolder> discovered, ref LocalTotals local)
    {
        using SafeFileHandle handle = CreateFile(
            ToNativePath(folder.Path),
            FileListDirectory | Synchronize,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        IntPtr address = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
        bool restart = true;
        bool any = false;
        while (true)
        {
            bool idClass = _useIdClass;
            int status = NtQueryDirectoryFile(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out IoStatusBlock io,
                address,
                BufferSize,
                idClass ? FileIdFullDirectoryInformation : FileFullDirectoryInformation,
                false,
                IntPtr.Zero,
                restart);

            if (status == StatusSuccess)
            {
                any = true;
                restart = false;
                int length = (int)Math.Min((ulong)BufferSize, (ulong)io.Information);
                ParseBuffer(folder, buffer.AsSpan(0, length), idClass, discovered, ref local);
                continue;
            }

            if (status is StatusNoMoreFiles or StatusNoSuchFile)
            {
                return true;
            }

            if (!any && idClass &&
                status is StatusInvalidInfoClass or StatusInvalidParameter or StatusNotSupported)
            {
                _useIdClass = false;
                restart = true;
                continue;
            }

            if (any)
            {
                // Part of the folder was read. Keep it, and say the folder was not fully readable.
                Interlocked.Increment(ref _totals.IncompleteReads);
                _deniedPaths.Add(folder.Path);
                return true;
            }

            return false;
        }
    }

    private void ParseBuffer(
        ScanFolder folder,
        ReadOnlySpan<byte> buffer,
        bool idClass,
        List<ScanFolder> discovered,
        ref LocalTotals local)
    {
        int nameOffset = idClass ? OffsetNameIdFull : OffsetNameFull;
        int offset = 0;
        long selfAllocated = 0;
        long selfLogical = 0;
        int selfItems = 0;
        long newest = folder.SelfNewestTicks;

        while (true)
        {
            if (offset < 0 || offset > buffer.Length - nameOffset)
            {
                throw new InvalidDataException(
                    $"Directory record at offset {offset} does not fit the {buffer.Length}-byte buffer.");
            }

            ReadOnlySpan<byte> record = buffer[offset..];
            int next = Read<int>(record, OffsetNextEntry);
            uint attributes = Read<uint>(record, OffsetFileAttributes);
            int nameBytes = Read<int>(record, OffsetFileNameLength);
            if (nameBytes < 0 || nameBytes > record.Length - nameOffset)
            {
                throw new InvalidDataException(
                    $"Directory record at offset {offset} claims a {nameBytes}-byte name.");
            }

            ReadOnlySpan<char> name = MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameBytes));
            if (!IsDotEntry(name))
            {
                if ((attributes & AttributeDirectory) != 0)
                {
                    uint tag = Read<uint>(record, OffsetEaSize);
                    bool surrogate = (attributes & AttributeReparsePoint) != 0 &&
                        (tag & ReparseTagNameSurrogate) != 0;
                    if (surrogate)
                    {
                        AddReparseLeaf(folder, name.ToString(), FileTimeToTicks(Read<long>(record, OffsetLastWriteTime)));
                        selfItems++;
                    }
                    else
                    {
                        discovered.Add(NewChild(folder, name.ToString()));
                    }
                }
                else
                {
                    long allocated = Math.Max(0, Read<long>(record, OffsetAllocationSize));
                    long logical = Math.Max(0, Read<long>(record, OffsetEndOfFile));
                    long ticks = FileTimeToTicks(Read<long>(record, OffsetLastWriteTime));

                    if ((attributes & AttributeRecallOnDataAccess) != 0)
                    {
                        local.CloudOnlyFiles++;
                        local.CloudOnlyBytes += logical;
                    }

                    if (idClass && _dedupeHardLinks && !FirstSighting(Read<long>(record, OffsetFileId)))
                    {
                        local.HardLinkDuplicates++;
                        allocated = 0;
                        logical = 0;
                    }
                    else
                    {
                        local.Files++;
                    }

                    selfAllocated += allocated;
                    selfLogical += logical;
                    selfItems++;
                    if (ticks > newest)
                    {
                        newest = ticks;
                    }

                    folder.Files.Add(_filesPerFolder, name, allocated, logical, ticks);
                }
            }

            if (next == 0)
            {
                break;
            }

            if (next < 0 || next > buffer.Length - offset)
            {
                throw new InvalidDataException($"Directory record at offset {offset} points {next} bytes on.");
            }

            offset += next;
        }

        folder.SelfAllocated += selfAllocated;
        folder.SelfLogical += selfLogical;
        folder.SelfItemCount += selfItems;
        folder.SelfNewestTicks = newest;
        local.Logical += selfLogical;
        Interlocked.Add(ref _totals.Allocated, selfAllocated);
    }

    /// <summary>
    /// The managed fallback: the ReFS-class path when the native listing is refused, and the test
    /// seam behind <c>ForceManagedEnumeration</c>. Per-file work is acceptable here because it is
    /// only reached when the fast path is not.
    /// </summary>
    private bool TryListManaged(ScanFolder folder, List<ScanFolder> discovered, ref LocalTotals local)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(folder.Path).EnumerateFileSystemInfos()];
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }

        foreach (FileSystemInfo info in entries)
        {
            try
            {
                FileAttributes attributes = info.Attributes;
                long ticks = SafeTicks(info);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
                    {
                        AddReparseLeaf(folder, info.Name, ticks);
                        folder.SelfItemCount++;
                    }
                    else
                    {
                        discovered.Add(NewChild(folder, info.Name));
                    }

                    continue;
                }

                long logical = info is FileInfo file ? Math.Max(0, file.Length) : 0;
                long allocated = NativeFileSize.GetAllocatedBytesOrApparent(info.FullName, logical);
                if (((uint)attributes & AttributeRecallOnDataAccess) != 0)
                {
                    local.CloudOnlyFiles++;
                    local.CloudOnlyBytes += logical;
                }

                local.Files++;
                local.Logical += logical;
                Interlocked.Add(ref _totals.Allocated, allocated);
                folder.SelfAllocated += allocated;
                folder.SelfLogical += logical;
                folder.SelfItemCount++;
                folder.SelfNewestTicks = Math.Max(folder.SelfNewestTicks, ticks);
                folder.Files.Add(_filesPerFolder, info.Name, allocated, logical, ticks);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // One unreadable entry should not lose the rest of the folder.
            }
        }

        return true;
    }

    private static void AddReparseLeaf(ScanFolder folder, string name, long ticks)
    {
        (folder.ReparseLeaves ??= []).Add(new ScanLeaf(name, 0, 0, ticks, true));
        if (ticks > folder.SelfNewestTicks)
        {
            folder.SelfNewestTicks = ticks;
        }
    }

    private static ScanFolder NewChild(ScanFolder parent, string name) =>
        new(parent, System.IO.Path.Combine(parent.Path, name), name);

    private bool FirstSighting(long fileId)
    {
        if (fileId == 0)
        {
            return true;
        }

        HashSet<long> shard = _fileIdShards[(int)((ulong)(fileId * unchecked((long)0x9E3779B97F4A7C15)) >> 58)];
        lock (shard)
        {
            return shard.Add(fileId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(ReadOnlySpan<byte> record, int offset)
        where T : unmanaged =>
        MemoryMarshal.Read<T>(record[offset..]);

    private static bool IsDotEntry(ReadOnlySpan<char> name) =>
        name.Length == 0 ||
        (name.Length == 1 && name[0] == '.') ||
        (name.Length == 2 && name[0] == '.' && name[1] == '.');

    private static long FileTimeToTicks(long fileTime)
    {
        // FILETIME counts 100 ns from 1601; DateTime ticks count 100 ns from year 1.
        const long FileTimeOffset = 504911232000000000;
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.Ticks - FileTimeOffset)
        {
            return EpochTicks;
        }

        return fileTime + FileTimeOffset;
    }

    private static long SafeTicks(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception exception) when (IsRecoverable(exception) || exception is ArgumentOutOfRangeException)
        {
            return EpochTicks;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or
            System.Security.SecurityException or PathTooLongException;

    /// <summary>Adds the <c>\\?\</c> prefix so deep trees are not cut off at MAX_PATH.</summary>
    private static string ToNativePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    /// <summary>
    /// Enables SeBackupPrivilege when the process is elevated, so backup-semantics opens can list
    /// folders whose ACLs would otherwise deny an administrator. A standard user simply does not
    /// hold the privilege; those folders are counted as unreadable instead.
    /// </summary>
    private static bool TryEnableBackupPrivilege()
    {
        try
        {
            if (!IsElevated ||
                !OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            {
                return false;
            }

            try
            {
                if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out long luid))
                {
                    return false;
                }

                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) &&
                    Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    /// <summary>Per-worker counters, merged once when the worker exits.</summary>
    private struct LocalTotals
    {
        public long Files;
        public long HardLinkDuplicates;
        public long CloudOnlyFiles;
        public long CloudOnlyBytes;
        public long Logical;

        public readonly void MergeInto(ScanTotals totals)
        {
            Interlocked.Add(ref totals.Files, Files);
            Interlocked.Add(ref totals.HardLinkDuplicates, HardLinkDuplicates);
            Interlocked.Add(ref totals.CloudOnlyFiles, CloudOnlyFiles);
            Interlocked.Add(ref totals.CloudOnlyBytes, CloudOnlyBytes);
            Interlocked.Add(ref totals.Logical, Logical);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
