namespace DevDriveCore.Models;

/// <summary>
/// Raw throughput / IOPS measured by a single <see cref="DevDriveCore.Abstractions.IDiskBenchmark"/>
/// run against one drive. Every value is a best-effort <em>synthetic</em> measurement produced from
/// the speed test's own bounded, always-deleted temporary file — never from the user's real data.
/// </summary>
public sealed record DiskBenchmarkResult
{
    /// <summary>The drive root the benchmark ran against (e.g. <c>G:\</c>).</summary>
    public string DriveRoot { get; init; } = string.Empty;

    /// <summary>Sequential write throughput in MB/s (decimal MB = 1,000,000 bytes).</summary>
    public double SequentialWriteMBps { get; init; }

    /// <summary>Sequential read throughput in MB/s.</summary>
    public double SequentialReadMBps { get; init; }

    /// <summary>Random 4 KiB read throughput in IO operations per second.</summary>
    public double RandomRead4KIops { get; init; }

    /// <summary>Random 4 KiB write throughput in IO operations per second.</summary>
    public double RandomWrite4KIops { get; init; }
}
