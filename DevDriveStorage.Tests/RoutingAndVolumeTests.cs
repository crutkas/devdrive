using System.Collections.Immutable;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class RoutingStorageSnapshotSourceTests
{
    [TestMethod]
    public async Task LivePrefixRoutesToLiveSourceWithStrippedPath()
    {
        var mock = new RecordingSource("mock");
        var live = new RecordingSource("live");
        var routing = new RoutingStorageSnapshotSource(mock, live);

        await routing.GetSnapshotAsync(
            new StorageSnapshotRequest(RoutingStorageSnapshotSource.LiveScenarioId(@"C:\")),
            null,
            CancellationToken.None);

        Assert.IsNull(mock.LastScenarioId, "mock must not be invoked for a live id");
        Assert.AreEqual(@"C:\", live.LastScenarioId, "the live prefix must be stripped to the raw path");
    }

    [TestMethod]
    public async Task NonLiveScenarioRoutesToMock()
    {
        var mock = new RecordingSource("mock");
        var live = new RecordingSource("live");
        var routing = new RoutingStorageSnapshotSource(mock, live);

        await routing.GetSnapshotAsync(
            new StorageSnapshotRequest("baseline"), null, CancellationToken.None);

        Assert.AreEqual("baseline", mock.LastScenarioId);
        Assert.IsNull(live.LastScenarioId, "live must not be invoked for a mock id");
    }

    [TestMethod]
    public void ScenarioIdHelpersRoundTrip()
    {
        string id = RoutingStorageSnapshotSource.LiveScenarioId(@"D:\");
        Assert.IsTrue(RoutingStorageSnapshotSource.IsLiveScenario(id));
        Assert.IsFalse(RoutingStorageSnapshotSource.IsLiveScenario("baseline"));
    }

    private sealed class RecordingSource(string tag) : IStorageSnapshotSource
    {
        public string? LastScenarioId { get; private set; }

        public Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastScenarioId = request.ScenarioId;
            var root = new StorageNode(
                Guid.NewGuid(), null, tag, $@"M:\{tag}", StorageNodeKind.Folder, 0, 0,
                DateTimeOffset.UnixEpoch, null);
            return Task.FromResult(new StorageSnapshot(
                StorageSnapshot.CurrentSchemaVersion,
                request.ScenarioId,
                $"{tag}-1",
                root.Id,
                DateTimeOffset.UnixEpoch,
                SnapshotCompletion.Complete,
                new ScanCoverage(0, 0, []),
                [root]));
        }
    }
}

[TestClass]
public sealed class VolumeProviderTests
{
    [TestMethod]
    public void SystemVolumeProviderReturnsUsableFixedVolumes()
    {
        IReadOnlyList<StorageVolume> volumes = new SystemVolumeProvider().GetFixedVolumes();

        Assert.IsNotEmpty(volumes, "at least one fixed volume is expected on a dev machine");
        foreach (StorageVolume volume in volumes)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(volume.RootPath));
            Assert.IsFalse(string.IsNullOrWhiteSpace(volume.DriveLetter));
            Assert.IsGreaterThan(0, volume.CapacityBytes, $"{volume.DriveLetter} should report capacity");
            Assert.IsGreaterThanOrEqualTo(0, volume.FreeBytes);
            Assert.IsGreaterThanOrEqualTo(0, volume.UsedBytes);
        }
    }

    [TestMethod]
    public void StorageVolumeComputesDisplayAndUsedBytes()
    {
        var dev = new StorageVolume(
            @"D:\", "D:", "Dev Drive", "ReFS", 1000, 400, IsReFS: true, IsDevDrive: true, IsTrusted: true);
        Assert.AreEqual(600, dev.UsedBytes);
        Assert.AreEqual("Dev Drive (D:)", dev.DisplayName);
        Assert.AreEqual("Dev Drive · trusted", dev.ClassificationDisplay);

        var unlabeled = dev with { Label = "" };
        Assert.AreEqual("D:", unlabeled.DisplayName);
    }

    /// <summary>
    /// A volume the probe could not read must not be described as an ordinary ReFS volume. That is
    /// the exact shape a BitLockered or locked Dev Drive takes: the FSCTL never ran, so every flag
    /// is false, and false is indistinguishable from a genuine plain-NTFS answer of 0x0000.
    /// </summary>
    [TestMethod]
    public void AnUnreadVolumeDoesNotClaimToBeAPlainReFSVolume()
    {
        var unread = new StorageVolume(
            @"E:\", "E:", "Locked", "ReFS", 0, 0, IsReFS: true, IsDevDrive: false, IsTrusted: false)
        {
            IsDevDriveStateKnown = false,
        };

        Assert.AreNotEqual(
            "ReFS volume",
            unread.ClassificationDisplay,
            "an unread volume must not be reported as one we checked and found ordinary");
        StringAssert.Contains(unread.ClassificationDisplay, "unknown");
    }

    /// <summary>
    /// The other direction, which matters just as much: an unknown must not be promoted into a
    /// positive claim either. Silence about which way it went is the only honest answer.
    /// </summary>
    [TestMethod]
    public void AnUnreadVolumeIsNotReportedAsADevDriveEither()
    {
        var unread = new StorageVolume(
            @"E:\", "E:", "Locked", "ReFS", 0, 0, IsReFS: true, IsDevDrive: false, IsTrusted: false)
        {
            IsDevDriveStateKnown = false,
        };

        Assert.IsFalse(unread.ClassificationDisplay.Contains("Dev Drive ·", StringComparison.Ordinal));
        Assert.IsFalse(unread.ClassificationDisplay.Contains("trusted", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The counter-test. Without it the rule above is satisfied by saying "unknown" about
    /// everything, which trades a false claim for a useless one.
    /// </summary>
    [TestMethod]
    public void AVolumeWeDidReadStillReportsWhatWeFound()
    {
        var plainReFS = new StorageVolume(
            @"E:\", "E:", "Data", "ReFS", 1000, 400, IsReFS: true, IsDevDrive: false, IsTrusted: false);
        var plainNtfs = new StorageVolume(
            @"C:\", "C:", "Windows", "NTFS", 1000, 400, IsReFS: false, IsDevDrive: false, IsTrusted: false);

        Assert.AreEqual("ReFS volume", plainReFS.ClassificationDisplay);
        Assert.AreEqual("NTFS", plainNtfs.ClassificationDisplay);
    }

    /// <summary>
    /// Both flags default to known, so the mock scenarios, the fakes and every hand-built volume in
    /// the suite keep meaning what they always meant. Only the live probe clears them.
    /// </summary>
    [TestMethod]
    public void AHandBuiltVolumeIsKnownUnlessItSaysOtherwise()
    {
        var volume = new StorageVolume(
            @"C:\", "C:", "Windows", "NTFS", 1000, 400, IsReFS: false, IsDevDrive: false, IsTrusted: false);

        Assert.IsTrue(volume.IsDevDriveStateKnown);
        Assert.IsTrue(volume.IsSizeKnown);
    }

    /// <summary>
    /// The guard on the new nullable plumbing: if <c>flags.HasValue</c> or the size pair were wired
    /// backwards, every volume on a perfectly readable machine would start reporting as unknown and
    /// nothing else in the suite would notice.
    /// </summary>
    [TestMethod]
    public void EveryFixedVolumeOnThisMachineIsActuallyRead()
    {
        IReadOnlyList<StorageVolume> volumes = new SystemVolumeProvider().GetFixedVolumes();

        Assert.IsNotEmpty(volumes);
        foreach (StorageVolume volume in volumes)
        {
            Assert.IsTrue(
                volume.IsDevDriveStateKnown,
                $"{volume.DriveLetter} is a readable fixed volume; its Dev Drive state should be known");
            Assert.IsTrue(
                volume.IsSizeKnown,
                $"{volume.DriveLetter} reported {volume.CapacityBytes} bytes, so its size should be known");
        }
    }

    /// <summary>
    /// The invariant that makes the flag trustworthy: a positive verdict can only come from a probe
    /// that ran. A volume claiming to be a Dev Drive while claiming nobody asked is incoherent.
    /// </summary>
    [TestMethod]
    public void NoVolumeClaimsADevDriveVerdictItNeverProbedFor()
    {
        foreach (StorageVolume volume in new SystemVolumeProvider().GetFixedVolumes())
        {
            if (volume.IsDevDrive || volume.IsTrusted)
            {
                Assert.IsTrue(
                    volume.IsDevDriveStateKnown,
                    $"{volume.DriveLetter} reports Dev Drive flags but claims the state is unknown");
            }
        }
    }
}
