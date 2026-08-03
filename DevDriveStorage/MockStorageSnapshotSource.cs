using System.Collections.Immutable;
using System.Reflection;

namespace DevDriveStorage;

public sealed record MockStorageScenario(
    string Id,
    string DisplayName,
    string Description,
    ImmutableArray<StorageSnapshot> Snapshots,
    int FailuresBeforeSuccess = 0,
    int PhaseDelayMultiplier = 1);

public sealed class MockStorageScenarioCatalog
{
    private readonly ImmutableDictionary<string, MockStorageScenario> _scenarios;

    private MockStorageScenarioCatalog(IEnumerable<MockStorageScenario> scenarios)
    {
        _scenarios = scenarios.ToImmutableDictionary(scenario => scenario.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> ScenarioIds => _scenarios.Keys.Order().ToArray();

    public IReadOnlyList<MockStorageScenario> Scenarios =>
        _scenarios.Values.OrderBy(scenario => scenario.DisplayName).ToArray();

    public MockStorageScenario GetScenario(string id) =>
        _scenarios.TryGetValue(id, out MockStorageScenario? scenario)
            ? scenario
            : throw new StorageSnapshotSourceException($"Unknown mock scenario '{id}'.");

    public static MockStorageScenarioCatalog CreateDefault()
    {
        StorageSnapshot baseline = LoadEmbedded("baseline.json");
        StorageSnapshot partial = LoadEmbedded("partial.json");
        StorageSnapshot empty = LoadEmbedded("empty.json");
        StorageSnapshot awkward = LoadEmbedded("awkward-names.json");
        StorageSnapshot changed = LoadEmbedded("changed-refresh.json");

        StorageSnapshot ForScenario(
            string id,
            string snapshotId,
            Func<StorageNode, StorageNode>? transform = null) =>
            new(
                StorageSnapshot.CurrentSchemaVersion,
                id,
                snapshotId,
                baseline.RootId,
                baseline.CapturedAtUtc,
                baseline.Completion,
                baseline.Coverage,
                baseline.Nodes.Select(transform ?? (node => node)).ToImmutableArray());

        StorageSnapshot noProvider = ForScenario(
            "no-provider", "no-provider-1", node => node with { Provider = null });
        StorageSnapshot staleProvider = ForScenario(
            "stale-provider", "stale-provider-1",
            node => node.Provider is null
                ? node
                : node with
                {
                    Provider = node.Provider with
                    {
                        Availability = ProviderAvailability.Stale,
                        ObservedAtUtc = baseline.CapturedAtUtc.AddDays(-14),
                    },
                });
        StorageSnapshot unavailable = ForScenario(
            "provider-unavailable", "provider-unavailable-1",
            node => node.Provider is null
                ? node
                : node with
                {
                    Provider = node.Provider with
                    {
                        Availability = ProviderAvailability.Unavailable,
                        Evidence = "Mock provider endpoint did not respond.",
                    },
                });
        StorageSnapshot scanning = ForScenario("scanning", "scanning-complete");
        StorageSnapshot failure = ForScenario("failure", "failure-recovered");
        StorageSnapshot changedInitial = ForScenario("changed-refresh", "changed-refresh-before");

        return new MockStorageScenarioCatalog(
        [
            new("baseline", "Baseline · 712 GB", "Approved synthetic drive snapshot.",
                [baseline]),
            new("scanning", "Scanning / cancel", "Scripted progress that can be cancelled.",
                [scanning], PhaseDelayMultiplier: 14),
            new("partial", "Partial coverage", "One mock path is denied.",
                [partial]),
            new("failure", "Failure / retry", "Fails once, then succeeds on retry.",
                [failure], FailuresBeforeSuccess: 1),
            new("empty", "Empty drive", "Successful scan with no child items.",
                [empty]),
            new("no-provider", "No provider metadata", "Physical items only.",
                [noProvider]),
            new("stale-provider", "Stale metadata", "Provider evidence is two weeks old.",
                [staleProvider]),
            new("provider-unavailable", "Provider unavailable", "Physical data remains usable.",
                [unavailable]),
            new("awkward-names", "Awkward names", "Deep, long, duplicate, and Unicode names.",
                [awkward]),
            new("large", "Large generated set", "2,500 deterministic files.",
                [CreateLargeSnapshot()]),
            new("changed-refresh", "Changed refresh", "Selection is removed on refresh.",
                [changedInitial, changed]),
        ]);
    }

    private static StorageSnapshot LoadEmbedded(string fileName)
    {
        string resourceName = $"DevDriveStorage.Scenarios.{fileName}";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new StorageSnapshotSourceException($"Missing embedded scenario '{fileName}'.");
        using var reader = new StreamReader(stream);
        return StorageScenarioSerializer.Deserialize(reader.ReadToEnd());
    }

    private static StorageSnapshot CreateLargeSnapshot()
    {
        const long fileUnit = 1_000_000;
        var nodes = ImmutableArray.CreateBuilder<StorageNode>(2541);
        Guid rootId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        nodes.Add(new StorageNode(
            rootId, null, "Large mock drive (M:)", @"M:\", StorageNodeKind.Folder,
            3_123_750_000, 2540, DateTimeOffset.Parse("2026-08-02T12:00:00Z"), null));

        for (int folderIndex = 0; folderIndex < 40; folderIndex++)
        {
            Guid folderId = DeterministicGuid(10_000 + folderIndex);
            string folderPath = $@"M:\generated-{folderIndex:D2}";
            int filesInFolder = folderIndex < 20 ? 63 : 62;
            long folderBytes = 0;
            for (int fileIndex = 0; fileIndex < filesInFolder; fileIndex++)
            {
                long bytes = ((folderIndex * 67L + fileIndex * 31L) % 2500 + 1) * fileUnit;
                folderBytes += bytes;
                nodes.Add(new StorageNode(
                    DeterministicGuid(folderIndex * 100 + fileIndex),
                    folderId,
                    $"artifact-{fileIndex:D4}.bin",
                    $@"{folderPath}\artifact-{fileIndex:D4}.bin",
                    StorageNodeKind.File,
                    bytes,
                    0,
                    DateTimeOffset.Parse("2026-08-02T12:00:00Z"),
                    null));
            }

            nodes.Insert(folderIndex + 1, new StorageNode(
                folderId,
                rootId,
                $"generated-{folderIndex:D2}",
                folderPath,
                StorageNodeKind.Folder,
                folderBytes,
                filesInFolder,
                DateTimeOffset.Parse("2026-08-02T12:00:00Z"),
                null));
        }

        long total = nodes.Where(node => node.Kind == StorageNodeKind.File).Sum(node => node.SizeBytes);
        nodes[0] = nodes[0] with { SizeBytes = total };
        return new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            "large",
            "large-1",
            rootId,
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"),
            SnapshotCompletion.Complete,
            new ScanCoverage(total, total, []),
            nodes.ToImmutable());
    }

    private static Guid DeterministicGuid(int value) =>
        Guid.Parse($"90000000-0000-0000-0001-{value:D12}");
}

public sealed class MockStorageSnapshotSource(
    MockStorageScenarioCatalog catalog,
    TimeSpan phaseDelay) : IStorageSnapshotSource
{
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private string? _lastScenarioId;

    public async Task<StorageSnapshot> GetSnapshotAsync(
        StorageSnapshotRequest request,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        MockStorageScenario scenario = catalog.GetScenario(request.ScenarioId);
        if (_lastScenarioId != request.ScenarioId)
        {
            _attempts[request.ScenarioId] = 0;
            _lastScenarioId = request.ScenarioId;
        }

        int attempt = _attempts.GetValueOrDefault(request.ScenarioId);
        _attempts[request.ScenarioId] = attempt + 1;

        StorageScanProgress[] phases =
        [
            new(0.08, "Preparing mock index", 0, 100),
            new(0.32, "Reading mock folder records", 32, 100),
            new(0.68, "Aggregating synthetic sizes", 68, 100),
            new(0.92, "Building mock projections", 92, 100),
        ];

        TimeSpan delay = phaseDelay > TimeSpan.Zero
            ? phaseDelay * Math.Max(1, scenario.PhaseDelayMultiplier)
            : TimeSpan.Zero;

        foreach (StorageScanProgress phase in phases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(phase);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (attempt < scenario.FailuresBeforeSuccess)
        {
            throw new StorageSnapshotSourceException(
                "The scripted mock scan failed while reading a synthetic index.");
        }

        int snapshotIndex = Math.Min(request.RefreshOrdinal, scenario.Snapshots.Length - 1);
        progress?.Report(new StorageScanProgress(1, "Mock scan complete", 100, 100));
        return scenario.Snapshots[snapshotIndex];
    }
}
