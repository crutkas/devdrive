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
}
