using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class SpeedTestServiceTests
{
    [TestMethod]
    public async Task RunComparisonAsync_BenchmarksBothDrivesAndBuildsComparison()
    {
        var benchmark = Substitute.For<IDiskBenchmark>();
        benchmark.Run("C:\\", Arg.Any<DiskBenchmarkOptions>(), Arg.Any<CancellationToken>())
            .Returns(new DiskBenchmarkResult { SequentialWriteMBps = 100, SequentialReadMBps = 200, RandomRead4KIops = 1000, RandomWrite4KIops = 500 });
        benchmark.Run("G:\\", Arg.Any<DiskBenchmarkOptions>(), Arg.Any<CancellationToken>())
            .Returns(new DiskBenchmarkResult { SequentialWriteMBps = 300, SequentialReadMBps = 400, RandomRead4KIops = 4000, RandomWrite4KIops = 2000 });

        DateTimeOffset fixedNow = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var service = new SpeedTestService(benchmark, DiskBenchmarkOptions.Default, () => fixedNow);

        SpeedTestComparison cmp = await service.RunComparisonAsync("C:\\", "G:\\");

        benchmark.Received(1).Run("C:\\", Arg.Any<DiskBenchmarkOptions>(), Arg.Any<CancellationToken>());
        benchmark.Received(1).Run("G:\\", Arg.Any<DiskBenchmarkOptions>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(fixedNow, cmp.CompletedAt);

        SpeedMetric seqWrite = cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.SequentialWrite);
        Assert.AreEqual(100, seqWrite.SystemValue);
        Assert.AreEqual(300, seqWrite.DevValue);
        Assert.AreEqual(3.0, seqWrite.Ratio, 1e-9);
        Assert.IsGreaterThan(1.0, cmp.HeadlineMultiplier, "Dev Drive is faster on every metric, so the headline must exceed 1x.");
    }

    [TestMethod]
    public async Task RunComparisonAsync_PropagatesBenchmarkFailure()
    {
        var benchmark = Substitute.For<IDiskBenchmark>();
        benchmark.Run(Arg.Any<string>(), Arg.Any<DiskBenchmarkOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Not enough free space"));
        var service = new SpeedTestService(benchmark);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.RunComparisonAsync("C:\\", "G:\\"));
    }

    [TestMethod]
    public async Task RunComparisonAsync_RejectsBlankRoots()
    {
        var service = new SpeedTestService(Substitute.For<IDiskBenchmark>());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunComparisonAsync(" ", "G:\\"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunComparisonAsync("C:\\", " "));
    }
}
