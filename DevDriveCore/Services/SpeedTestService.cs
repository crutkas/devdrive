using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Default <see cref="ISpeedTestService"/>. Composes an <see cref="IDiskBenchmark"/> (which owns all
/// disk I/O and temp-file cleanup) and the pure <see cref="SpeedTestMath"/>. All decision/aggregation
/// logic lives here so it is unit-testable against a mock benchmark — no disk is touched in tests.
/// </summary>
public sealed class SpeedTestService : ISpeedTestService
{
    private readonly IDiskBenchmark _benchmark;
    private readonly DiskBenchmarkOptions _options;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Creates a service over a benchmark, with optional benchmark tunables and clock.</summary>
    public SpeedTestService(IDiskBenchmark benchmark, DiskBenchmarkOptions? options = null, Func<DateTimeOffset>? now = null)
    {
        _benchmark = benchmark ?? throw new ArgumentNullException(nameof(benchmark));
        _options = options ?? DiskBenchmarkOptions.Default;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Convenience factory wiring the real <see cref="DiskBenchmark"/>.</summary>
    public static SpeedTestService CreateDefault() => new(new DiskBenchmark());

    /// <inheritdoc />
    public async Task<SpeedTestComparison> RunComparisonAsync(string systemDriveRoot, string devDriveRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDriveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(devDriveRoot);

        return await Task.Run(
            () =>
            {
                DiskBenchmarkResult system = _benchmark.Run(systemDriveRoot, _options, cancellationToken);
                DiskBenchmarkResult dev = _benchmark.Run(devDriveRoot, _options, cancellationToken);
                return SpeedTestMath.Build(system, dev, _now());
            },
            cancellationToken).ConfigureAwait(false);
    }
}
