using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;

namespace DevDriveCore.Tests;

/// <summary>
/// A volume we could not read must not be reported as a volume we read and found wanting.
/// "BitLocker had it locked" and "this is not a Dev Drive" are different sentences, and only one
/// of them is something we actually know.
/// </summary>
[TestClass]
public sealed class UnreadableVolumeTests
{
    private sealed class FakeNative(uint? flags) : INativeVolumeApi
    {
        public uint? QueryPersistentVolumeState(string volumeRoot) => flags;
    }

    private sealed class FakeStorage(StorageVolumeRecord volume) : IStorageQuery
    {
        public IReadOnlyList<StorageVolumeRecord> GetVolumes() => [volume];

        public IReadOnlyList<StoragePartitionRecord> GetPartitions() => [];

        public IReadOnlyList<StorageDiskRecord> GetDisks() => [];
    }

    private sealed class SilentRunner : IProcessRunner
    {
        public ProcessRunResult Run(string fileName, string arguments) => new(1, string.Empty, string.Empty);
    }

    private static StorageVolumeRecord Volume(bool sizeKnown = true) => new()
    {
        DriveLetter = 'G',
        Label = "Dev",
        FileSystem = "ReFS",
        SizeBytes = sizeKnown ? 1_000_000_000UL : 0UL,
        FreeBytes = sizeKnown ? 400_000_000UL : 0UL,
        IsSizeKnown = sizeKnown,
        DriveType = 3,
    };

    private static VolumeInfo Read(uint? flags, bool sizeKnown = true)
    {
        var service = new DevDriveService(
            new FakeStorage(Volume(sizeKnown)), new FakeNative(flags), new SilentRunner());
        return service.GetVolumes().Single();
    }

    [TestMethod]
    public void AVolumeWhoseFlagsCouldNotBeReadIsUnknownRatherThanNotADevDrive()
    {
        // The FSCTL returns nothing for a BitLocker-locked or not-ready volume, and that decoded
        // to (false, false) — indistinguishable from a plain NTFS disk we read successfully.
        VolumeInfo volume = Read(flags: null);

        Assert.IsFalse(volume.IsDevDriveStateKnown);
        Assert.AreEqual("Unknown", new VolumeRowViewModel(volume).DevDrivePillText);
    }

    [TestMethod]
    public void AnUnreadableVolumeIsFlaggedForAttentionNotGreyedOutAsUninteresting()
    {
        var row = new VolumeRowViewModel(Read(flags: null));

        Assert.AreEqual(
            "system",
            row.DevDrivePillKind,
            "'we could not tell' is worth a look; the mute pill would file it away as settled.");
    }

    [TestMethod]
    public void AVolumeWeDidReadStillAnswersPlainly()
    {
        Assert.AreEqual("No", new VolumeRowViewModel(Read(flags: 0)).DevDrivePillText);
        Assert.AreEqual("Trusted", new VolumeRowViewModel(Read(flags: 0x2000 | 0x4000)).DevDrivePillText);
        Assert.AreEqual("Untrusted", new VolumeRowViewModel(Read(flags: 0x2000)).DevDrivePillText);

        Assert.IsTrue(
            Read(flags: 0).IsDevDriveStateKnown,
            "a successful read that finds no Dev Drive flag really has learned something");
    }

    [TestMethod]
    public void AVolumeWmiCouldNotSizeShowsADashRatherThanZeroBytes()
    {
        VolumeInfo volume = Read(flags: 0, sizeKnown: false);
        var row = new VolumeRowViewModel(volume);

        Assert.IsFalse(volume.IsSizeKnown);
        Assert.AreEqual("—", row.CapacityText);
        Assert.AreEqual("—", row.FreeText);
        StringAssert.Contains(row.Description, "size unavailable");
    }

    [TestMethod]
    public void AGenuinelyEmptyVolumeStillReadsAsZero()
    {
        // The counterpart that keeps the dash honest: zero is a real answer when someone measured.
        var service = new DevDriveService(
            new FakeStorage(new StorageVolumeRecord
            {
                DriveLetter = 'H',
                FileSystem = "NTFS",
                SizeBytes = 0,
                FreeBytes = 0,
                IsSizeKnown = true,
                DriveType = 3,
            }),
            new FakeNative(0),
            new SilentRunner());

        var row = new VolumeRowViewModel(service.GetVolumes().Single());
        Assert.AreNotEqual("—", row.CapacityText, "we measured it; it is zero, not unknown");
    }
}
