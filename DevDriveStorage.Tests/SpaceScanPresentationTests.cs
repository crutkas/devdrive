using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class SpaceScanPresentationTests
{
    [TestMethod]
    public async Task ChoosingVolumeDoesNotScanAndCannotRedirectRunningScan()
    {
        var source = new ControlledSource();
        var vm = new StorageExplorerViewModel(source) { SelectedVolumePath = @"M:\" };
        Assert.AreEqual(0, source.Requests);
        Assert.AreEqual(ExplorerScanState.Idle, vm.ScanState);

        Task scan = vm.LoadScenarioAsync(vm.SelectedVolumePath);
        Assert.IsTrue(vm.IsScanning);
        Assert.AreEqual(0, vm.Progress, "Unknown progress must remain indeterminate, not pretend to be measured.");
        vm.SelectedVolumePath = @"N:\";
        Assert.AreEqual(@"M:\", vm.SelectedVolumePath);
        Assert.AreEqual(@"M:\", vm.ScenarioId);

        vm.CancelScan();
        await scan;
        Assert.IsFalse(vm.IsScanning);
        Assert.AreEqual(ExplorerScanState.Cancelled, vm.ScanState);
        Assert.AreEqual(@"M:\", vm.SelectedVolumePath);
        vm.SelectedVolumePath = @"N:\";
        Assert.AreEqual(@"N:\", vm.SelectedVolumePath);
        Assert.AreEqual(1, source.Requests);
    }

    [TestMethod]
    public async Task CompletionPartialAndFailureExitScanningAndRetainScope()
    {
        foreach (string scenario in new[] { "baseline", "partial", "failure" })
        {
            var vm = new StorageExplorerViewModel(new MockStorageSnapshotSource(
                MockStorageScenarioCatalog.CreateDefault(), TimeSpan.Zero))
            {
                SelectedVolumePath = scenario,
            };
            List<bool> states = [];
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.IsScanning)) states.Add(vm.IsScanning);
            };
            await vm.LoadScenarioAsync(scenario);
            CollectionAssert.AreEqual(new[] { true, false }, states);
            Assert.AreEqual(scenario, vm.SelectedVolumePath);
            if (scenario == "partial")
            {
                Assert.IsTrue(vm.HasCoverageDetail);
                StringAssert.Contains(vm.CoverageDetail, "denied");
            }
            else if (scenario == "failure")
            {
                Assert.IsTrue(vm.HasError);
                await vm.RetryAsync();
                Assert.AreEqual(ExplorerScanState.Completed, vm.ScanState);
                Assert.IsFalse(vm.IsScanning);
            }
        }
    }

    [TestMethod]
    public void PickerReportsUsedRatherThanFreeCapacityAndKeepsUnknownHonest()
    {
        var volume = new StorageVolume(@"M:\", "M:", "Mock", "NTFS", 1000, 250, false, false, false);
        var entry = new VolumeStripEntry(volume, false, VolumeAccentRole.Other, 0);
        Assert.IsTrue(entry.HasKnownCapacity);
        Assert.AreEqual(.75, entry.UsedFraction, .0001);
        Assert.AreEqual(0, (entry with { Volume = volume with { FreeBytes = 2000 } }).UsedFraction);
        Assert.AreEqual(1, (entry with { Volume = volume with { FreeBytes = -10 } }).UsedFraction);
        Assert.IsFalse((entry with { Volume = volume with { IsSizeKnown = false } }).HasKnownCapacity);
        Assert.AreEqual(0, (entry with { Volume = volume with { IsSizeKnown = false } }).UsedFraction);
        Assert.IsFalse((entry with { Volume = volume with { CapacityBytes = 0 } }).HasKnownCapacity);
    }

    private sealed class ControlledSource : IStorageSnapshotSource
    {
        public int Requests { get; private set; }

        public async Task<StorageSnapshot> GetSnapshotAsync(StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress, CancellationToken cancellationToken)
        {
            Requests++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return StorageTestBuilder.Snapshot();
        }
    }
}
