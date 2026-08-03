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
        long totalBytes)
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
    }

    public double Fraction { get; }

    public string Label { get; }

    public long ProcessedBytes { get; }

    public long TotalBytes { get; }
}

public interface IStorageSnapshotSource
{
    Task<StorageSnapshot> GetSnapshotAsync(
        StorageSnapshotRequest request,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class StorageSnapshotSourceException(string message) : Exception(message);
