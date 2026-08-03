using System.Text.Json;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class ModelAndScenarioTests
{
    [TestMethod]
    public void SnapshotRejectsDuplicateIds()
    {
        var duplicate = StorageTestBuilder.Node(
            StorageTestBuilder.RootId, StorageTestBuilder.RootId, "duplicate", @"M:\duplicate",
            StorageNodeKind.File, 1);

        Assert.ThrowsExactly<StorageSnapshotValidationException>(() =>
            StorageTestBuilder.Snapshot(nodes:
            [
                StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\",
                    StorageNodeKind.Folder, 1),
                duplicate,
            ]));
    }

    [TestMethod]
    public void SnapshotRejectsMissingParentAndInvalidBytes()
    {
        var orphan = StorageTestBuilder.Node(Guid.NewGuid(), Guid.NewGuid(), "orphan", @"M:\orphan",
            StorageNodeKind.File, 4);
        Assert.ThrowsExactly<StorageSnapshotValidationException>(() =>
            StorageTestBuilder.Snapshot(nodes:
            [
                StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\",
                    StorageNodeKind.Folder, 4),
                orphan,
            ]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            StorageTestBuilder.Node(Guid.NewGuid(), StorageTestBuilder.RootId, "bad", @"M:\bad",
                StorageNodeKind.File, -1));
    }

    [TestMethod]
    public void CoverageRejectsImpossibleValues()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScanCoverage(101, 100, []));
        Assert.AreEqual(0.75, new ScanCoverage(75, 100, [@"M:\denied"]).Ratio, 0.001);
    }

    [TestMethod]
    public void JsonValidationRejectsUnknownMembers()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "scenarioId": "bad",
              "snapshotId": "bad-1",
              "rootId": "10000000-0000-0000-0000-000000000001",
              "capturedAtUtc": "2026-08-02T12:00:00Z",
              "completion": "complete",
              "coverage": { "coveredBytes": 0, "totalBytes": 0, "deniedPaths": [] },
              "nodes": [],
              "unexpected": true
            }
            """;

        Assert.ThrowsExactly<JsonException>(() => StorageScenarioSerializer.Deserialize(json));
    }

    [TestMethod]
    public void CatalogContainsEveryApprovedScenarioAndBaselineIs712Gb()
    {
        var catalog = MockStorageScenarioCatalog.CreateDefault();
        string[] expected =
        [
            "baseline", "scanning", "partial", "failure", "empty",
            "no-provider", "stale-provider", "provider-unavailable",
            "awkward-names", "large", "changed-refresh",
        ];

        CollectionAssert.AreEquivalent(expected, catalog.ScenarioIds.ToArray());
        var baseline = catalog.GetScenario("baseline").Snapshots[0];
        Assert.AreEqual(712_000_000_000, baseline.Root.SizeBytes);
        Assert.AreEqual(SnapshotCompletion.Complete, baseline.Completion);
    }

    [TestMethod]
    public void ProviderMetadataDoesNotDefinePhysicalIdentity()
    {
        var provider = new StorageProviderContext(
            "WSL", "Ubuntu", "Mock correlation only", ProviderAvailability.Available,
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var withProvider = StorageTestBuilder.Node(
            StorageTestBuilder.FileId, StorageTestBuilder.FolderId, "disk.vhdx", @"M:\disk.vhdx",
            StorageNodeKind.File, 42, provider: provider);
        var withoutProvider = withProvider with { Provider = null };

        Assert.AreEqual(withProvider.Id, withoutProvider.Id);
        Assert.AreEqual(withProvider.PhysicalPath, withoutProvider.PhysicalPath);
        Assert.AreEqual(withProvider.SizeBytes, withoutProvider.SizeBytes);
    }
}
