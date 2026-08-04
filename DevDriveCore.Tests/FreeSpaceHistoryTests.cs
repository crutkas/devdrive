using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Covers the arithmetic the Overview room's chart depends on. Every case here is a rule the room
/// states in words somewhere — "recording since", "at this rate", "down 61 GB this month" — and a
/// chart that contradicts its own caption is worse than no chart.
/// </summary>
[TestClass]
public sealed class FreeSpaceHistoryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FreeSpaceSample Sample(double hoursAfterOrigin, long freeGb, string volume = "G:") => new()
    {
        TakenAtUtc = Origin.AddHours(hoursAfterOrigin),
        VolumeId = volume,
        TotalBytes = 1_000L * 1024 * 1024 * 1024,
        FreeBytes = freeGb * 1024 * 1024 * 1024,
    };

    [TestMethod]
    public void Append_AddsTheFirstReading()
    {
        IReadOnlyList<FreeSpaceSample> result = FreeSpaceHistory.Append([], Sample(0, 400));

        Assert.HasCount(1, result);
    }

    [TestMethod]
    public void Append_DropsAReadingInsideTheMinimumInterval()
    {
        IReadOnlyList<FreeSpaceSample> first = FreeSpaceHistory.Append([], Sample(0, 400));

        IReadOnlyList<FreeSpaceSample> second = FreeSpaceHistory.Append(first, Sample(0.1, 399));

        Assert.HasCount(1, second);
        Assert.AreEqual(400L * 1024 * 1024 * 1024, second[0].FreeBytes);
    }

    [TestMethod]
    public void Append_KeepsAReadingPastTheMinimumInterval()
    {
        IReadOnlyList<FreeSpaceSample> first = FreeSpaceHistory.Append([], Sample(0, 400));

        IReadOnlyList<FreeSpaceSample> second = FreeSpaceHistory.Append(first, Sample(1, 399));

        Assert.HasCount(2, second);
    }

    [TestMethod]
    public void Append_ThrottlesEachVolumeSeparately()
    {
        IReadOnlyList<FreeSpaceSample> history = FreeSpaceHistory.Append([], Sample(0, 400, "G:"));

        history = FreeSpaceHistory.Append(history, Sample(0.1, 100, "C:"));

        Assert.HasCount(2, history);
    }

    [TestMethod]
    public void Append_TrimsPerVolumeNotGlobally()
    {
        List<FreeSpaceSample> seed = [];
        for (int i = 0; i < FreeSpaceHistory.MaxSamplesPerVolume + 20; i++)
        {
            seed.Add(Sample(i, 400, "G:"));
        }

        seed.Add(Sample(0, 100, "C:"));

        IReadOnlyList<FreeSpaceSample> result = FreeSpaceHistory.Append(
            seed, Sample(FreeSpaceHistory.MaxSamplesPerVolume + 21, 399, "G:"));

        Assert.HasCount(FreeSpaceHistory.MaxSamplesPerVolume, result.Where(s => s.VolumeId == "G:").ToList());
        Assert.HasCount(1, result.Where(s => s.VolumeId == "C:").ToList());
    }

    [TestMethod]
    public void Append_DoesNotMutateTheInput()
    {
        List<FreeSpaceSample> original = [Sample(0, 400)];

        FreeSpaceHistory.Append(original, Sample(1, 399));

        Assert.HasCount(1, original);
    }

    [TestMethod]
    public void Describe_ReturnsEmptyForAnUnknownVolume()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400, "G:")], "Z:", Origin.AddHours(1), TimeSpan.FromDays(30));

        Assert.IsFalse(trend.HasTrend);
        Assert.AreEqual(0, trend.SampleCount);
    }

    [TestMethod]
    public void Describe_WithOneReadingHasNoTrend()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400)], "G:", Origin.AddHours(1), TimeSpan.FromDays(30));

        Assert.IsFalse(trend.HasTrend);
        Assert.AreEqual(1, trend.SampleCount);
        Assert.IsNull(trend.ProjectedFreeBytes);
        Assert.IsEmpty(trend.Points);
    }

    [TestMethod]
    public void Describe_IgnoresReadingsOlderThanTheWindow()
    {
        List<FreeSpaceSample> history = [Sample(0, 500), Sample(24 * 40, 400), Sample(24 * 41, 390)];

        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            history, "G:", Origin.AddDays(41), TimeSpan.FromDays(30));

        Assert.AreEqual(2, trend.SampleCount);
        Assert.AreEqual(400L * 1024 * 1024 * 1024, trend.Oldest!.FreeBytes);
    }

    [TestMethod]
    public void Describe_ReportsTheDropAcrossTheWindow()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400), Sample(24, 340)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.AreEqual(-60L * 1024 * 1024 * 1024, trend.DeltaBytes);
        Assert.AreEqual(TimeSpan.FromHours(24), trend.Span);
    }

    [TestMethod]
    public void Describe_ProjectsTheSameSpanForward()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400), Sample(24, 340)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.AreEqual(280L * 1024 * 1024 * 1024, trend.ProjectedFreeBytes);
    }

    [TestMethod]
    public void Describe_NeverProjectsBelowZero()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 100), Sample(24, 10)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.AreEqual(0L, trend.ProjectedFreeBytes);
    }

    [TestMethod]
    public void Describe_NeverProjectsAboveCapacity()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 100), Sample(24, 800)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.AreEqual(1_000L * 1024 * 1024 * 1024, trend.ProjectedFreeBytes);
    }

    [TestMethod]
    public void Describe_PutsMeasuredPointsInTheFirstHalf()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400), Sample(12, 380), Sample(24, 340)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.HasCount(4, trend.Points);
        Assert.AreEqual(0d, trend.Points[0].X, 0.001d);
        Assert.AreEqual(0.25d, trend.Points[1].X, 0.001d);
        Assert.AreEqual(0.5d, trend.Points[2].X, 0.001d);
        Assert.AreEqual(1d, trend.Points[3].X, 0.001d);
    }

    [TestMethod]
    public void Describe_MarksOnlyTheLastPointProjected()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400), Sample(24, 340)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.HasCount(1, trend.Points.Where(p => p.IsProjected).ToList());
        Assert.IsTrue(trend.Points[^1].IsProjected);
    }

    [TestMethod]
    public void Describe_ScalesYToTheObservedRange()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400), Sample(24, 340)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        // Highest reading pins to 1, the projected low pins to 0.
        Assert.AreEqual(1d, trend.Points[0].Y, 0.001d);
        Assert.AreEqual(0d, trend.Points[^1].Y, 0.001d);
    }

    [TestMethod]
    public void Describe_CentresAFlatLine()
    {
        FreeSpaceTrend trend = FreeSpaceHistory.Describe(
            [Sample(0, 400), Sample(24, 400)], "G:", Origin.AddHours(25), TimeSpan.FromDays(30));

        Assert.IsTrue(trend.Points.All(p => Math.Abs(p.Y - 0.5d) < 0.001d));
    }

    [TestMethod]
    public void Store_ReadsBackWhatItWrote()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ddm-history-{Guid.NewGuid():N}.json");
        try
        {
            JsonFreeSpaceHistoryStore store = new(path);
            store.Append(Sample(0, 400));
            store.Append(Sample(1, 390));

            Assert.HasCount(2, new JsonFreeSpaceHistoryStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Store_ReadsAMissingFileAsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ddm-history-{Guid.NewGuid():N}.json");

        Assert.IsEmpty(new JsonFreeSpaceHistoryStore(path).Load());
    }

    [TestMethod]
    public void Store_ReadsACorruptFileAsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ddm-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");

            Assert.IsEmpty(new JsonFreeSpaceHistoryStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
