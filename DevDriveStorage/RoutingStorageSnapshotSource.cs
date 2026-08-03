namespace DevDriveStorage;

/// <summary>
/// Dispatches a snapshot request to the live scanner or the mock catalogue based on a
/// prefix on <see cref="StorageSnapshotRequest.ScenarioId"/>. This lets a single view
/// model serve both the deterministic mock scenarios and real disk scans without
/// changing its public interaction model: ids that start with <see cref="LivePrefix"/>
/// route to the live source (the remainder is the root path to scan), everything else
/// routes to the mock source.
/// </summary>
public sealed class RoutingStorageSnapshotSource : IStorageSnapshotSource
{
    /// <summary>Scenario id prefix that selects the live scanner, e.g. <c>live:C:\</c>.</summary>
    public const string LivePrefix = "live:";

    private readonly IStorageSnapshotSource _mock;
    private readonly IStorageSnapshotSource _live;

    public RoutingStorageSnapshotSource(IStorageSnapshotSource mock, IStorageSnapshotSource live)
    {
        _mock = mock ?? throw new ArgumentNullException(nameof(mock));
        _live = live ?? throw new ArgumentNullException(nameof(live));
    }

    /// <summary>Builds a live scenario id for the given root path.</summary>
    public static string LiveScenarioId(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return LivePrefix + rootPath;
    }

    /// <summary>True when the id targets the live scanner rather than a mock scenario.</summary>
    public static bool IsLiveScenario(string scenarioId) =>
        scenarioId is not null && scenarioId.StartsWith(LivePrefix, StringComparison.Ordinal);

    public Task<StorageSnapshot> GetSnapshotAsync(
        StorageSnapshotRequest request,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsLiveScenario(request.ScenarioId))
        {
            string rootPath = request.ScenarioId[LivePrefix.Length..];
            return _live.GetSnapshotAsync(
                request with { ScenarioId = rootPath }, progress, cancellationToken);
        }

        return _mock.GetSnapshotAsync(request, progress, cancellationToken);
    }
}
