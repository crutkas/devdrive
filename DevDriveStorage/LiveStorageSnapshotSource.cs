using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DevDriveStorage.Live;

namespace DevDriveStorage;

/// <summary>
/// Scans a real volume or folder root and builds a <see cref="StorageSnapshot"/>. This
/// is the production swap for <see cref="MockStorageSnapshotSource"/>: it honours the
/// same <see cref="IStorageSnapshotSource"/> contract (scope, progress, cancellation,
/// partial coverage) so the view model and UI keep their meaning.
/// </summary>
/// <remarks>
/// The scan interprets <see cref="StorageSnapshotRequest.ScenarioId"/> as the root path
/// to walk. The walk itself is <see cref="ParallelVolumeScanner"/>: one worker per core, one
/// <c>NtQueryDirectoryFile</c> listing per folder and no per-file opens. It reports allocated-on-disk
/// bytes rather than apparent length, does not follow junctions, symlinks or mount points, counts
/// NTFS hard links once, and turns denied paths into reduced <see cref="ScanCoverage"/> rather than
/// silently omitting them.
/// </remarks>
public sealed class LiveStorageSnapshotSource : IStorageSnapshotSource
{
    /// <summary>Default cap on the number of direct children emitted per folder.</summary>
    public const int DefaultMaxChildrenPerFolder = 1024;

    private readonly int _maxChildrenPerFolder;
    private readonly TimeSpan _progressInterval;

    /// <summary>
    /// Number of directories on the most recent scan whose fast per-directory enumeration failed
    /// yet were still readable through the managed fallback. This isolates the fast path being
    /// rejected (a silent drop to the ~30x-slower path) from ordinary access denials, which
    /// surface separately in <see cref="ScanCoverage.DeniedPaths"/>. Expected to be 0; a non-zero
    /// value is a performance warning, not a correctness one.
    /// </summary>
    public int LastFastPathFallbackCount { get; private set; }

    /// <summary>The headline counts and timings of the most recent completed scan.</summary>
    public ScanStatistics? LastStatistics { get; private set; }

    /// <summary>
    /// Forces every directory down the managed enumeration path, as if the native fast path had
    /// been rejected. That fallback cannot otherwise be reached from a test running on NTFS, and a
    /// bug that only shows up on the path a refused volume takes is the worst kind to ship.
    /// </summary>
    public bool ForceManagedEnumeration { get; init; }

    /// <summary>Overrides the number of walker threads. Defaults to one per logical core.</summary>
    public int? WorkerCount { get; init; }

    public LiveStorageSnapshotSource(
        int maxChildrenPerFolder = DefaultMaxChildrenPerFolder,
        TimeSpan? progressInterval = null)
    {
        if (maxChildrenPerFolder < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxChildrenPerFolder),
                "At least two children are required to leave room for an aggregate node.");
        }

        _maxChildrenPerFolder = maxChildrenPerFolder;

        // Roughly ten updates a second keeps the UI thread responsive without swamping it.
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public Task<StorageSnapshot> GetSnapshotAsync(
        StorageSnapshotRequest request,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string rootPath = request.ScenarioId;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new StorageSnapshotSourceException("A live scan requires a root path.");
        }

        // The whole scan is blocking I/O; keep it off the calling (UI) thread.
        return Task.Run(() => Scan(request, rootPath, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Derives a node's identity from its path instead of minting a fresh one. A streaming scan
    /// emits the same folder many times as it grows, and a refresh emits it again later; random
    /// ids would make each emission a brand-new set of nodes, so the room would lose the user's
    /// scope, selection and expanded folders on every update. Paths are unique within a snapshot
    /// (the emitter dedupes them), so hashing one is enough to keep ids unique.
    /// </summary>
    internal static Guid StableId(string path)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(path.ToLowerInvariant().AsSpan()), digest);
        return new Guid(digest[..16]);
    }

    private StorageSnapshot Scan(
        StorageSnapshotRequest request,
        string rootPath,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new StorageSnapshotSourceException($"Invalid root path '{rootPath}'.");
        }

        if (!Directory.Exists(fullRoot))
        {
            throw new StorageSnapshotSourceException($"Root path '{fullRoot}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        long? referenceTotalBytes = TryGetVolumeUsedBytes(fullRoot);
        string? fileSystem = TryGetFileSystem(fullRoot);
        var context = new ScanContext(progress, referenceTotalBytes, _progressInterval);
        using ReaderWriterLockSlim? freezeGate = progress is null ? null : new ReaderWriterLockSlim();

        var scanner = new ParallelVolumeScanner(
            _maxChildrenPerFolder,
            ForceManagedEnumeration,
            dedupeHardLinks: string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase),
            cancellationToken,
            WorkerCount)
        {
            FreezeGate = freezeGate,
            FolderCompleted = (walker, folder) =>
                OnFolderCompleted(walker, folder, request, context, freezeGate, referenceTotalBytes),
        };

        var query = Stopwatch.StartNew();
        ScanFolder root = scanner.Run(fullRoot, DescribeRootName(fullRoot));
        query.Stop();

        ParallelVolumeScanner.Rollup(scanner.Folders);
        ImmutableArray<StorageNode> nodes = Emit(root, correlateProviders: true, foldersOnly: false);

        long covered = root.Allocated;
        long totalBytes = Math.Max(referenceTotalBytes ?? covered, covered);
        (string[] deniedPaths, string[] excludedPaths) = Classify(scanner.DeniedPaths);

        // Only *unexpected* denials degrade the result. OS-owned exclusions are reported separately so
        // "Complete — 1 system folder excluded" stays truthful and the Partial signal keeps its meaning.
        bool partial = deniedPaths.Length > 0 ||
            (referenceTotalBytes is long reference && covered < reference - reference / 1000);

        ScanTotals totals = scanner.Totals;
        LastFastPathFallbackCount = totals.FastPathFallbacks;
        if (LastFastPathFallbackCount > 0 && !ForceManagedEnumeration)
        {
            // Observable rather than silent: if this fires it almost certainly means the fast
            // directory listing was rejected and the scan quietly ran ~30x slower.
            Trace.TraceWarning(
                "LiveStorageSnapshotSource: {0} directories fell back from the fast enumeration " +
                "path to managed enumeration during scan of '{1}'.",
                LastFastPathFallbackCount,
                fullRoot);
        }

        var statistics = new ScanStatistics(
            totals.Files,
            totals.HardLinkDuplicates,
            scanner.Folders.Count - 1,
            deniedPaths.Length + excludedPaths.Length,
            totals.Allocated,
            referenceTotalBytes,
            totals.CloudOnlyFiles,
            totals.CloudOnlyBytes,
            query.Elapsed,
            total.Elapsed,
            scanner.WorkerCount,
            fileSystem,
            ParallelVolumeScanner.HasBackupPrivilege);
        LastStatistics = statistics;

        progress?.Report(new StorageScanProgress(1, "Scan complete", Math.Max(0, covered),
            Math.Max(covered, totalBytes)));

        return new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            request.ScenarioId,
            $"live-{Guid.NewGuid():N}",
            root.StableId,
            DateTimeOffset.UtcNow,
            partial ? SnapshotCompletion.Partial : SnapshotCompletion.Complete,
            new ScanCoverage(
                covered,
                totalBytes,
                deniedPaths,
                totals.Allocated,
                totals.Logical,
                excludedPaths)
            {
                Statistics = statistics,
            },
            nodes);
    }

    /// <summary>
    /// Runs on a walker thread after each folder. Reports throttled progress and, when a partial is
    /// due, freezes the walk (every worker finishes the folder in hand and waits) to build one.
    /// </summary>
    private void OnFolderCompleted(
        ParallelVolumeScanner scanner,
        ScanFolder folder,
        StorageSnapshotRequest request,
        ScanContext context,
        ReaderWriterLockSlim? freezeGate,
        long? referenceTotalBytes)
    {
        context.OnFolderCompleted(scanner.ProcessedBytes, folder.Path);

        if (freezeGate is null ||
            scanner.Root is not { IsMeasured: true } ||
            !context.WantsPartial ||
            !context.TryClaimPartial())
        {
            return;
        }

        // Freeze only for the rollup: it is O(folders) arithmetic, a few milliseconds even on a
        // full volume. Emitting and indexing the partial is far more expensive and runs while the
        // walk continues, reading only what the rollup captured.
        freezeGate.EnterWriteLock();
        try
        {
            ParallelVolumeScanner.Rollup(scanner.Folders);
        }
        finally
        {
            freezeGate.ExitWriteLock();
        }

        try
        {
            context.PublishPartial(
                () => BuildPartial(request, scanner, referenceTotalBytes),
                scanner.ProcessedBytes);
        }
        finally
        {
            context.ReleasePartialClaim();
        }
    }

    /// <summary>
    /// Freezes the walk so far into a usable snapshot. Always <see cref="SnapshotCompletion.Partial"/>
    /// — by construction there is more to come — so nothing downstream can mistake it for a result.
    /// Reads the values of the last rollup only, so it is safe to run while workers keep walking.
    /// </summary>
    private StorageSnapshot BuildPartial(
        StorageSnapshotRequest request,
        ParallelVolumeScanner scanner,
        long? referenceTotalBytes)
    {
        ScanFolder root = scanner.Root!;
        ImmutableArray<StorageNode> nodes = Emit(root, correlateProviders: false, foldersOnly: true);
        long covered = root.Allocated;
        (string[] denied, string[] excluded) = Classify(scanner.DeniedPaths);

        return new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            request.ScenarioId,
            $"live-partial-{root.StableId:N}",
            root.StableId,
            DateTimeOffset.UtcNow,
            SnapshotCompletion.Partial,
            new ScanCoverage(
                covered,
                Math.Max(referenceTotalBytes ?? covered, covered),
                denied,
                covered,
                root.Logical,
                excluded),
            nodes);
    }

    private static (string[] Denied, string[] Excluded) Classify(IEnumerable<string> paths)
    {
        var denied = new List<string>();
        var excluded = new List<string>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            (SystemPathClassifier.IsExpectedSystemExclusion(path) ? excluded : denied).Add(path);
        }

        return ([.. denied], [.. excluded]);
    }

    /// <summary>
    /// Freezes the tree into immutable nodes.
    /// <para>
    /// <paramref name="correlateProviders"/> is false for streamed partials: provider correlation is
    /// per-node string matching whose answer is decoration on a result that is about to be replaced.
    /// </para>
    /// <para>
    /// <paramref name="foldersOnly"/> is likewise true only for partials, and is what keeps live
    /// updates arriving on a large volume. Files are the overwhelming majority of nodes, but
    /// mid-scan the room renders folders: the tree rail, the folder table and the treemap. Folder
    /// sizes are full rollups either way, so the numbers on screen are the same; only the file rows
    /// wait for the finished scan.
    /// </para>
    /// </summary>
    private ImmutableArray<StorageNode> Emit(ScanFolder root, bool correlateProviders, bool foldersOnly)
    {
        // Level by level: every folder on a level builds its own child rows independently (hashing
        // ids, joining paths and correlating providers is the per-node cost), then the kept
        // subfolders form the next level. Paths only collide between siblings — a case-sensitive
        // directory holding "a" and "A" — so de-duplication is per folder.
        var nodes = ImmutableArray.CreateBuilder<StorageNode>();
        nodes.Add(FolderNode(root, null, correlateProviders));

        List<ScanFolder> level = [root];
        while (level.Count > 0)
        {
            var emitted = new (List<StorageNode> Nodes, List<ScanFolder> Expand)[level.Count];
            Parallel.For(
                0,
                level.Count,
                () => (Candidates: new List<Row>(), Seen: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                (i, _, scratch) =>
                {
                    emitted[i] = EmitChildren(level[i], correlateProviders, foldersOnly, scratch.Candidates, scratch.Seen);
                    return scratch;
                },
                static _ => { });

            var next = new List<ScanFolder>();
            foreach ((List<StorageNode> children, List<ScanFolder> expand) in emitted)
            {
                nodes.AddRange(children);
                next.AddRange(expand);
            }

            level = next;
        }

        return nodes.ToImmutable();
    }

    private (List<StorageNode> Nodes, List<ScanFolder> Expand) EmitChildren(
        ScanFolder folder,
        bool correlateProviders,
        bool foldersOnly,
        List<Row> candidates,
        HashSet<string> seenPaths)
    {
        var nodes = new List<StorageNode>();
        var expand = new List<ScanFolder>();
        Guid folderId = folder.StableId;
        CollectCandidates(folder, foldersOnly, candidates);
        seenPaths.Clear();
        TopFileList files = folder.Files;
        int untracked = foldersOnly ? 0 : files.UntrackedCount;

        int keep = candidates.Count + untracked <= _maxChildrenPerFolder
            ? candidates.Count
            : _maxChildrenPerFolder - 1;
        if (keep < candidates.Count || untracked > 0)
        {
            candidates.Sort(static (a, b) => b.Allocated.CompareTo(a.Allocated));
        }

        for (int i = 0; i < keep; i++)
        {
            Row row = candidates[i];
            if (row.Folder is ScanFolder child)
            {
                if (seenPaths.Add(child.Path))
                {
                    nodes.Add(FolderNode(child, folderId, correlateProviders));
                    expand.Add(child);
                }

                continue;
            }

            string path = Path.Combine(folder.Path, row.Leaf.Name);
            if (seenPaths.Add(path))
            {
                nodes.Add(LeafNode(row.Leaf, path, folderId, correlateProviders));
            }
        }

        int rolledRows = candidates.Count - keep;
        if (rolledRows + untracked > 0)
        {
            long allocated = foldersOnly ? 0 : files.UntrackedAllocated;
            long logical = foldersOnly ? 0 : files.UntrackedLogical;
            int itemCount = untracked;
            long newest = foldersOnly ? 0 : files.UntrackedNewestTicks;
            for (int i = keep; i < candidates.Count; i++)
            {
                Row row = candidates[i];
                allocated += row.Allocated;
                logical += row.Logical;
                itemCount += 1 + (row.Folder?.ItemCount ?? 0);
                newest = Math.Max(newest, row.NewestTicks);
            }

            int rolled = rolledRows + untracked;
            string aggregatePath = $"{folder.Path.TrimEnd('\\')}\\\u2026 ({rolled} more items)";
            if (seenPaths.Add(aggregatePath))
            {
                nodes.Add(new StorageNode(
                    StableId(aggregatePath),
                    folderId,
                    $"\u2026 and {rolled:N0} more items",
                    aggregatePath,
                    StorageNodeKind.Folder,
                    allocated,
                    itemCount,
                    ToTime(newest == 0 ? folder.NewestTicks : newest),
                    null,
                    logical > allocated ? logical : null));
            }
        }

        return (nodes, expand);
    }

    private static void CollectCandidates(ScanFolder folder, bool foldersOnly, List<Row> candidates)
    {
        candidates.Clear();

        // A folder that was not finished at the last rollup may still be gaining children.
        if (!folder.RolledMeasured)
        {
            return;
        }

        if (folder.Subfolders is { } subfolders)
        {
            foreach (ScanFolder child in subfolders)
            {
                candidates.Add(new Row(child, default));
            }
        }

        if (folder.ReparseLeaves is { } reparse)
        {
            foreach (ScanLeaf leaf in reparse)
            {
                candidates.Add(new Row(null, leaf));
            }
        }

        if (!foldersOnly && folder.Files.Items is { } items)
        {
            for (int i = 0; i < folder.Files.Count; i++)
            {
                candidates.Add(new Row(null, items[i]));
            }
        }
    }

    private static StorageNode FolderNode(ScanFolder folder, Guid? parentId, bool correlateProviders)
    {
        DateTimeOffset modified = ToTime(folder.NewestTicks);
        return new StorageNode(
            folder.StableId,
            parentId,
            folder.Name,
            folder.Path,
            StorageNodeKind.Folder,
            folder.Allocated,
            folder.ItemCount,
            modified,
            correlateProviders
                ? LiveProviderCorrelator.Correlate(folder.Name, folder.Path, StorageNodeKind.Folder, modified)
                : null,
            folder.Logical > folder.Allocated ? folder.Logical : null)
        {
            IsMeasured = folder.RolledMeasured,
        };
    }

    private static StorageNode LeafNode(ScanLeaf leaf, string path, Guid parentId, bool correlateProviders)
    {
        // AllocationSize normally rounds *up* past the apparent length (cluster slack), so
        // allocated >= apparent is the common, uninteresting case and must stay hidden. Only
        // when the apparent length exceeds the on-disk allocation — sparse VHDX, NTFS
        // compression, ReFS block cloning — is there a real "size vs size on disk" story worth
        // surfacing, so LogicalBytes is populated only then.
        StorageNodeKind kind = leaf.IsDirectory ? StorageNodeKind.Folder : StorageNodeKind.File;
        DateTimeOffset modified = ToTime(leaf.LastWriteTicks);
        return new StorageNode(
            StableId(path),
            parentId,
            string.IsNullOrWhiteSpace(leaf.Name) ? path : leaf.Name,
            path,
            kind,
            leaf.Allocated,
            0,
            modified,
            correlateProviders ? LiveProviderCorrelator.Correlate(leaf.Name, path, kind, modified) : null,
            leaf.Logical > leaf.Allocated ? leaf.Logical : null);
    }

    private static DateTimeOffset ToTime(long ticks) =>
        ticks <= 0 ? DateTimeOffset.UnixEpoch : new DateTimeOffset(ticks, TimeSpan.Zero);

    private static long? TryGetVolumeUsedBytes(string fullRoot)
    {
        try
        {
            string? volumeRoot = Path.GetPathRoot(fullRoot);
            if (volumeRoot is null ||
                !string.Equals(
                    fullRoot.TrimEnd('\\') + "\\",
                    volumeRoot.TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady)
            {
                return null;
            }

            return Math.Max(0, drive.TotalSize - drive.TotalFreeSpace);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryGetFileSystem(string fullRoot)
    {
        try
        {
            string? volumeRoot = Path.GetPathRoot(fullRoot);
            if (volumeRoot is null)
            {
                return null;
            }

            var drive = new DriveInfo(volumeRoot);
            return drive.IsReady ? drive.DriveFormat : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DescribeRootName(string fullRoot)
    {
        string? volumeRoot = Path.GetPathRoot(fullRoot);
        bool isVolumeRoot = volumeRoot is not null &&
            string.Equals(
                fullRoot.TrimEnd('\\') + "\\",
                volumeRoot.TrimEnd('\\') + "\\",
                StringComparison.OrdinalIgnoreCase);

        if (isVolumeRoot)
        {
            try
            {
                var drive = new DriveInfo(volumeRoot!);
                string label = drive.IsReady ? drive.VolumeLabel : string.Empty;
                string letter = volumeRoot!.TrimEnd('\\');
                return string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})";
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return volumeRoot!.TrimEnd('\\');
            }
        }

        string name = new DirectoryInfo(fullRoot).Name;
        return string.IsNullOrWhiteSpace(name) ? fullRoot : name;
    }

    /// <summary>A candidate child row: either a walked folder or a leaf from a folder's lists.</summary>
    private readonly record struct Row(ScanFolder? Folder, ScanLeaf Leaf)
    {
        public long Allocated => Folder?.Allocated ?? Leaf.Allocated;

        public long Logical => Folder?.Logical ?? Leaf.Logical;

        public long NewestTicks => Folder?.NewestTicks ?? Leaf.LastWriteTicks;
    }

    /// <summary>
    /// Throttled, monotonic progress reporting shared by every walker thread. Reports are
    /// serialized by one gate so fractions never go backwards, and a busy gate is skipped rather
    /// than waited on so reporting never stalls the walk.
    /// </summary>
    private sealed class ScanContext(
        IProgress<StorageScanProgress>? progress,
        long? referenceTotalBytes,
        TimeSpan interval)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Lock _gate = new();
        private TimeSpan _lastReport = TimeSpan.FromSeconds(-1);
        private double _lastFraction;
        private int _foldersDone;
        private long _nextPartialTicks;
        private int _partialClaim;
        private string _lastDirectory = string.Empty;

        /// <summary>True when enough time has passed that another partial snapshot is worth building.</summary>
        public bool WantsPartial =>
            progress is not null && _stopwatch.Elapsed.Ticks >= Interlocked.Read(ref _nextPartialTicks);

        public bool TryClaimPartial() => Interlocked.CompareExchange(ref _partialClaim, 1, 0) == 0;

        public void ReleasePartialClaim() => Volatile.Write(ref _partialClaim, 0);

        public void OnFolderCompleted(long processedBytes, string directoryPath)
        {
            Interlocked.Increment(ref _foldersDone);
            if (progress is null || !_gate.TryEnter())
            {
                return;
            }

            try
            {
                _lastDirectory = directoryPath;
                TimeSpan now = _stopwatch.Elapsed;
                if (now - _lastReport < interval)
                {
                    return;
                }

                _lastReport = now;
                progress.Report(new StorageScanProgress(
                    CurrentFraction(processedBytes),
                    $"Scanning {Shorten(directoryPath)}",
                    processedBytes,
                    ReportedTotal(processedBytes)));
            }
            finally
            {
                _gate.Exit();
            }
        }

        /// <summary>
        /// Builds and reports a partial, then pushes the next one out by whichever is longer: the
        /// normal interval, or twice what this one cost. Freezing the tree is O(folders), so on a
        /// very large volume it stops being free — this caps the price of streaming at roughly a
        /// third of the scan no matter how big the tree gets.
        /// </summary>
        public void PublishPartial(Func<StorageSnapshot> build, long processedBytes)
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                TimeSpan startedAt = _stopwatch.Elapsed;
                StorageSnapshot partial = build();
                TimeSpan cost = _stopwatch.Elapsed - startedAt;
                TimeSpan next = _stopwatch.Elapsed + (cost * 2 > interval ? cost * 2 : interval);
                Interlocked.Exchange(ref _nextPartialTicks, next.Ticks);

                progress.Report(new StorageScanProgress(
                    CurrentFraction(processedBytes),
                    _lastDirectory.Length == 0 ? "Scanning" : $"Scanning {Shorten(_lastDirectory)}",
                    processedBytes,
                    ReportedTotal(processedBytes),
                    partial));
            }
        }

        private long ReportedTotal(long processedBytes) =>
            referenceTotalBytes is long reference ? Math.Max(reference, processedBytes) : processedBytes;

        private double CurrentFraction(long processedBytes)
        {
            double fraction = referenceTotalBytes is long total && total > 0
                ? Math.Min(0.99, (double)processedBytes / total)
                : 1 - 1 / (1 + Volatile.Read(ref _foldersDone) / 1_000.0);

            fraction = Math.Clamp(fraction, 0, 0.99);
            fraction = Math.Max(fraction, _lastFraction);
            _lastFraction = fraction;
            return fraction;
        }

        private static string Shorten(string path)
        {
            const int limit = 48;
            return path.Length <= limit ? path : "\u2026" + path[^limit..];
        }
    }
}
