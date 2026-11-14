using System.Collections.Immutable;

namespace DevDriveStorage;

public enum StorageNodeKind
{
    Folder,
    File,
}

public enum ProviderAvailability
{
    Available,
    Stale,
    Unavailable,
}

public enum SnapshotCompletion
{
    Complete,
    Partial,
}

public sealed record StorageProviderContext(
    string ProviderName,
    string? LogicalIdentity,
    string? Evidence,
    ProviderAvailability Availability,
    DateTimeOffset? ObservedAtUtc)
{
    public string AvailabilityDisplay => Availability switch
    {
        ProviderAvailability.Available => "Available",
        ProviderAvailability.Stale => "Metadata may be stale",
        _ => "Provider unavailable",
    };
}

/// <summary>
/// One file or folder. A <see cref="StorageSnapshot"/> does not hold these: it keeps its nodes in a
/// table and makes a node when a caller asks for one, so two reads of the same node are equal but
/// need not be the same instance.
/// </summary>
public sealed record StorageNode
{
    // A node's full path is usually its folder's path plus its name. A node made by InFolder keeps a
    // reference to its folder's path string, one instance shared by all its siblings, and joins on
    // read rather than carrying a copy.
    private readonly string _path;
    private readonly bool _pathIsFolder;
    private readonly long _modifiedUtcTicks;

    // Apparent size and provider context are set on a few percent of nodes; one optional reference
    // costs every other node 8 bytes instead of 24.
    private readonly RareMetadata? _rare;

    public StorageNode(
        Guid id,
        Guid? parentId,
        string name,
        string physicalPath,
        StorageNodeKind kind,
        long sizeBytes,
        int itemCount,
        DateTimeOffset modifiedAtUtc,
        StorageProviderContext? provider,
        long? logicalBytes = null)
        : this(id, parentId, name, physicalPath, pathIsFolder: false, kind, sizeBytes, itemCount,
            modifiedAtUtc.UtcTicks, provider, logicalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
    }

    private StorageNode(
        Guid id,
        Guid? parentId,
        string name,
        string path,
        bool pathIsFolder,
        StorageNodeKind kind,
        long sizeBytes,
        int itemCount,
        long modifiedUtcTicks,
        StorageProviderContext? provider,
        long? logicalBytes)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A node ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        if (kind == StorageNodeKind.File && itemCount != 0)
        {
            throw new ArgumentException("Files cannot contain child items.", nameof(itemCount));
        }

        if (logicalBytes is long logical)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(logical, nameof(logicalBytes));
        }

        Id = id;
        ParentId = parentId;
        Name = name;
        _path = path;
        _pathIsFolder = pathIsFolder;
        Kind = kind;
        SizeBytes = sizeBytes;
        ItemCount = itemCount;
        _modifiedUtcTicks = modifiedUtcTicks;
        _rare = RareMetadata.Create(logicalBytes, provider);
    }

    /// <summary>
    /// A node whose <see cref="PhysicalPath"/> is <paramref name="folderPath"/> joined with
    /// <paramref name="name"/>, built on read. Pass the folder's own path instance so every child
    /// shares it rather than carrying a copy.
    /// </summary>
    internal static StorageNode InFolder(
        Guid id,
        Guid parentId,
        string name,
        string folderPath,
        StorageNodeKind kind,
        long sizeBytes,
        DateTimeOffset modifiedAtUtc,
        StorageProviderContext? provider,
        long? logicalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return new StorageNode(id, parentId, name, folderPath, pathIsFolder: true, kind, sizeBytes, 0,
            modifiedAtUtc.UtcTicks, provider, logicalBytes);
    }

    /// <summary>
    /// One node of a snapshot, made when a caller asks for it. <paramref name="pathIsFolder"/> says
    /// <paramref name="path"/> is the parent's path, shared by every sibling made with it.
    /// </summary>
    internal static StorageNode View(
        Guid id,
        Guid? parentId,
        string name,
        string path,
        bool pathIsFolder,
        StorageNodeKind kind,
        long sizeBytes,
        int itemCount,
        long modifiedUtcTicks,
        StorageProviderContext? provider,
        long? logicalBytes,
        bool isMeasured) =>
        new(id, parentId, name, path, pathIsFolder, kind, sizeBytes, itemCount, modifiedUtcTicks, provider, logicalBytes)
        {
            IsMeasured = isMeasured,
        };

    public Guid Id { get; init; }

    public Guid? ParentId { get; init; }

    public string Name { get; init; }

    public string PhysicalPath
    {
        get => _pathIsFolder ? Path.Combine(_path, Name) : _path;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _path = value;
            _pathIsFolder = false;
        }
    }

    public StorageNodeKind Kind { get; init; }

    public long SizeBytes { get; init; }

    public int ItemCount { get; init; }

    public DateTimeOffset ModifiedAtUtc
    {
        get => new(_modifiedUtcTicks, TimeSpan.Zero);
        init => _modifiedUtcTicks = value.UtcTicks;
    }

    public StorageProviderContext? Provider
    {
        get => _rare?.Provider;
        init => _rare = RareMetadata.Create(LogicalBytes, value);
    }

    /// <summary>
    /// Apparent size on disk when it differs from the bytes actually allocated —
    /// sparse virtual disks and copy-on-write clones are the common cases.
    /// </summary>
    public long? LogicalBytes
    {
        get => _rare?.LogicalBytes;
        init => _rare = RareMetadata.Create(value, Provider);
    }

    /// <summary>
    /// Whether this node's bytes have actually been counted. A streaming scan discovers a folder
    /// from its parent's listing and walks it later, and in that window the honest answer to "how
    /// big is it" is <em>we have not looked yet</em> — not zero, which claims we looked and found
    /// nothing. Also false for a folder we were denied, where the answer is unknowable rather than
    /// merely pending. Defaults to <see langword="true"/> so a completed scan, a mock scenario and a
    /// deserialised snapshot all keep meaning exactly what they say.
    /// </summary>
    public bool IsMeasured { get; init; } = true;

    /// <summary>
    /// The scanner's roll-up of a folder's smallest items (<see cref="RollupPath"/>): one node standing for many items, not a
    /// folder on disk.
    /// </summary>
    public bool IsRollup => Kind == StorageNodeKind.Folder && !_pathIsFolder && IsRollupPath(_path);

    private const string RollupMarker = "\\\u2026 (";
    private const string RollupSuffix = " more items)";

    /// <summary>The path of the node that rolls up <paramref name="items"/> of <paramref name="folderPath"/>'s smallest items.</summary>
    public static string RollupPath(string folderPath, int items) =>
        $"{folderPath.TrimEnd('\\')}{RollupMarker}{items}{RollupSuffix}";

    internal static bool IsRollupPath(string path) =>
        path.EndsWith(RollupSuffix, StringComparison.Ordinal) &&
        path.Contains(RollupMarker, StringComparison.Ordinal);

    public string SizeDisplay => IsMeasured ? ByteSizeFormatter.Format(SizeBytes) : "—";

    public string LogicalDisplay => IsMeasured && LogicalBytes is long logical
        ? ByteSizeFormatter.Format(logical)
        : "—";

    public bool HasLogicalDifference =>
        IsMeasured && LogicalBytes is long logical && logical != SizeBytes;

    public string KindDisplay => Kind == StorageNodeKind.Folder ? "Folder" : "File";

    public string ItemCountDisplay => Kind == StorageNodeKind.Folder && IsMeasured
        ? $"{ItemCount:N0}"
        : "—";

    public string ProviderDisplay => Provider?.ProviderName ?? "No provider metadata";

    /// <summary>
    /// The short context label shown in the table: provider name when one is
    /// correlated, otherwise a plain physical descriptor.
    /// </summary>
    public string ContextDisplay => Provider?.ProviderName
        ?? (Kind == StorageNodeKind.Folder ? "Physical folder" : "Physical file");

    public string ModifiedDisplay => ModifiedAtUtc.ToString("MMM d · HH:mm");

    /// <summary>
    /// Equal when every fact the node states is equal. A path held whole and one held as the
    /// parent's path plus the name are equal when they spell the same path, so a node a snapshot
    /// hands out equals the node it was built from.
    /// </summary>
    public bool Equals(StorageNode? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
            (Id == other.Id &&
            ParentId == other.ParentId &&
            Kind == other.Kind &&
            SizeBytes == other.SizeBytes &&
            ItemCount == other.ItemCount &&
            _modifiedUtcTicks == other._modifiedUtcTicks &&
            IsMeasured == other.IsMeasured &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            EqualityComparer<RareMetadata?>.Default.Equals(_rare, other._rare) &&
            SamePath(other)));

    public override int GetHashCode() =>
        HashCode.Combine(Id, ParentId, Name, Kind, SizeBytes, ItemCount, _modifiedUtcTicks, IsMeasured);

    private bool SamePath(StorageNode other) =>
        (_pathIsFolder == other._pathIsFolder && string.Equals(_path, other._path, StringComparison.Ordinal)) ||
        string.Equals(PhysicalPath, other.PhysicalPath, StringComparison.Ordinal);

    private sealed record RareMetadata(long? LogicalBytes, StorageProviderContext? Provider)
    {
        public static RareMetadata? Create(long? logicalBytes, StorageProviderContext? provider) =>
            logicalBytes is null && provider is null ? null : new(logicalBytes, provider);
    }
}

public sealed record ScanCoverage
{
    public ScanCoverage(
        long coveredBytes,
        long totalBytes,
        IEnumerable<string> deniedPaths,
        long? totalAllocatedBytes = null,
        long? totalApparentBytes = null,
        IEnumerable<string>? excludedPaths = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(coveredBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        if (coveredBytes > totalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coveredBytes), "Covered bytes cannot exceed total bytes.");
        }

        if (totalAllocatedBytes is long allocated)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(allocated, nameof(totalAllocatedBytes));
        }

        if (totalApparentBytes is long apparent)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(apparent, nameof(totalApparentBytes));
        }

        CoveredBytes = coveredBytes;
        TotalBytes = totalBytes;
        TotalAllocatedBytes = totalAllocatedBytes;
        TotalApparentBytes = totalApparentBytes;
        DeniedPaths = deniedPaths?.ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(deniedPaths));
        if (DeniedPaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Denied paths cannot be blank.", nameof(deniedPaths));
        }

        ExcludedPaths = excludedPaths?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
        if (ExcludedPaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Excluded paths cannot be blank.", nameof(excludedPaths));
        }
    }

    public long CoveredBytes { get; }

    public long TotalBytes { get; }

    /// <summary>
    /// Sum of every scanned file's on-disk <c>AllocationSize</c> (cluster slack included), or
    /// <see langword="null"/> when the source did not compute it (the mock path leaves it null).
    /// Together with <see cref="TotalApparentBytes"/> this recovers the per-file "size vs size on
    /// disk" delta that is otherwise folded into <see cref="StorageNode.SizeBytes"/> and lost —
    /// letting a caller state a single "N bytes in cluster slack" fact with zero per-row noise.
    /// </summary>
    public long? TotalAllocatedBytes { get; }

    /// <summary>
    /// Sum of every scanned file's apparent (logical) length, or <see langword="null"/> when the
    /// source did not compute it. Compare against <see cref="TotalAllocatedBytes"/> via
    /// <see cref="AllocatedMinusApparentBytes"/>.
    /// </summary>
    public long? TotalApparentBytes { get; }

    public ImmutableArray<string> DeniedPaths { get; }

    /// <summary>
    /// OS-owned folders that were skipped because they are unreadable by design (System Volume
    /// Information and friends). Kept apart from <see cref="DeniedPaths"/> so a scan that hit only
    /// these still reports <see cref="SnapshotCompletion.Complete"/> — otherwise every whole-volume
    /// scan is permanently Partial and the signal means nothing.
    /// </summary>
    public ImmutableArray<string> ExcludedPaths { get; }

    /// <summary>
    /// Signed on-disk-minus-apparent aggregate. Positive means cluster slack dominates (real disk
    /// usage exceeds apparent — the common dev-tree, millions-of-tiny-files case, exaggerated on
    /// ReFS/NTFS cluster boundaries); negative means sparse/compressed/cloned savings dominate.
    /// <see langword="null"/> when either aggregate is unavailable.
    /// </summary>
    public long? AllocatedMinusApparentBytes =>
        TotalAllocatedBytes is long allocated && TotalApparentBytes is long apparent
            ? allocated - apparent
            : null;

    public double Ratio => TotalBytes == 0 ? 1 : (double)CoveredBytes / TotalBytes;

    public string Display => $"{Ratio:P0} coverage";

    /// <summary>
    /// Whole-scan counts from the live scanner, or <see langword="null"/> for sources that do not
    /// measure them (the mock path, and partial snapshots mid-walk).
    /// </summary>
    public ScanStatistics? Statistics { get; init; }
}

/// <summary>
/// The headline numbers of one live scan. These describe the whole walk, independent of how many
/// rows the snapshot emits per folder.
/// </summary>
/// <param name="FileCount">Distinct files counted. A file with several hard links counts once.</param>
/// <param name="HardLinkDuplicateCount">Extra hard links to a file already counted (NTFS only).</param>
/// <param name="FolderCount">Folders found beneath the scan root.</param>
/// <param name="UnreadableFolderCount">Folders that could not be listed, or only partly.</param>
/// <param name="SizeOnDiskBytes">Sum of every distinct file's allocation on disk.</param>
/// <param name="VolumeUsedBytes">The volume's own used-space figure, when the root is a volume.</param>
/// <param name="CloudOnlyFileCount">Cloud-only placeholders (recall on data access).</param>
/// <param name="CloudOnlyBytes">Logical size of cloud-only placeholders. They allocate nothing locally.</param>
/// <param name="QueryDuration">Wall time of the walk itself.</param>
/// <param name="TotalDuration">Wall time including building the snapshot.</param>
/// <param name="WorkerCount">Threads that walked the tree.</param>
/// <param name="FileSystem">The volume's file system name (NTFS, ReFS, …), when known.</param>
/// <param name="UsedBackupPrivilege">Whether SeBackupPrivilege was enabled for the walk.</param>
public sealed record ScanStatistics(
    long FileCount,
    long HardLinkDuplicateCount,
    long FolderCount,
    long UnreadableFolderCount,
    long SizeOnDiskBytes,
    long? VolumeUsedBytes,
    long CloudOnlyFileCount,
    long CloudOnlyBytes,
    TimeSpan QueryDuration,
    TimeSpan TotalDuration,
    int WorkerCount,
    string? FileSystem,
    bool UsedBackupPrivilege)
{
    /// <summary>Multi-line summary for status surfaces.</summary>
    public string Display
    {
        get
        {
            string used = VolumeUsedBytes is long volumeUsed
                ? $" · drive used {ByteSizeFormatter.Format(volumeUsed)}"
                : string.Empty;
            string cloud = CloudOnlyFileCount > 0
                ? $" · {CloudOnlyFileCount:N0} cloud-only ({ByteSizeFormatter.Format(CloudOnlyBytes)})"
                : string.Empty;
            return $"{FileCount:N0} files · {HardLinkDuplicateCount:N0} hard-link duplicates · "
                + $"{FolderCount:N0} folders · {UnreadableFolderCount:N0} unreadable\n"
                + $"{ByteSizeFormatter.Format(SizeOnDiskBytes)} on disk{used}{cloud}\n"
                + $"Query {QueryDuration.TotalSeconds:0.00} s · total {TotalDuration.TotalSeconds:0.00} s · "
                + $"{WorkerCount} threads{(FileSystem is null ? string.Empty : $" · {FileSystem}")}";
        }
    }
}

/// <summary>
/// One scan's tree of nodes. The nodes live in a table of a few large arrays rather than an object
/// each (see <see cref="NodeTable"/>), so a snapshot of millions of nodes is cheap to keep alive and
/// cheap for a full garbage collection to step over. A <see cref="StorageNode"/> is made when a
/// caller asks for one: a node read twice is equal both times, and a later scan of the same path
/// gives it the same id.
/// </summary>
public sealed class StorageSnapshot
{
    public const int CurrentSchemaVersion = 1;

    private readonly NodeTable _table;
    private readonly StorageNode _root;
    private ParentPath? _lastParent;
    private IVolumeIndex? _index;

    /// <summary>
    /// What the walk read, when the source kept it (<see cref="LiveStorageSnapshotSource.KeepIndex"/>), for one taker: the
    /// first call returns it and the snapshot lets go of it, so the walk's folder tree is freed once the taker is done.
    /// </summary>
    public IVolumeIndex? TakeIndex() => Interlocked.Exchange(ref _index, null);

    internal void AttachIndex(IVolumeIndex index) => _index = index;

    public StorageSnapshot(
        int schemaVersion,
        string scenarioId,
        string snapshotId,
        Guid rootId,
        DateTimeOffset capturedAtUtc,
        SnapshotCompletion completion,
        ScanCoverage coverage,
        ImmutableArray<StorageNode> nodes)
        : this(
            schemaVersion,
            scenarioId,
            snapshotId,
            capturedAtUtc,
            completion,
            coverage,
            TableOf(schemaVersion, scenarioId, snapshotId, rootId, nodes))
    {
    }

    /// <summary>A snapshot over a table its source has filled and sealed.</summary>
    internal StorageSnapshot(
        int schemaVersion,
        string scenarioId,
        string snapshotId,
        DateTimeOffset capturedAtUtc,
        SnapshotCompletion completion,
        ScanCoverage coverage,
        NodeTable table)
    {
        ValidateHeader(schemaVersion, scenarioId, snapshotId);
        if (table.RootIndex < 0)
        {
            throw new ArgumentException("The node table has not been sealed.", nameof(table));
        }

        SchemaVersion = schemaVersion;
        ScenarioId = scenarioId;
        SnapshotId = snapshotId;
        CapturedAtUtc = capturedAtUtc;
        Completion = completion;
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        _table = table;
        RootId = table[table.RootIndex].Id;
        _root = CreateView(table.RootIndex, null);
        Nodes = new StorageNodeList(this, null);
    }

    public int SchemaVersion { get; }

    public string ScenarioId { get; }

    public string SnapshotId { get; }

    public Guid RootId { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public SnapshotCompletion Completion { get; }

    public ScanCoverage Coverage { get; }

    /// <summary>
    /// Every node, in the order the snapshot was built. Each is made as it is read; keep the ones
    /// you need rather than materialising the whole list.
    /// </summary>
    public StorageNodeList Nodes { get; }

    /// <summary>The root, which unlike other nodes is the same instance on every read.</summary>
    public StorageNode Root => _root;

    internal NodeTable Table => _table;

    public StorageNode? Find(Guid id)
    {
        int index = _table.IndexOf(id);
        return index < 0 ? null : NodeAt(index);
    }

    /// <summary>The direct children of <paramref name="parentId"/>: folders first, then by name.</summary>
    public IReadOnlyList<StorageNode> ChildrenOf(Guid parentId)
    {
        int parent = _table.IndexOf(parentId);
        if (parent < 0)
        {
            return [];
        }

        int first = _table.FirstChild(parent);
        int end = _table.ChildEnd(parent);
        if (first == end)
        {
            return [];
        }

        string parentPath = ParentPathOf(parent);
        var children = new StorageNode[end - first];
        for (int position = first; position < end; position++)
        {
            children[position - first] = CreateView(_table.ChildAt(position), parentPath);
        }

        return children;
    }

    /// <summary>
    /// Walks the subtree under <paramref name="parentId"/>. Descending from the parent costs the
    /// size of the subtree; testing every node's ancestry instead costs the whole snapshot times
    /// its depth, which on a real volume never finishes. The nodes are made as the list is read.
    /// </summary>
    public IReadOnlyList<StorageNode> DescendantsOf(Guid parentId)
    {
        int parent = _table.IndexOf(parentId);
        if (parent < 0)
        {
            return [];
        }

        // A folder's item count is the size of its subtree as the scan counted it, so the list is
        // usually allocated once rather than grown by copying.
        var result = new List<int>(Math.Min(_table.Count, _table[parent].ItemCount));
        var pending = new Stack<int>();
        pending.Push(parent);
        while (pending.Count > 0)
        {
            int folder = pending.Pop();
            for (int position = _table.FirstChild(folder), end = _table.ChildEnd(folder); position < end; position++)
            {
                int child = _table.ChildAt(position);
                result.Add(child);
                if (_table[child].IsFolder)
                {
                    pending.Push(child);
                }
            }
        }

        return new StorageNodeList(this, result);
    }

    public IReadOnlyList<StorageNode> AncestorsAndSelf(Guid id)
    {
        int index = _table.IndexOf(id);
        if (index < 0)
        {
            return [];
        }

        var chain = new List<int>();
        for (int current = index; current >= 0 && chain.Count <= _table.Count; current = _table[current].Parent)
        {
            chain.Add(current);
        }

        var result = new StorageNode[chain.Count];
        string? parentPath = null;
        for (int slot = 0; slot < result.Length; slot++)
        {
            int node = chain[chain.Count - 1 - slot];
            result[slot] = node == _table.RootIndex ? _root : CreateView(node, parentPath);
            if (slot < result.Length - 1)
            {
                parentPath = parentPath is null ? _table.PathOf(node) : _table.ChildPath(node, parentPath);
            }
        }

        return result;
    }

    internal StorageNode NodeAt(int index) => index == _table.RootIndex ? _root : CreateView(index, null);

    private static NodeTable TableOf(
        int schemaVersion,
        string scenarioId,
        string snapshotId,
        Guid rootId,
        ImmutableArray<StorageNode> nodes)
    {
        ValidateHeader(schemaVersion, scenarioId, snapshotId);
        if (rootId == Guid.Empty)
        {
            throw new StorageSnapshotValidationException("A root ID is required.");
        }

        if (nodes.IsDefaultOrEmpty)
        {
            throw new StorageSnapshotValidationException("A snapshot must contain a root node.");
        }

        return NodeTable.FromNodes(nodes, rootId);
    }

    private static void ValidateHeader(int schemaVersion, string scenarioId, string snapshotId)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new StorageSnapshotValidationException(
                $"Unsupported schema version {schemaVersion}; expected {CurrentSchemaVersion}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
    }

    /// <param name="parentPath">The parent's path, when the caller has it; siblings made with one instance share it.</param>
    private StorageNode CreateView(int index, string? parentPath)
    {
        ref readonly NodeRecord record = ref _table[index];
        string? ownPath = _table.OwnPathOf(index);
        return StorageNode.View(
            record.Id,
            record.Parent >= 0 ? _table[record.Parent].Id : null,
            _table.NameOf(index),
            ownPath ?? parentPath ?? ParentPathOf(record.Parent),
            pathIsFolder: ownPath is null,
            record.IsFolder ? StorageNodeKind.Folder : StorageNodeKind.File,
            record.SizeBytes,
            record.ItemCount,
            record.ModifiedTicks,
            _table.ProviderOf(index),
            _table.LogicalOf(index),
            record.Has(NodeFlags.Measured));
    }

    /// <summary>
    /// A parent's path, remembering the last one: nodes are usually read a sibling run at a time, so
    /// the run shares one path instead of each rebuilding it.
    /// </summary>
    private string ParentPathOf(int parent)
    {
        ParentPath? last = Volatile.Read(ref _lastParent);
        if (last is not null && last.Index == parent)
        {
            return last.Path;
        }

        string path = _table.PathOf(parent);
        Volatile.Write(ref _lastParent, new ParentPath(parent, path));
        return path;
    }

    private sealed class ParentPath(int index, string path)
    {
        public int Index { get; } = index;

        public string Path { get; } = path;
    }
}

public sealed class StorageSnapshotValidationException(string message) : Exception(message);

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        string format = value >= 100 || unit == 0 ? "N0" : value >= 10 ? "N1" : "N2";
        return $"{value.ToString(format)} {Units[unit]}";
    }
}
