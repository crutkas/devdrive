using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevDriveStorage;

public static class StorageScenarioSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static StorageSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SnapshotDocument document = JsonSerializer.Deserialize<SnapshotDocument>(json, Options)
            ?? throw new JsonException("The scenario document was empty.");

        var nodes = document.Nodes.Select(node =>
            new StorageNode(
                node.Id,
                node.ParentId,
                node.Name,
                node.PhysicalPath,
                node.Kind,
                node.SizeBytes,
                node.ItemCount,
                node.ModifiedAtUtc,
                node.Provider is null
                    ? null
                    : new StorageProviderContext(
                        node.Provider.ProviderName,
                        node.Provider.LogicalIdentity,
                        node.Provider.Evidence,
                        node.Provider.Availability,
                        node.Provider.ObservedAtUtc),
                node.LogicalBytes))
            .ToImmutableArray();

        return new StorageSnapshot(
            document.SchemaVersion,
            document.ScenarioId,
            document.SnapshotId,
            document.RootId,
            document.CapturedAtUtc,
            document.Completion,
            new ScanCoverage(
                document.Coverage.CoveredBytes,
                document.Coverage.TotalBytes,
                document.Coverage.DeniedPaths),
            nodes);
    }

    private sealed record SnapshotDocument(
        int SchemaVersion,
        string ScenarioId,
        string SnapshotId,
        Guid RootId,
        DateTimeOffset CapturedAtUtc,
        SnapshotCompletion Completion,
        CoverageDocument Coverage,
        NodeDocument[] Nodes);

    private sealed record CoverageDocument(
        long CoveredBytes,
        long TotalBytes,
        string[] DeniedPaths);

    private sealed record NodeDocument(
        Guid Id,
        Guid? ParentId,
        string Name,
        string PhysicalPath,
        StorageNodeKind Kind,
        long SizeBytes,
        int ItemCount,
        DateTimeOffset ModifiedAtUtc,
        ProviderDocument? Provider,
        long? LogicalBytes = null);

    private sealed record ProviderDocument(
        string ProviderName,
        string? LogicalIdentity,
        string? Evidence,
        ProviderAvailability Availability,
        DateTimeOffset? ObservedAtUtc);
}
