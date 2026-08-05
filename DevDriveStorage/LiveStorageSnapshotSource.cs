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
/// to walk. It uses an explicit stack (real trees are deep enough to blow the managed
/// stack), reports allocated-on-disk bytes rather than apparent length, never follows
/// reparse points, and turns denied paths into reduced <see cref="ScanCoverage"/> rather
/// than silently omitting them.
/// </remarks>
public sealed class LiveStorageSnapshotSource : IStorageSnapshotSource
{
    /// <summary>Default cap on the number of direct children emitted per folder.</summary>
    public const int DefaultMaxChildrenPerFolder = 1024;

    private readonly int _maxChildrenPerFolder;
    private readonly TimeSpan _progressInterval;

    /// <summary>
    /// Number of directories on the most recent scan whose fast per-directory enumeration failed
    /// yet were still readable through the managed fallback. This isolates the ReFS 64-bit-FileId
    /// failure mode (a silent drop to the ~30x-slower path) from ordinary access denials, which
    /// surface separately in <see cref="ScanCoverage.DeniedPaths"/>. Expected to be 0; a non-zero
    /// value is a performance warning, not a correctness one.
    /// </summary>
    public int LastFastPathFallbackCount { get; private set; }

    /// <summary>
    /// Forces every directory down the managed enumeration path, as if the native fast path had
    /// been rejected. This exists because that fallback is the <em>ReFS</em> path — the one a Dev
    /// Drive actually takes — and it cannot otherwise be reached from a test running on NTFS. A
    /// bug that only shows up on the volume this tool is built for is the worst kind to ship, and
    /// this seam is the only way a test can see it.
    /// </summary>
    public bool ForceManagedEnumeration { get; init; }

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

        // The whole scan is synchronous, blocking I/O; keep it off the calling (UI) thread.
        return Task.Run(() => Scan(request, rootPath, progress, cancellationToken), cancellationToken);
    }

    private StorageSnapshot Scan(
        StorageSnapshotRequest request,
        string rootPath,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
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

        long? referenceTotalBytes = TryGetVolumeUsedBytes(fullRoot);
        var context = new ScanContext(progress, referenceTotalBytes, _progressInterval, ForceManagedEnumeration);

        var root = new ScanEntry(null, fullRoot, DescribeRootName(fullRoot), StorageNodeKind.Folder);
        var order = new List<ScanEntry> { root };
        var stack = new Stack<ScanEntry>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanEntry directory = stack.Pop();
            EnumerateDirectory(directory, order, stack, context, cancellationToken);

            // Between directories, not inside one: the tree is consistent here, and a partial is
            // only worth building once a whole directory's worth of new rows exists.
            if (context.WantsPartial)
            {
                context.PublishPartial(() => BuildPartial(request, root, order, context, referenceTotalBytes));
            }
        }

        Aggregate(order);
        ImmutableArray<StorageNode> nodes = Emit(root, out Guid rootId);

        long covered = root.Allocated;
        long total = Math.Max(referenceTotalBytes ?? covered, covered);
        var deniedPaths = context.DeniedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedPaths = context.ExcludedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Only *unexpected* denials degrade the result. OS-owned exclusions are reported separately so
        // "Complete — 1 system folder excluded" stays truthful and the Partial signal keeps its meaning.
        bool partial = deniedPaths.Length > 0 ||
            (referenceTotalBytes is long reference && covered < reference - reference / 1000);

        LastFastPathFallbackCount = context.FastPathFallbacks;
        if (LastFastPathFallbackCount > 0)
        {
            // Observable rather than silent: if this fires on a Dev Drive it almost certainly means
            // the fast directory-info path was rejected and the scan quietly ran ~30x slower.
            Trace.TraceWarning(
                "LiveStorageSnapshotSource: {0} directories fell back from the fast enumeration " +
                "path to managed enumeration during scan of '{1}'.",
                LastFastPathFallbackCount,
                fullRoot);
        }

        progress?.Report(new StorageScanProgress(1, "Scan complete", Math.Max(0, context.ProcessedBytes),
            Math.Max(covered, total)));

        return new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            request.ScenarioId,
            $"live-{Guid.NewGuid():N}",
            rootId,
            DateTimeOffset.UtcNow,
            partial ? SnapshotCompletion.Partial : SnapshotCompletion.Complete,
            new ScanCoverage(
                covered,
                total,
                deniedPaths,
                context.TotalAllocatedBytes,
                context.TotalApparentBytes,
                excludedPaths),
            nodes);
    }

    /// <summary>
    /// Freezes the walk so far into a usable snapshot. Always <see cref="SnapshotCompletion.Partial"/>
    /// — by construction there is more to come — so nothing downstream can mistake it for a result.
    /// </summary>
    private StorageSnapshot BuildPartial(
        StorageSnapshotRequest request,
        ScanEntry root,
        List<ScanEntry> order,
        ScanContext context,
        long? referenceTotalBytes)
    {
        Aggregate(order);
        ImmutableArray<StorageNode> nodes = Emit(
            root, correlateProviders: false, foldersOnly: true, out Guid rootId);
        long covered = root.Allocated;

        return new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            request.ScenarioId,
            $"live-partial-{rootId:N}",
            rootId,
            DateTimeOffset.UtcNow,
            SnapshotCompletion.Partial,
            new ScanCoverage(
                covered,
                Math.Max(referenceTotalBytes ?? covered, covered),
                context.DeniedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                context.TotalAllocatedBytes,
                context.TotalApparentBytes,
                context.ExcludedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            nodes);
    }

    private static void EnumerateDirectory(
        ScanEntry directory,
        List<ScanEntry> order,
        Stack<ScanEntry> stack,
        ScanContext context,
        CancellationToken cancellationToken)
    {
        // Fast path: one syscall for the whole directory yields each child's on-disk
        // AllocationSize (cluster slack included) alongside its apparent length, replacing
        // both the managed enumeration and a per-file GetCompressedFileSizeW call.
        if (!context.ForceManagedEnumeration &&
            NativeDirectoryEnumerator.TryEnumerate(directory.Path, out List<NativeDirEntry> nativeEntries))
        {
            EnumerateFromNative(directory, nativeEntries, order, stack, context, cancellationToken);
            return;
        }

        // The directory could not be opened for a fast scan (denied, gone, or an unexpected
        // native failure). Fall back to managed enumeration, which records denials the same way
        // and still reports allocation via GetCompressedFileSizeW.
        EnumerateManaged(directory, order, stack, context, cancellationToken, viaFallback: true);
    }

    private static void EnumerateFromNative(
        ScanEntry directory,
        List<NativeDirEntry> entries,
        List<ScanEntry> order,
        Stack<ScanEntry> stack,
        ScanContext context,
        CancellationToken cancellationToken)
    {
        int counter = 0;
        directory.IsMeasured = true;
        foreach (NativeDirEntry entry in entries)
        {
            if (++counter % 256 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            bool isReparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            string fullPath = Path.Combine(directory.Path, entry.Name);

            if (isDirectory && !isReparse)
            {
                var child = new ScanEntry(directory, fullPath, entry.Name, StorageNodeKind.Folder);
                directory.Children.Add(child);
                order.Add(child);
                stack.Push(child);
                continue;
            }

            long allocated = entry.AllocationSize;
            long apparent = entry.EndOfFile;
            var leaf = new ScanEntry(
                directory,
                fullPath,
                entry.Name,
                isDirectory ? StorageNodeKind.Folder : StorageNodeKind.File)
            {
                SelfAllocated = allocated,
                SelfLogical = apparent,
                IsLeaf = true,
                IsMeasured = true,
                IsReparsePoint = isReparse,
                SelfModifiedAtUtc = new DateTimeOffset(entry.LastWriteTimeUtcTicks, TimeSpan.Zero),
            };

            directory.Children.Add(leaf);
            order.Add(leaf);
            context.OnFileProcessed(allocated, apparent, directory.Path);
        }
    }

    private static void EnumerateManaged(
        ScanEntry directory,
        List<ScanEntry> order,
        Stack<ScanEntry> stack,
        ScanContext context,
        CancellationToken cancellationToken,
        bool viaFallback)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory.Path).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            context.RecordDenied(directory.Path);
            return;
        }

        // We opened and listed the directory through the managed path, so if we only got here
        // because the fast path failed, this is a genuine (readable) fast-path fallback — the
        // observable ReFS-class-rejection signal, kept distinct from the denial case above.
        if (viaFallback)
        {
            context.RecordFastPathFallback();
        }

        int counter = 0;
        directory.IsMeasured = true;
        foreach (FileSystemInfo info in entries)
        {
            if (++counter % 256 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            bool isReparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory && !isReparse)
            {
                var child = new ScanEntry(directory, info.FullName, info.Name, StorageNodeKind.Folder);
                directory.Children.Add(child);
                order.Add(child);
                stack.Push(child);
                continue;
            }

            long apparent = SafeLength(info);
            long allocated = NativeFileSize.GetAllocatedBytesOrApparent(info.FullName, apparent);
            var leaf = new ScanEntry(
                directory,
                info.FullName,
                info.Name,
                isDirectory ? StorageNodeKind.Folder : StorageNodeKind.File)
            {
                SelfAllocated = allocated,
                SelfLogical = apparent,
                IsLeaf = true,
                IsMeasured = true,
                IsReparsePoint = isReparse,
                SelfModifiedAtUtc = SafeModified(info),
            };

            directory.Children.Add(leaf);
            order.Add(leaf);
            context.OnFileProcessed(allocated, apparent, directory.Path);
        }
    }

    /// <summary>
    /// Rolls child totals up into their ancestors. Safe to run repeatedly on the same tree — every
    /// entry is reset to its own contribution first — which is what lets a streaming scan publish
    /// an aggregated partial and then keep walking.
    /// </summary>
    private static void Aggregate(List<ScanEntry> order)
    {
        foreach (ScanEntry entry in order)
        {
            entry.Allocated = entry.SelfAllocated;
            entry.Logical = entry.SelfLogical;
            entry.ItemCount = 0;
            entry.ModifiedAtUtc = entry.SelfModifiedAtUtc;
        }

        for (int i = order.Count - 1; i >= 1; i--)
        {
            ScanEntry entry = order[i];
            ScanEntry parent = entry.Parent!;
            parent.Allocated += entry.Allocated;
            parent.Logical += entry.Logical;
            parent.ItemCount += 1 + entry.ItemCount;
            if (entry.ModifiedAtUtc > parent.ModifiedAtUtc)
            {
                parent.ModifiedAtUtc = entry.ModifiedAtUtc;
            }
        }
    }

    private ImmutableArray<StorageNode> Emit(ScanEntry root, out Guid rootId) =>
        Emit(root, correlateProviders: true, foldersOnly: false, out rootId);

    /// <summary>
    /// Freezes the tree into immutable nodes.
    /// <para>
    /// <paramref name="correlateProviders"/> is false for streamed partials: provider correlation is
    /// per-node string matching whose answer is decoration on a result that is about to be replaced.
    /// </para>
    /// <para>
    /// <paramref name="foldersOnly"/> is likewise true only for partials, and is what keeps live
    /// updates arriving on a large volume. Files are the overwhelming majority of nodes — around
    /// 95% of a source tree — but mid-scan the room renders folders: the tree rail, the folder
    /// table and the treemap. Emitting every file on every tick makes each emission cost grow with
    /// the whole walk, so the updates space themselves further and further apart exactly when the
    /// scan is long enough to need them. Folder sizes are full rollups either way, so the numbers
    /// on screen are the same; only the file rows wait for the finished scan.
    /// </para>
    /// </summary>
    private ImmutableArray<StorageNode> Emit(
        ScanEntry root,
        bool correlateProviders,
        bool foldersOnly,
        out Guid rootId)
    {
        var nodes = ImmutableArray.CreateBuilder<StorageNode>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        rootId = root.Id;

        seenPaths.Add(root.Path);
        nodes.Add(ToNode(root, rootId, null, correlateProviders));

        var stack = new Stack<(ScanEntry Entry, Guid Id)>();
        stack.Push((root, rootId));

        while (stack.Count > 0)
        {
            (ScanEntry directory, Guid directoryId) = stack.Pop();
            IReadOnlyList<ScanEntry> candidates = foldersOnly
                ? directory.Children.Where(child => child.Kind == StorageNodeKind.Folder).ToList()
                : directory.Children;
            IReadOnlyList<ScanEntry> emitted = SelectChildren(
                directory, candidates, out ScanEntry? aggregate);

            foreach (ScanEntry child in emitted)
            {
                if (!seenPaths.Add(child.Path))
                {
                    continue;
                }

                Guid childId = child.Id;
                nodes.Add(ToNode(child, childId, directoryId, correlateProviders));
                if (child is { IsLeaf: false, Kind: StorageNodeKind.Folder })
                {
                    stack.Push((child, childId));
                }
            }

            if (aggregate is not null && seenPaths.Add(aggregate.Path))
            {
                nodes.Add(ToNode(aggregate, aggregate.Id, directoryId, correlateProviders));
            }
        }

        return nodes.ToImmutable();
    }

    /// <summary>
    /// Derives a node's identity from its path instead of minting a fresh one. A streaming scan
    /// emits the same folder many times as it grows, and a refresh emits it again later; random
    /// ids would make each emission a brand-new set of nodes, so the room would lose the user's
    /// scope, selection and expanded folders on every update. Paths are unique within a snapshot
    /// (the emitter dedupes them), so hashing one is enough to keep ids unique.
    /// </summary>
    private static Guid StableId(string path)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(path.ToLowerInvariant().AsSpan()), digest);
        return new Guid(digest[..16]);
    }

    private IReadOnlyList<ScanEntry> SelectChildren(
        ScanEntry directory,
        IReadOnlyList<ScanEntry> candidates,
        out ScanEntry? aggregate)
    {
        aggregate = null;
        if (candidates.Count <= _maxChildrenPerFolder)
        {
            return candidates;
        }

        List<ScanEntry> ranked = candidates
            .OrderByDescending(child => child.Allocated)
            .ToList();

        var kept = ranked.Take(_maxChildrenPerFolder - 1).ToList();
        List<ScanEntry> rolled = ranked.Skip(_maxChildrenPerFolder - 1).ToList();

        long allocated = rolled.Sum(child => child.Allocated);
        long logical = rolled.Sum(child => child.Logical);
        int itemCount = rolled.Sum(child => 1 + child.ItemCount);
        DateTimeOffset modified = rolled.Count == 0
            ? directory.ModifiedAtUtc
            : rolled.Max(child => child.ModifiedAtUtc);

        string aggregatePath = $"{directory.Path.TrimEnd('\\')}\\\u2026 ({rolled.Count} more items)";
        aggregate = new ScanEntry(directory, aggregatePath, $"\u2026 and {rolled.Count:N0} more items",
            StorageNodeKind.Folder)
        {
            Allocated = allocated,
            Logical = logical,
            ItemCount = itemCount,
            IsLeaf = true,
            ModifiedAtUtc = modified,
        };

        return kept;
    }

    private static StorageNode ToNode(ScanEntry entry, Guid id, Guid? parentId, bool correlateProviders)
    {
        // AllocationSize normally rounds *up* past the apparent length (cluster slack), so
        // allocated >= apparent is the common, uninteresting case and must stay hidden. Only
        // when the apparent length exceeds the on-disk allocation — sparse VHDX, NTFS
        // compression, ReFS block cloning — is there a real "size vs size on disk" story worth
        // surfacing, so LogicalBytes is populated only then.
        long? logical = entry.Logical > entry.Allocated ? entry.Logical : null;
        StorageProviderContext? provider = correlateProviders
            ? LiveProviderCorrelator.Correlate(entry.Name, entry.Path, entry.Kind, entry.ModifiedAtUtc)
            : null;

        int itemCount = entry.Kind == StorageNodeKind.Folder ? entry.ItemCount : 0;
        return new StorageNode(
            id,
            parentId,
            entry.Name,
            entry.Path,
            entry.Kind,
            entry.Allocated,
            itemCount,
            entry.ModifiedAtUtc,
            provider,
            logical)
        {
            IsMeasured = entry.IsMeasured,
        };
    }

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

    private static long SafeLength(FileSystemInfo info)
    {
        try
        {
            return info is FileInfo file ? Math.Max(0, file.Length) : 0;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return 0;
        }
    }

    private static DateTimeOffset SafeModified(FileSystemInfo info)
    {
        try
        {
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is UnauthorizedAccessException
            or DirectoryNotFoundException
            or FileNotFoundException
            or PathTooLongException
            or IOException;

    /// <summary>Mutable node accumulated during the walk, converted to an immutable
    /// <see cref="StorageNode"/> on emit.</summary>
    private sealed class ScanEntry(ScanEntry? parent, string path, string name, StorageNodeKind kind)
    {
        public ScanEntry? Parent { get; } = parent;

        public string Path { get; } = path;

        public string Name { get; } = string.IsNullOrWhiteSpace(name) ? path : name;

        public StorageNodeKind Kind { get; } = kind;

        public List<ScanEntry> Children { get; } = [];

        /// <summary>
        /// This entry's own bytes, set once at discovery: a file's allocation size, zero for a
        /// folder. Kept separate from <see cref="Allocated"/> so a rollup can be recomputed from
        /// scratch as often as we like — a streaming scan aggregates the same tree repeatedly, and
        /// rolling up in place would add each file's bytes to its ancestors again every pass.
        /// </summary>
        public long SelfAllocated { get; set; }

        /// <summary>This entry's own apparent length. See <see cref="SelfAllocated"/>.</summary>
        public long SelfLogical { get; set; }

        /// <summary>This entry's own timestamp, before folders take the newest of their contents.</summary>
        public DateTimeOffset SelfModifiedAtUtc { get; set; } = DateTimeOffset.UnixEpoch;

        /// <summary>Rolled-up total. Recomputed by <c>Aggregate</c>; do not set during the walk.</summary>
        public long Allocated { get; set; }

        /// <summary>Rolled-up total. Recomputed by <c>Aggregate</c>; do not set during the walk.</summary>
        public long Logical { get; set; }

        public int ItemCount { get; set; }

        public bool IsLeaf { get; set; }

        /// <summary>
        /// Whether this folder has been looked inside. A directory is discovered by its parent's
        /// enumeration and only walked later, so between those two moments a partial snapshot knows
        /// it exists and nothing else. Emitting that as zero bytes reads as "measured, and empty",
        /// which is a different and false claim &mdash; the UI shows an em dash instead.
        /// Leaves are measured at discovery, so this is only ever false for a pending folder.
        /// </summary>
        public bool IsMeasured { get; set; }

        public bool IsReparsePoint { get; set; }

        public DateTimeOffset ModifiedAtUtc { get; set; } = DateTimeOffset.UnixEpoch;

        /// <summary>
        /// Hashed once and kept. A streaming scan re-emits the same entry on every partial, and
        /// re-hashing a million paths each time is most of what makes an emission expensive.
        /// </summary>
        public Guid Id => _id ??= StableId(Path);

        private Guid? _id;
    }

    /// <summary>Carries throttled, monotonic progress reporting state through the walk.</summary>
    private sealed class ScanContext(
        IProgress<StorageScanProgress>? progress,
        long? referenceTotalBytes,
        TimeSpan interval,
        bool forceManagedEnumeration = false)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastReport = TimeSpan.FromSeconds(-1);
        private double _lastFraction;
        private int _fileCount;
        private TimeSpan _nextPartial = TimeSpan.Zero;
        private string _lastDirectory = string.Empty;

        /// <summary>Test seam; see <see cref="LiveStorageSnapshotSource.ForceManagedEnumeration"/>.</summary>
        public bool ForceManagedEnumeration { get; } = forceManagedEnumeration;

        public List<string> DeniedPaths { get; } = [];

        /// <summary>OS-owned folders skipped by design; never a reason to downgrade to Partial.</summary>
        public List<string> ExcludedPaths { get; } = [];

        public long ProcessedBytes { get; private set; }

        /// <summary>Running sum of every file's on-disk AllocationSize (cluster slack included).</summary>
        public long TotalAllocatedBytes { get; private set; }

        /// <summary>Running sum of every file's apparent (logical) length.</summary>
        public long TotalApparentBytes { get; private set; }

        /// <summary>Directories that fell back from the fast path to readable managed enumeration.</summary>
        public int FastPathFallbacks { get; private set; }

        public void RecordDenied(string path)
        {
            if (SystemPathClassifier.IsExpectedSystemExclusion(path))
            {
                ExcludedPaths.Add(path);
                return;
            }

            DeniedPaths.Add(path);
        }

        public void RecordFastPathFallback() => FastPathFallbacks++;

        /// <summary>True when enough time has passed that another partial snapshot is worth building.</summary>
        public bool WantsPartial => progress is not null && _stopwatch.Elapsed >= _nextPartial;

        /// <summary>
        /// Builds and reports a partial, then pushes the next one out by whichever is longer: the
        /// normal interval, or twice what this one cost. Freezing the tree is O(folders), so on a
        /// very large volume it stops being free — this caps the price of streaming at roughly a
        /// third of the scan no matter how big the tree gets, without a tuned node threshold that
        /// would be wrong on the next machine.
        /// </summary>
        public void PublishPartial(Func<StorageSnapshot> build)
        {
            if (progress is null)
            {
                return;
            }

            TimeSpan startedAt = _stopwatch.Elapsed;
            StorageSnapshot partial = build();
            TimeSpan cost = _stopwatch.Elapsed - startedAt;
            _nextPartial = _stopwatch.Elapsed + (cost * 2 > interval ? cost * 2 : interval);

            long reportedTotal = referenceTotalBytes is long reference
                ? Math.Max(reference, ProcessedBytes)
                : ProcessedBytes;

            progress.Report(new StorageScanProgress(
                CurrentFraction(),
                _lastDirectory.Length == 0 ? "Scanning" : $"Scanning {Shorten(_lastDirectory)}",
                ProcessedBytes,
                reportedTotal,
                partial));
        }

        private double CurrentFraction()
        {
            double fraction = referenceTotalBytes is long total && total > 0
                ? Math.Min(0.99, (double)ProcessedBytes / total)
                : 1 - 1 / (1 + _fileCount / 10_000.0);

            fraction = Math.Clamp(fraction, 0, 0.99);
            fraction = Math.Max(fraction, _lastFraction);
            _lastFraction = fraction;
            return fraction;
        }

        public void OnFileProcessed(long allocated, long apparent, string directoryPath)
        {
            ProcessedBytes += allocated;
            TotalAllocatedBytes += allocated;
            TotalApparentBytes += apparent;
            _fileCount++;
            _lastDirectory = directoryPath;
            MaybeReport(directoryPath);
        }

        private void MaybeReport(string directoryPath)
        {
            if (progress is null)
            {
                return;
            }

            TimeSpan now = _stopwatch.Elapsed;
            if (now - _lastReport < interval)
            {
                return;
            }

            _lastReport = now;

            long reportedTotal = referenceTotalBytes is long reference
                ? Math.Max(reference, ProcessedBytes)
                : ProcessedBytes;

            progress.Report(new StorageScanProgress(
                CurrentFraction(),
                $"Scanning {Shorten(directoryPath)}",
                ProcessedBytes,
                reportedTotal));
        }

        private static string Shorten(string path)
        {
            const int limit = 48;
            return path.Length <= limit ? path : "\u2026" + path[^limit..];
        }
    }
}
