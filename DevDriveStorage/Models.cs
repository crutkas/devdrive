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

public sealed record StorageNode
{
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
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A node ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
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

        LogicalBytes = logicalBytes;
        Id = id;
        ParentId = parentId;
        Name = name;
        PhysicalPath = physicalPath;
        Kind = kind;
        SizeBytes = sizeBytes;
        ItemCount = itemCount;
        ModifiedAtUtc = modifiedAtUtc;
        Provider = provider;
    }

    public Guid Id { get; init; }

    public Guid? ParentId { get; init; }

    public string Name { get; init; }

    public string PhysicalPath { get; init; }

    public StorageNodeKind Kind { get; init; }

    public long SizeBytes { get; init; }

    public int ItemCount { get; init; }

    public DateTimeOffset ModifiedAtUtc { get; init; }

    public StorageProviderContext? Provider { get; init; }

    /// <summary>
    /// Apparent size on disk when it differs from the bytes actually allocated —
    /// sparse virtual disks and copy-on-write clones are the common cases.
    /// </summary>
    public long? LogicalBytes { get; init; }

    public string SizeDisplay => ByteSizeFormatter.Format(SizeBytes);

    public string LogicalDisplay =>
        LogicalBytes is long logical ? ByteSizeFormatter.Format(logical) : "—";

    public bool HasLogicalDifference =>
        LogicalBytes is long logical && logical != SizeBytes;

    public string KindDisplay => Kind == StorageNodeKind.Folder ? "Folder" : "File";

    public string ItemCountDisplay => Kind == StorageNodeKind.Folder ? $"{ItemCount:N0}" : "—";

    public string ProviderDisplay => Provider?.ProviderName ?? "No provider metadata";

    /// <summary>
    /// The short context label shown in the table: provider name when one is
    /// correlated, otherwise a plain physical descriptor.
    /// </summary>
    public string ContextDisplay => Provider?.ProviderName
        ?? (Kind == StorageNodeKind.Folder ? "Physical folder" : "Physical file");

    public string ModifiedDisplay => ModifiedAtUtc.ToString("MMM d · HH:mm");
}

public sealed record ScanCoverage
{
    public ScanCoverage(
        long coveredBytes,
        long totalBytes,
        IEnumerable<string> deniedPaths,
        long? totalAllocatedBytes = null,
        long? totalApparentBytes = null)
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
}

public sealed class StorageSnapshot
{
    public const int CurrentSchemaVersion = 1;

    private readonly ImmutableDictionary<Guid, StorageNode> _nodesById;

    public StorageSnapshot(
        int schemaVersion,
        string scenarioId,
        string snapshotId,
        Guid rootId,
        DateTimeOffset capturedAtUtc,
        SnapshotCompletion completion,
        ScanCoverage coverage,
        ImmutableArray<StorageNode> nodes)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new StorageSnapshotValidationException(
                $"Unsupported schema version {schemaVersion}; expected {CurrentSchemaVersion}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        if (rootId == Guid.Empty)
        {
            throw new StorageSnapshotValidationException("A root ID is required.");
        }

        if (nodes.IsDefaultOrEmpty)
        {
            throw new StorageSnapshotValidationException("A snapshot must contain a root node.");
        }

        var builder = ImmutableDictionary.CreateBuilder<Guid, StorageNode>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StorageNode node in nodes)
        {
            if (!builder.TryAdd(node.Id, node))
            {
                throw new StorageSnapshotValidationException($"Duplicate node ID: {node.Id}.");
            }

            if (!paths.Add(node.PhysicalPath))
            {
                throw new StorageSnapshotValidationException(
                    $"Duplicate physical path: {node.PhysicalPath}.");
            }
        }

        if (!builder.TryGetValue(rootId, out StorageNode? root) ||
            root.ParentId is not null ||
            root.Kind != StorageNodeKind.Folder)
        {
            throw new StorageSnapshotValidationException(
                "The root must be a folder with no parent.");
        }

        foreach (StorageNode node in nodes.Where(node => node.Id != rootId))
        {
            if (node.ParentId is null ||
                !builder.TryGetValue(node.ParentId.Value, out StorageNode? parent) ||
                parent.Kind != StorageNodeKind.Folder)
            {
                throw new StorageSnapshotValidationException(
                    $"Node '{node.PhysicalPath}' must have an existing folder parent.");
            }
        }

        SchemaVersion = schemaVersion;
        ScenarioId = scenarioId;
        SnapshotId = snapshotId;
        RootId = rootId;
        CapturedAtUtc = capturedAtUtc;
        Completion = completion;
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        Nodes = nodes;
        _nodesById = builder.ToImmutable();
    }

    public int SchemaVersion { get; }

    public string ScenarioId { get; }

    public string SnapshotId { get; }

    public Guid RootId { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public SnapshotCompletion Completion { get; }

    public ScanCoverage Coverage { get; }

    public ImmutableArray<StorageNode> Nodes { get; }

    public StorageNode Root => _nodesById[RootId];

    public StorageNode? Find(Guid id) => _nodesById.GetValueOrDefault(id);

    public IReadOnlyList<StorageNode> ChildrenOf(Guid parentId) =>
        Nodes.Where(node => node.ParentId == parentId)
            .OrderByDescending(node => node.Kind == StorageNodeKind.Folder)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<StorageNode> DescendantsOf(Guid parentId) =>
        Nodes.Where(node => IsDescendantOf(node, parentId)).ToArray();

    public IReadOnlyList<StorageNode> AncestorsAndSelf(Guid id)
    {
        var result = new List<StorageNode>();
        StorageNode? current = Find(id);
        while (current is not null)
        {
            result.Add(current);
            current = current.ParentId is Guid parentId ? Find(parentId) : null;
        }

        result.Reverse();
        return result;
    }

    private bool IsDescendantOf(StorageNode node, Guid ancestorId)
    {
        Guid? parentId = node.ParentId;
        while (parentId is Guid id)
        {
            if (id == ancestorId)
            {
                return true;
            }

            parentId = Find(id)?.ParentId;
        }

        return false;
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
