using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevDriveStorage;

public static partial class StorageScenarioSerializer
{
    public static StorageSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SnapshotDocument document = JsonSerializer.Deserialize(json, ScenarioJsonContext.Default.SnapshotDocument)
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

    // Source-generated, so reading a scenario needs no reflection and stays safe under trimming and
    // NativeAOT. Reproduces the options this reader has always used: camelCase member names matched
    // case-sensitively, unknown members rejected, and enums as camelCase strings (integers still
    // accepted). The enum converters are per type because an attribute cannot hand a naming policy to
    // JsonStringEnumConverter<TEnum>'s constructor.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        [
            typeof(CamelCaseEnumConverter<SnapshotCompletion>),
            typeof(CamelCaseEnumConverter<StorageNodeKind>),
            typeof(CamelCaseEnumConverter<ProviderAvailability>),
        ])]
    [JsonSerializable(typeof(SnapshotDocument))]
    private sealed partial class ScenarioJsonContext : JsonSerializerContext
    {
    }

    private sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
        where TEnum : struct, Enum
    {
        public CamelCaseEnumConverter()
            : base(JsonNamingPolicy.CamelCase)
        {
        }
    }
}
