using System.Collections.Immutable;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

internal static class StorageTestBuilder
{
    public static readonly Guid RootId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid FolderId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid FileId = Guid.Parse("10000000-0000-0000-0000-000000000003");

    public static StorageSnapshot Snapshot(
        string id = "test",
        IEnumerable<StorageNode>? nodes = null,
        ScanCoverage? coverage = null)
    {
        nodes ??=
        [
            Node(RootId, null, "Mock drive (M:)", @"M:\", StorageNodeKind.Folder, 1000, 2),
            Node(FolderId, RootId, "src", @"M:\src", StorageNodeKind.Folder, 700, 1),
            Node(FileId, FolderId, "app.bin", @"M:\src\app.bin", StorageNodeKind.File, 700),
        ];

        return new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            id,
            $"snapshot-{id}",
            RootId,
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"),
            SnapshotCompletion.Complete,
            coverage ?? new ScanCoverage(1000, 1000, []),
            nodes.ToImmutableArray());
    }

    public static StorageNode Node(
        Guid id,
        Guid? parentId,
        string name,
        string path,
        StorageNodeKind kind,
        long bytes,
        int itemCount = 0,
        StorageProviderContext? provider = null) =>
        new(id, parentId, name, path, kind, bytes, itemCount,
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"), provider);
}
