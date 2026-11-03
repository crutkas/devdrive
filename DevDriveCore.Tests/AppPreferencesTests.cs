using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// The preference values themselves, and the two Core services that read them.
/// </summary>
/// <remarks>
/// The point of these tests is that every preference reaches a decision. A preference the app stores
/// but never consults is the same lie as a toggle wired to nothing, and it is invisible in the UI —
/// the control moves, the value persists, and the behaviour does not change.
/// </remarks>
[TestClass]
public sealed class AppPreferencesTests
{
    private static VolumeInfo Volume(char letter, ulong size, ulong free, bool devDrive = false) => new()
    {
        DriveLetter = letter,
        Label = $"{letter} drive",
        FileSystemType = devDrive ? "ReFS" : "NTFS",
        SizeBytes = size,
        FreeBytes = free,
        IsDevDrive = devDrive,
    };

    private const ulong OneTb = 1_000_000_000_000UL;

    [TestMethod]
    public void Defaults_match_the_values_the_code_shipped_with()
    {
        AppPreferences p = AppPreferences.Default;

        Assert.AreEqual(15, p.LowFreePercent);
        Assert.AreEqual(AttentionSignalBuilder.LowFreeFraction, p.LowFreeFraction);
        Assert.AreEqual(AttentionSignalBuilder.DefaultCacheRollupThreshold, p.CacheRollupThreshold);
        Assert.IsTrue(p.WatchCachesOffDevDrive);
        Assert.IsTrue(p.RecordFreeSpaceHistory);
        Assert.AreEqual(90, p.HistoryRetentionDays);
        Assert.IsTrue(p.PreferResizeOverVhdx);
    }

    [TestMethod]
    public void Normalize_clamps_a_low_free_threshold_of_zero_up_to_the_floor()
    {
        // A stored 0 would switch the Overview room's first job off without saying so.
        AppPreferences p = new AppPreferences { LowFreePercent = 0 }.Normalized();

        Assert.AreEqual(AppPreferences.MinLowFreePercent, p.LowFreePercent);
    }

    [TestMethod]
    public void Normalize_clamps_an_absurd_low_free_threshold_down_to_the_ceiling()
    {
        AppPreferences p = new AppPreferences { LowFreePercent = 900 }.Normalized();

        Assert.AreEqual(AppPreferences.MaxLowFreePercent, p.LowFreePercent);
    }

    [TestMethod]
    public void Normalize_keeps_a_rollup_threshold_of_zero_because_zero_means_never()
    {
        AppPreferences p = new AppPreferences { CacheRollupThreshold = 0 }.Normalized();

        Assert.AreEqual(0, p.CacheRollupThreshold);
    }

    [TestMethod]
    public void Normalize_floors_a_negative_rollup_threshold_at_never()
    {
        AppPreferences p = new AppPreferences { CacheRollupThreshold = -4 }.Normalized();

        Assert.AreEqual(0, p.CacheRollupThreshold);
    }

    [TestMethod]
    public void Normalize_clamps_retention_into_range()
    {
        Assert.AreEqual(
            AppPreferences.MinRetentionDays,
            new AppPreferences { HistoryRetentionDays = 1 }.Normalized().HistoryRetentionDays);

        Assert.AreEqual(
            AppPreferences.MaxRetentionDays,
            new AppPreferences { HistoryRetentionDays = 99_999 }.Normalized().HistoryRetentionDays);
    }

    [TestMethod]
    public void Normalize_leaves_an_in_range_value_alone()
    {
        AppPreferences p = new AppPreferences { LowFreePercent = 25, HistoryRetentionDays = 30 }.Normalized();

        Assert.AreEqual(25, p.LowFreePercent);
        Assert.AreEqual(30, p.HistoryRetentionDays);
    }

    [TestMethod]
    public void Raising_the_low_free_threshold_calls_out_a_volume_the_default_ignores()
    {
        // 20% free: quiet at the 15% default, reported once the threshold moves to 25%.
        VolumeInfo[] volumes = [Volume('C', OneTb, OneTb / 5)];

        IReadOnlyList<AttentionSignal> quiet = AttentionSignalBuilder.Build(new AttentionInputs
        {
            Volumes = volumes,
            HasSpaceScan = true,
        });

        IReadOnlyList<AttentionSignal> loud = AttentionSignalBuilder.Build(new AttentionInputs
        {
            Volumes = volumes,
            HasSpaceScan = true,
            LowFreeFraction = 0.25d,
        });

        Assert.IsFalse(quiet.Any(s => s.Id.StartsWith("LowSpace", StringComparison.Ordinal)));
        Assert.IsTrue(loud.Any(s => s.Id.StartsWith("LowSpace", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void The_low_space_detail_quotes_the_threshold_actually_in_force()
    {
        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(new AttentionInputs
        {
            Volumes = [Volume('C', OneTb, OneTb / 5)],
            HasSpaceScan = true,
            LowFreeFraction = 0.25d,
        });

        AttentionSignal low = signals.Single(s => s.Id.StartsWith("LowSpace", StringComparison.Ordinal));
        StringAssert.Contains(low.Detail, "25%");
    }

    [TestMethod]
    public void A_rollup_threshold_of_never_keeps_one_row_per_ecosystem()
    {
        AttentionInputs inputs = new()
        {
            Volumes = [Volume('C', OneTb, OneTb / 2), Volume('G', OneTb, OneTb / 2, devDrive: true)],
            HasSpaceScan = true,
            CacheRollupThreshold = 0,
            CachesOffDevDrive =
            [
                new CacheSignalInput("npm", @"C:\npm", 1_000, false),
                new CacheSignalInput("pip", @"C:\pip", 2_000, false),
                new CacheSignalInput("Cargo", @"C:\cargo", 3_000, false),
                new CacheSignalInput("vcpkg", @"C:\vcpkg", 4_000, false),
            ],
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.HasCount(4, signals.Where(s => s.Id.StartsWith("CacheOffDrive_", StringComparison.Ordinal)));
        Assert.IsFalse(signals.Any(s => s.Id == "CachesOffDrive"));
    }

    [TestMethod]
    public void Lowering_the_rollup_threshold_to_two_collapses_a_pair()
    {
        AttentionInputs inputs = new()
        {
            Volumes = [Volume('C', OneTb, OneTb / 2), Volume('G', OneTb, OneTb / 2, devDrive: true)],
            HasSpaceScan = true,
            CacheRollupThreshold = 2,
            CachesOffDevDrive =
            [
                new CacheSignalInput("npm", @"C:\npm", 1_000, false),
                new CacheSignalInput("pip", @"C:\pip", 2_000, false),
            ],
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.IsTrue(signals.Any(s => s.Id == "CachesOffDrive"));
        Assert.IsFalse(signals.Any(s => s.Id.StartsWith("CacheOffDrive_", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Turning_the_cache_watch_off_removes_every_cache_signal()
    {
        AttentionInputs inputs = new()
        {
            Volumes = [Volume('C', OneTb, OneTb / 2), Volume('G', OneTb, OneTb / 2, devDrive: true)],
            HasSpaceScan = true,
            WatchCachesOffDevDrive = false,
            CachesOffDevDrive =
            [
                new CacheSignalInput("npm", @"C:\npm", 1_000, false),
                new CacheSignalInput("pip", @"C:\pip", 2_000, false),
                new CacheSignalInput("Cargo", @"C:\cargo", 3_000, false),
            ],
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.IsFalse(signals.Any(s => s.RoomTag == "caches"));
    }

    [TestMethod]
    public void Turning_the_cache_watch_off_leaves_the_other_signals_alone()
    {
        AttentionInputs inputs = new()
        {
            Volumes = [Volume('C', OneTb, OneTb / 50), Volume('G', OneTb, OneTb / 2, devDrive: true)],
            HasSpaceScan = false,
            WatchCachesOffDevDrive = false,
            CachesOffDevDrive = [new CacheSignalInput("npm", @"C:\npm", 1_000, false)],
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.IsTrue(signals.Any(s => s.Id.StartsWith("LowSpace", StringComparison.Ordinal)));
        Assert.IsTrue(signals.Any(s => s.Id == "NoSpaceScan"));
    }

    [TestMethod]
    public void Trim_drops_readings_older_than_the_retention_window()
    {
        DateTimeOffset now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        FreeSpaceSample[] samples =
        [
            new() { TakenAtUtc = now.AddDays(-120), VolumeId = "G", TotalBytes = 100, FreeBytes = 50 },
            new() { TakenAtUtc = now.AddDays(-10), VolumeId = "G", TotalBytes = 100, FreeBytes = 40 },
            new() { TakenAtUtc = now, VolumeId = "G", TotalBytes = 100, FreeBytes = 30 },
        ];

        IReadOnlyList<FreeSpaceSample> kept = FreeSpaceHistory.Trim(samples, now, TimeSpan.FromDays(90));

        Assert.HasCount(2, kept);
        Assert.AreEqual(now.AddDays(-10), kept[0].TakenAtUtc);
    }

    [TestMethod]
    public void Trim_returns_oldest_first_regardless_of_input_order()
    {
        DateTimeOffset now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        FreeSpaceSample[] samples =
        [
            new() { TakenAtUtc = now, VolumeId = "G", TotalBytes = 100, FreeBytes = 30 },
            new() { TakenAtUtc = now.AddDays(-1), VolumeId = "G", TotalBytes = 100, FreeBytes = 40 },
        ];

        IReadOnlyList<FreeSpaceSample> kept = FreeSpaceHistory.Trim(samples, now, TimeSpan.FromDays(90));

        Assert.AreEqual(now.AddDays(-1), kept[0].TakenAtUtc);
    }

    [TestMethod]
    public void Trim_keeps_everything_when_retention_is_not_positive()
    {
        DateTimeOffset now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        FreeSpaceSample[] samples =
        [
            new() { TakenAtUtc = now.AddYears(-5), VolumeId = "G", TotalBytes = 100, FreeBytes = 50 },
        ];

        Assert.HasCount(1, FreeSpaceHistory.Trim(samples, now, TimeSpan.Zero));
    }

    [TestMethod]
    public void Trim_does_not_mutate_its_input()
    {
        DateTimeOffset now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        List<FreeSpaceSample> samples =
        [
            new() { TakenAtUtc = now.AddDays(-120), VolumeId = "G", TotalBytes = 100, FreeBytes = 50 },
            new() { TakenAtUtc = now, VolumeId = "G", TotalBytes = 100, FreeBytes = 30 },
        ];

        FreeSpaceHistory.Trim(samples, now, TimeSpan.FromDays(90));

        Assert.HasCount(2, samples);
    }
}
