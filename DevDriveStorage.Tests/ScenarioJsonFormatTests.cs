using System.Text.Json;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// Pins the scenario document format <see cref="StorageScenarioSerializer"/> accepts: camelCase
/// member names matched case-sensitively, camelCase enum strings, unknown members rejected, and
/// strict numbers. The embedded scenarios and every fixture written against them depend on it.
/// </summary>
/// <remarks>
/// These expectations were captured from the reflection-based serializer the scenario reader used
/// before it moved to a source-generated context, so a pass here means the move changed nothing.
/// </remarks>
[TestClass]
public sealed class ScenarioJsonFormatTests
{
    private const string RootId = "11111111-1111-1111-1111-111111111111";
    private const string FileId = "22222222-2222-2222-2222-222222222222";

    [TestMethod]
    public void EveryDocumentMemberBindsFromItsCamelCaseName()
    {
        StorageSnapshot snapshot = StorageScenarioSerializer.Deserialize(Document());

        Assert.AreEqual(1, snapshot.SchemaVersion);
        Assert.AreEqual("format", snapshot.ScenarioId);
        Assert.AreEqual("format-1", snapshot.SnapshotId);
        Assert.AreEqual(Guid.Parse(RootId), snapshot.RootId);
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-02T12:00:00Z"), snapshot.CapturedAtUtc);
        Assert.AreEqual(SnapshotCompletion.Partial, snapshot.Completion);
        Assert.AreEqual(40, snapshot.Coverage.CoveredBytes);
        Assert.AreEqual(50, snapshot.Coverage.TotalBytes);
        CollectionAssert.AreEqual(new[] { @"M:\denied" }, snapshot.Coverage.DeniedPaths.ToArray());
        Assert.HasCount(2, snapshot.Nodes);

        StorageNode root = snapshot.Nodes.Single(n => n.Id == Guid.Parse(RootId));
        Assert.IsNull(root.ParentId);
        Assert.AreEqual(StorageNodeKind.Folder, root.Kind);
        Assert.IsNull(root.Provider);
        Assert.IsNull(root.LogicalBytes, "an omitted logicalBytes takes its declared default");

        StorageNode file = snapshot.Nodes.Single(n => n.Id == Guid.Parse(FileId));
        Assert.AreEqual(Guid.Parse(RootId), file.ParentId);
        Assert.AreEqual("disk.vhdx", file.Name);
        Assert.AreEqual(@"M:\disk.vhdx", file.PhysicalPath);
        Assert.AreEqual(StorageNodeKind.File, file.Kind);
        Assert.AreEqual(40, file.SizeBytes);
        Assert.AreEqual(0, file.ItemCount);
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-01T08:30:00Z"), file.ModifiedAtUtc);
        Assert.AreEqual(4096L, file.LogicalBytes);
        Assert.AreEqual(
            new StorageProviderContext(
                "WSL",
                "Ubuntu",
                null,
                ProviderAvailability.Stale,
                DateTimeOffset.Parse("2026-08-01T09:00:00Z")),
            file.Provider);
    }

    [TestMethod]
    public void MemberNamesAreCaseSensitive()
    {
        string json = Document().Replace("\"scenarioId\"", "\"ScenarioId\"", StringComparison.Ordinal);

        Assert.ThrowsExactly<JsonException>(() => StorageScenarioSerializer.Deserialize(json));
    }

    [TestMethod]
    public void UnknownNestedMembersAreRejected()
    {
        string json = Document().Replace("\"itemCount\": 0,", "\"itemCount\": 0, \"extra\": 1,", StringComparison.Ordinal);

        Assert.ThrowsExactly<JsonException>(() => StorageScenarioSerializer.Deserialize(json));
    }

    [TestMethod]
    public void EnumsAcceptCamelCaseAnyCaseAndIntegerValues()
    {
        Assert.AreEqual(
            SnapshotCompletion.Complete,
            StorageScenarioSerializer.Deserialize(WithCompletion("\"complete\"")).Completion);
        Assert.AreEqual(
            SnapshotCompletion.Partial,
            StorageScenarioSerializer.Deserialize(WithCompletion("\"Partial\"")).Completion);
        Assert.AreEqual(
            SnapshotCompletion.Partial,
            StorageScenarioSerializer.Deserialize(WithCompletion("1")).Completion);
        Assert.ThrowsExactly<JsonException>(() => StorageScenarioSerializer.Deserialize(WithCompletion("\"half\"")));
    }

    [TestMethod]
    public void NumbersMustBeJsonNumbers()
    {
        string json = Document().Replace("\"sizeBytes\": 40,", "\"sizeBytes\": \"40\",", StringComparison.Ordinal);

        Assert.ThrowsExactly<JsonException>(() => StorageScenarioSerializer.Deserialize(json));
    }

    [TestMethod]
    public void OmittedMembersBindAsTheirDefaults()
    {
        string json = $$"""
            {
              "schemaVersion": 1, "scenarioId": "s", "snapshotId": "s-1", "rootId": "{{RootId}}",
              "capturedAtUtc": "2026-08-02T12:00:00+02:00", "completion": "complete",
              "coverage": { "coveredBytes": 0, "totalBytes": 0, "deniedPaths": [] },
              "nodes": [
                { "id": "{{RootId}}", "name": "root", "physicalPath": "M:\\", "kind": "folder",
                  "sizeBytes": 0, "itemCount": 1, "modifiedAtUtc": "2026-08-02T12:00:00Z" },
                { "id": "{{FileId}}", "parentId": "{{RootId}}", "name": "f", "physicalPath": "M:\\f",
                  "kind": "file", "sizeBytes": 0, "itemCount": 0, "modifiedAtUtc": "2026-08-02T12:00:00Z",
                  "provider": { "providerName": "WSL", "availability": 2 } }
              ]
            }
            """;

        StorageSnapshot snapshot = StorageScenarioSerializer.Deserialize(json);

        Assert.AreEqual(DateTimeOffset.Parse("2026-08-02T10:00:00Z"), snapshot.CapturedAtUtc);
        StorageNode root = snapshot.Nodes.Single(n => n.Id == Guid.Parse(RootId));
        Assert.IsNull(root.ParentId);
        Assert.IsNull(root.Provider);
        Assert.IsNull(root.LogicalBytes);
        StorageNode file = snapshot.Nodes.Single(n => n.Id == Guid.Parse(FileId));
        Assert.AreEqual(
            new StorageProviderContext("WSL", null, null, ProviderAvailability.Unavailable, null),
            file.Provider);
    }

    [TestMethod]
    public void EveryEmbeddedScenarioStillLoads()
    {
        MockStorageScenarioCatalog catalog = MockStorageScenarioCatalog.CreateDefault();

        foreach (string id in catalog.ScenarioIds)
        {
            Assert.IsNotEmpty(catalog.GetScenario(id).Snapshots, id);
        }
    }

    private static string WithCompletion(string value) =>
        Document().Replace("\"completion\": \"partial\"", $"\"completion\": {value}", StringComparison.Ordinal);

    private static string Document() =>
        $$"""
        {
          "schemaVersion": 1,
          "scenarioId": "format",
          "snapshotId": "format-1",
          "rootId": "{{RootId}}",
          "capturedAtUtc": "2026-08-02T12:00:00Z",
          "completion": "partial",
          "coverage": { "coveredBytes": 40, "totalBytes": 50, "deniedPaths": ["M:\\denied"] },
          "nodes": [
            {
              "id": "{{RootId}}",
              "parentId": null,
              "name": "Dev Drive (M:)",
              "physicalPath": "M:\\",
              "kind": "folder",
              "sizeBytes": 40,
              "itemCount": 1,
              "modifiedAtUtc": "2026-08-02T12:00:00Z",
              "provider": null
            },
            {
              "id": "{{FileId}}",
              "parentId": "{{RootId}}",
              "name": "disk.vhdx",
              "physicalPath": "M:\\disk.vhdx",
              "kind": "file",
              "sizeBytes": 40,
              "itemCount": 0,
              "modifiedAtUtc": "2026-08-01T08:30:00Z",
              "provider": {
                "providerName": "WSL",
                "logicalIdentity": "Ubuntu",
                "evidence": null,
                "availability": "stale",
                "observedAtUtc": "2026-08-01T09:00:00Z"
              },
              "logicalBytes": 4096
            }
          ]
        }
        """;
}
