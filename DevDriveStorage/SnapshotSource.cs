namespace DevDriveStorage;

public sealed record StorageSnapshotRequest(
    string ScenarioId,
    Guid? ScopeId = null,
    int RefreshOrdinal = 0);

public sealed record StorageScanProgress
{
    public StorageScanProgress(
        double fraction,
        string label,
        long processedBytes,
        long totalBytes,
        StorageSnapshot? partial = null)
    {
        if (fraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfNegative(processedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        Fraction = fraction;
        Label = label;
        ProcessedBytes = processedBytes;
        TotalBytes = totalBytes;
        Partial = partial;
    }

    public double Fraction { get; }

    public string Label { get; }

    public long ProcessedBytes { get; }

    public long TotalBytes { get; }

    /// <summary>
    /// Everything walked so far, as a usable snapshot, when the source can produce one cheaply
    /// enough to be worth showing. A cold full-volume walk is minutes long, so a report that says
    /// only "63%" leaves the room blank for the entire scan; a partial lets the tree, table and
    /// treemap fill in as the results arrive. Null means the source does not stream — consumers
    /// must keep working with progress alone.
    /// </summary>
    public StorageSnapshot? Partial { get; }
}

public interface IStorageSnapshotSource
{
    Task<StorageSnapshot> GetSnapshotAsync(
        StorageSnapshotRequest request,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class StorageSnapshotSourceException(string message) : Exception(message);
