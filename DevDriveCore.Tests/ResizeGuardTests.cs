using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="ResizeGuard"/> — the pure, deterministic don't-touch-the-OS firewall that
/// decides whether a <see cref="ResizePlan"/> may shrink a volume to carve out a Dev Drive. Every case
/// is driven by a hand-built <see cref="DiskLayoutSnapshot"/>, so the full guard suite runs WITHOUT a
/// real disk: no Storage query, no elevation, no mutation.
/// </summary>
[TestClass]
public sealed class ResizeGuardTests
{
    private const ulong Gib = 1024UL * 1024UL * 1024UL;
    private const ulong Mib = 1024UL * 1024UL;

    // A healthy, resizable Basic NTFS data volume on a fixed NVMe disk: 500 GiB partition, shrinkable to
    // 200 GiB (so 300 GiB is reclaimable). The baseline every "deny" test perturbs by exactly one field.
    private static DiskLayoutSnapshot HealthySnapshot(Action<DiskLayoutSnapshotBuilder>? tweak = null)
    {
        var b = new DiskLayoutSnapshotBuilder
        {
            SourceResolved = true,
            SourceVolumeLetter = 'C',
            FileSystem = "NTFS",
            PartitionType = "Basic",
            GptType = string.Empty,
            IsSystemPartition = false,
            PartitionSizeBytes = 500UL * Gib,
            SupportedSizeMinBytes = 200UL * Gib,
            SupportedSizeMaxBytes = 500UL * Gib,
            DiskNumber = 0,
            PartitionStyle = "GPT",
            IsDiskOffline = false,
            IsDiskReadOnly = false,
            IsRemovable = false,
            BusType = "NVMe",
            PartitionAlignmentBytes = 0,
        };
        tweak?.Invoke(b);
        return b.Build();
    }

    private static ResizePlan Plan(char source = 'C', ulong shrink = 100UL * Gib, char target = 'D', string label = "DevDrive") =>
        new() { SourceVolumeLetter = source, ShrinkBytes = shrink, NewDriveLetter = target, Label = label };

    // ---- happy path ----------------------------------------------------------------------------

    [TestMethod]
    public void Evaluate_HealthyPlan_CanProceed()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot());

        Assert.IsTrue(f.CanProceed, f.Reason);
        Assert.AreEqual('C', f.SourceVolumeLetter);
        Assert.AreEqual('D', f.NewDriveLetter);
        Assert.AreEqual(100UL * Gib, f.AlignedShrinkBytes);
        Assert.AreEqual(300UL * Gib, f.ReclaimableBytes);
        Assert.AreEqual(500UL * Gib, f.SourceSizeBytesBefore);
        Assert.AreEqual(400UL * Gib, f.SourceSizeBytesAfter);
        Assert.HasCount(3, f.Steps);
        Assert.IsTrue(f.IsReadOnlyProbe);
    }

    [TestMethod]
    public void Evaluate_LowercasePlanLetters_AreNormalizedToUppercase()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(source: 'c', target: 'd'), HealthySnapshot());

        Assert.IsTrue(f.CanProceed, f.Reason);
        Assert.AreEqual('C', f.SourceVolumeLetter);
        Assert.AreEqual('D', f.NewDriveLetter);
    }

    // ---- F6: target-letter-in-use denial -------------------------------------------------------

    [TestMethod]
    public void Evaluate_TargetLetterAlreadyInUse_IsDenied()
    {
        // The requested Dev Drive letter 'D' is already mounted (C + D in use) — carving onto it would
        // collide, so the guard refuses BEFORE any disk command.
        ResizeFeasibility f = ResizeGuard.Evaluate(
            Plan(target: 'D'),
            HealthySnapshot(b => b.DriveLettersInUse = "CD"));

        AssertDenied(f, "already in use");
    }

    [TestMethod]
    public void Evaluate_TargetLetterInUse_IsCaseInsensitive()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(
            Plan(target: 'd'),
            HealthySnapshot(b => b.DriveLettersInUse = "cd"));

        AssertDenied(f, "already in use");
    }

    [TestMethod]
    public void Evaluate_TargetLetterFree_StillProceeds_WhenOthersInUse()
    {
        // C and G are mounted, but the requested Dev Drive letter 'D' is free — the guard proceeds.
        ResizeFeasibility f = ResizeGuard.Evaluate(
            Plan(target: 'D'),
            HealthySnapshot(b => b.DriveLettersInUse = "CG"));

        Assert.IsTrue(f.CanProceed, f.Reason);
    }

    // ---- plan-sanity denials -------------------------------------------------------------------

    [TestMethod]
    [DataRow('1')] // below 'A'
    [DataRow('!')]
    public void Evaluate_InvalidSourceLetter_Denied(char source)
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(source: source), HealthySnapshot());
        AssertDenied(f, "source");
    }

    [TestMethod]
    public void Evaluate_InvalidTargetLetter_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(target: '7'), HealthySnapshot());
        AssertDenied(f, "Dev Drive letter");
    }

    [TestMethod]
    public void Evaluate_SourceEqualsTarget_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(source: 'C', target: 'c'), HealthySnapshot());
        AssertDenied(f, "must differ");
    }

    [TestMethod]
    public void Evaluate_ZeroShrink_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(shrink: 0), HealthySnapshot());
        AssertDenied(f, "zero");
    }

    [TestMethod]
    public void Evaluate_SourceNotResolved_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.SourceResolved = false));
        AssertDenied(f, "find volume");
    }

    // ---- disk-level denials --------------------------------------------------------------------

    [TestMethod]
    public void Evaluate_OfflineDisk_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.IsDiskOffline = true));
        AssertDenied(f, "offline");
    }

    [TestMethod]
    public void Evaluate_ReadOnlyDisk_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.IsDiskReadOnly = true));
        AssertDenied(f, "read-only");
    }

    [TestMethod]
    public void Evaluate_RemovableDisk_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.IsRemovable = true));
        AssertDenied(f, "removable");
    }

    [TestMethod]
    [DataRow("RAW")]
    [DataRow("Unknown")]
    [DataRow("")]
    public void Evaluate_UninitializedDisk_Denied(string partitionStyle)
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.PartitionStyle = partitionStyle));
        AssertDenied(f, "not initialized");
    }

    // ---- protected-partition denials (Type string) ---------------------------------------------

    [TestMethod]
    [DataRow("Reserved")]
    [DataRow("Recovery")]
    [DataRow("System")]
    public void Evaluate_ProtectedPartitionType_Denied(string type)
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.PartitionType = type));
        AssertDenied(f, "protected partition");
    }

    [TestMethod]
    public void Evaluate_IsSystemPartition_Denied()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.IsSystemPartition = true));
        AssertDenied(f, "protected partition");
    }

    // ---- protected-partition denials (GPT type GUID — can't be disguised by a fake "Basic" Type) ----

    [TestMethod]
    [DataRow("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")] // EFI System
    [DataRow("e3c9e316-0b5c-4db8-817d-f92df00215ae")] // Microsoft Reserved (MSR)
    [DataRow("de94bba4-06d1-4d40-a16a-bfd50179d6ac")] // Windows Recovery
    [DataRow("{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}")] // braced + uppercase still recognised
    public void Evaluate_ProtectedGptGuid_Denied_EvenWhenTypeClaimsBasic(string gptType)
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s =>
        {
            s.PartitionType = "Basic"; // crafted: claim Basic while carrying a protected GPT GUID
            s.GptType = gptType;
        }));
        AssertDenied(f, "protected partition");
    }

    // ---- file-system denials -------------------------------------------------------------------

    [TestMethod]
    [DataRow("exFAT")]
    [DataRow("FAT32")]
    [DataRow("RAW")]
    [DataRow("")]
    public void Evaluate_NonResizableFileSystem_Denied(string fileSystem)
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.FileSystem = fileSystem));
        AssertDenied(f, "can't be safely resized");
    }

    [TestMethod]
    [DataRow("NTFS")]
    [DataRow("ntfs")]
    [DataRow("ReFS")]
    [DataRow("refs")]
    public void Evaluate_ResizableFileSystem_CanProceed(string fileSystem)
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(), HealthySnapshot(s => s.FileSystem = fileSystem));
        Assert.IsTrue(f.CanProceed, f.Reason);
    }

    // ---- reclaimable / minimum-size denials ----------------------------------------------------

    [TestMethod]
    public void Evaluate_ShrinkExceedsReclaimable_Denied()
    {
        // 400 GiB requested, only 300 GiB reclaimable.
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(shrink: 400UL * Gib), HealthySnapshot());
        AssertDenied(f, "exceeds");
    }

    [TestMethod]
    public void Evaluate_BelowMinimumDevDrive_Denied()
    {
        // 40 GiB is within reclaimable but under the 50 GiB Dev Drive minimum.
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(shrink: 40UL * Gib), HealthySnapshot());
        AssertDenied(f, "at least");
    }

    [TestMethod]
    public void Evaluate_ExactlyMinimumDevDrive_CanProceed()
    {
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(shrink: ResizeGuard.MinimumDevDriveBytes), HealthySnapshot());
        Assert.IsTrue(f.CanProceed, f.Reason);
        Assert.AreEqual(ResizeGuard.MinimumDevDriveBytes, f.AlignedShrinkBytes);
    }

    // ---- alignment -----------------------------------------------------------------------------

    [TestMethod]
    public void Evaluate_RoundsShrinkDownToAlignment()
    {
        // 105 GiB requested with 10 GiB alignment rounds down to 100 GiB.
        ResizeFeasibility f = ResizeGuard.Evaluate(
            Plan(shrink: 105UL * Gib),
            HealthySnapshot(s => s.PartitionAlignmentBytes = 10UL * Gib));

        Assert.IsTrue(f.CanProceed, f.Reason);
        Assert.AreEqual(105UL * Gib, f.RequestedShrinkBytes);
        Assert.AreEqual(100UL * Gib, f.AlignedShrinkBytes);
    }

    [TestMethod]
    public void Evaluate_AlignmentLargerThanShrink_RoundsToZero_Denied()
    {
        // 100 GiB requested with 1 TiB alignment rounds to zero.
        ResizeFeasibility f = ResizeGuard.Evaluate(
            Plan(shrink: 100UL * Gib),
            HealthySnapshot(s => s.PartitionAlignmentBytes = 1024UL * Gib));
        AssertDenied(f, "rounds to zero");
    }

    [TestMethod]
    public void Evaluate_DefaultsTo1MibAlignment_WhenSnapshotSaysZero()
    {
        // Shrink of (50 GiB + 0.5 MiB) rounds DOWN to a multiple of the default 1 MiB alignment.
        ulong shrink = (ResizeGuard.MinimumDevDriveBytes + 4UL * Gib) + (Mib / 2UL);
        ResizeFeasibility f = ResizeGuard.Evaluate(Plan(shrink: shrink), HealthySnapshot());
        Assert.IsTrue(f.CanProceed, f.Reason);
        Assert.AreEqual(0UL, f.AlignedShrinkBytes % Mib, "Aligned size must be a multiple of the 1 MiB default.");
        // The 0.5 MiB remainder is dropped, leaving exactly 54 GiB (50 GiB minimum + 4 GiB).
        Assert.AreEqual(ResizeGuard.MinimumDevDriveBytes + 4UL * Gib, f.AlignedShrinkBytes);
    }

    // ---- pure helpers --------------------------------------------------------------------------

    [TestMethod]
    public void ReclaimableBytes_IsPartitionSizeMinusSupportedMin()
    {
        Assert.AreEqual(300UL * Gib, ResizeGuard.ReclaimableBytes(HealthySnapshot()));
    }

    [TestMethod]
    public void ReclaimableBytes_NeverNegative_WhenMinExceedsSize()
    {
        DiskLayoutSnapshot s = HealthySnapshot(b =>
        {
            b.PartitionSizeBytes = 100UL * Gib;
            b.SupportedSizeMinBytes = 200UL * Gib;
        });
        Assert.AreEqual(0UL, ResizeGuard.ReclaimableBytes(s));
    }

    [TestMethod]
    [DataRow(105UL, 10UL, 100UL)]
    [DataRow(100UL, 10UL, 100UL)]
    [DataRow(9UL, 10UL, 0UL)]
    [DataRow(100UL, 0UL, 100UL)] // alignment 0 is a no-op
    public void RoundDownToAlignment_FloorsToMultiple(ulong value, ulong alignment, ulong expected)
    {
        Assert.AreEqual(expected, ResizeGuard.RoundDownToAlignment(value, alignment));
    }

    // ---- null guards ---------------------------------------------------------------------------

    [TestMethod]
    public void Evaluate_NullArgs_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ResizeGuard.Evaluate(null!, HealthySnapshot()));
        Assert.ThrowsExactly<ArgumentNullException>(() => ResizeGuard.Evaluate(Plan(), null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ResizeGuard.ReclaimableBytes(null!));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void AssertDenied(ResizeFeasibility f, string reasonContains)
    {
        Assert.IsFalse(f.CanProceed, "Plan should have been denied.");
        Assert.AreEqual(0UL, f.AlignedShrinkBytes, "A denied plan must not report an aligned shrink size.");
        Assert.IsTrue(f.IsReadOnlyProbe);
        StringAssert.Contains(f.Reason, reasonContains, StringComparison.OrdinalIgnoreCase);
    }

    // Mutable builder so each test can tweak exactly one field of the immutable snapshot record.
    private sealed class DiskLayoutSnapshotBuilder
    {
        public bool SourceResolved { get; set; }
        public char SourceVolumeLetter { get; set; }
        public string FileSystem { get; set; } = string.Empty;
        public string PartitionType { get; set; } = string.Empty;
        public string GptType { get; set; } = string.Empty;
        public bool IsSystemPartition { get; set; }
        public ulong PartitionSizeBytes { get; set; }
        public ulong SupportedSizeMinBytes { get; set; }
        public ulong SupportedSizeMaxBytes { get; set; }
        public int DiskNumber { get; set; }
        public string PartitionStyle { get; set; } = string.Empty;
        public bool IsDiskOffline { get; set; }
        public bool IsDiskReadOnly { get; set; }
        public bool IsRemovable { get; set; }
        public string BusType { get; set; } = string.Empty;
        public ulong PartitionAlignmentBytes { get; set; }
        public string DriveLettersInUse { get; set; } = string.Empty;

        public DiskLayoutSnapshot Build() => new()
        {
            SourceResolved = SourceResolved,
            SourceVolumeLetter = SourceVolumeLetter,
            FileSystem = FileSystem,
            PartitionType = PartitionType,
            GptType = GptType,
            IsSystemPartition = IsSystemPartition,
            PartitionSizeBytes = PartitionSizeBytes,
            SupportedSizeMinBytes = SupportedSizeMinBytes,
            SupportedSizeMaxBytes = SupportedSizeMaxBytes,
            DiskNumber = DiskNumber,
            PartitionStyle = PartitionStyle,
            IsDiskOffline = IsDiskOffline,
            IsDiskReadOnly = IsDiskReadOnly,
            IsRemovable = IsRemovable,
            BusType = BusType,
            PartitionAlignmentBytes = PartitionAlignmentBytes,
            DriveLettersInUse = DriveLettersInUse,
        };
    }
}
