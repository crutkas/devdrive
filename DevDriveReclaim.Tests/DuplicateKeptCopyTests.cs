using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// A duplicate is only safe to remove while the copy kept in its place stays (Dev Config #347). The kept copy was the shortest
/// path wherever it was: inside <c>$Recycle.Bin</c>, or inside another row such as a build output, so removing what was
/// offered, or both rows, lost every copy.
/// </summary>
/// <remarks>
/// Every removal here is permanent (<see cref="ReclaimCandidate.WithoutRecycleBin"/>) and inside a <see cref="ReclaimFixture"/>;
/// the <c>$Recycle.Bin</c> here is a stand-in folder in the fixture, not the real one.
/// </remarks>
[TestClass]
public sealed class DuplicateKeptCopyTests
{
    private const int Mb = 1024 * 1024;

    private static byte[] Payload()
    {
        byte[] bytes = new byte[5 * Mb];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static ReclaimScanContext Context(ReclaimFixture fixture) =>
        new([Path.GetPathRoot(fixture.Root)!], [fixture.Root], minimumCandidateBytes: 1);

    private static async Task<IReadOnlyList<ReclaimCandidate>> Duplicates(ReclaimFixture fixture) =>
        await new DuplicateFileReclaimProvider().ScanAsync(Context(fixture), null, CancellationToken.None);

    private static ReclaimCandidate Duplicate(string path, string kept) =>
        new("duplicates", path, Path.GetFileName(path), 5 * Mb, ReclaimRisk.Check, "identical", $"Copy it back from {kept}")
        {
            KeptCopy = kept,
        };

    private static ReclaimCandidate Folder(string path, string category = "build-outputs") =>
        new(category, path, Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path, 5 * Mb, ReclaimRisk.Safe,
            "generated", "rebuild");

    private static Task<ReclaimOutcome> Run(params ReclaimCandidate[] selection) =>
        new ReclaimExecutor().ExecuteAsync([.. selection.Select(c => c.WithoutRecycleBin())], null, CancellationToken.None);

    [TestMethod]
    public async Task AFileInTheRecycleBinIsNeverTheKeptCopy()
    {
        using var fixture = new ReclaimFixture();
        byte[] data = Payload();

        // A data drive's root is a source root as a whole, and the recycled copy's path is the shorter one.
        fixture.FileWithContent(@"$Recycle.Bin\S-1-5-21-1-2-3-1001\$RX1Y2Z3.iso", data);
        fixture.FileWithContent(@"Users\me\Downloads\installers\ubuntu-24.04.1-desktop-amd64\ubuntu.iso", data);

        Assert.IsEmpty(await Duplicates(fixture), "the only copy that stays when the bin is emptied was offered");
    }

    [TestMethod]
    public async Task NothingInASystemFolderIsKeptOrOffered()
    {
        using var fixture = new ReclaimFixture();
        byte[] data = Payload();
        fixture.FileWithContent(@"$Recycle.Bin\S-1\$R1.bin", data);
        fixture.FileWithContent(@"System Volume Information\sv.bin", data);
        string first = fixture.FileWithContent(@"work\one\copy.bin", data);
        string second = fixture.FileWithContent(@"work\two\copy.bin", data);

        ReclaimCandidate row = (await Duplicates(fixture)).Single();

        CollectionAssert.AreEquivalent(new[] { first, second }, new[] { row.Path, row.KeptCopy! });
        StringAssert.Contains(row.RecoveryHint, row.KeptCopy!);
    }

    [TestMethod]
    public async Task ADuplicateWhoseKeptCopyIsInsideABuildOutputIsNotOffered()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("proj");
        byte[] data = Payload();
        string asset = fixture.FileWithContent(@"proj\assets\models\model.onnx", data);
        string output = fixture.FileWithContent(@"proj\bin\model.onnx", data);

        // On its own, the provider keeps the shorter path, the build's copy, and offers the asset.
        ReclaimCandidate offered = (await Duplicates(fixture)).Single();
        Assert.AreEqual(asset, offered.Path);
        Assert.AreEqual(output, offered.KeptCopy);

        ReclaimResult result = await new ReclaimEngine([new BuildOutputReclaimProvider(), new DuplicateFileReclaimProvider()])
            .ScanAsync(Context(fixture), null, CancellationToken.None);

        Assert.AreEqual(Path.GetDirectoryName(output), result.AllCandidates.Single().Path, "only the build output is offered");
    }

    [TestMethod]
    public void OnlyRowsKeptInsideAnotherRowAreDropped()
    {
        ReclaimCandidate inBin = Duplicate(@"C:\src\app\assets\a.bin", @"C:\src\app\bin\a.bin");
        ReclaimCandidate inProject = Duplicate(@"C:\src\new\b.bin", @"C:\src\old-project\b.bin");
        ReclaimCandidate isAModel = Duplicate(@"C:\src\c.gguf", @"C:\models\c.gguf");
        ReclaimCandidate elsewhere = Duplicate(@"C:\src\d.bin", @"C:\keep\d.bin");
        ReclaimCandidate bin = Folder(@"C:\src\app\bin");
        ReclaimCandidate recycleBin = Folder(@"C:\", "recycle-bin");

        ReclaimCategoryResult[] results =
        [
            new(BuildOutputReclaimProvider.ReclaimCategory, [bin]),
            new(DormantProjectReclaimProvider.ReclaimCategory, [Folder(@"C:\src\old-project", "dormant-projects")]),
            new(Providers.Models.LocalModelReclaimProvider.ReclaimCategory, [Folder(@"C:\models\c.gguf", "ai-models")]),
            new(RecycleBinReclaimProvider.ReclaimCategory, [recycleBin]),
            new(DuplicateFileReclaimProvider.ReclaimCategory, [inBin, inProject, isAModel, elsewhere]),
        ];

        ReclaimCategoryResult duplicates = ReclaimEngine.WithoutCopiesKeptInOtherRows(results)
            .Single(r => r.Category == DuplicateFileReclaimProvider.ReclaimCategory);

        CollectionAssert.AreEqual(new[] { elsewhere }, duplicates.Candidates.ToArray(), "a volume root (a bin) contains nothing");
    }

    [TestMethod]
    public async Task TheRunKeepsADuplicateWhoseKeptCopyGoesInTheSameRun()
    {
        using var fixture = new ReclaimFixture();
        byte[] data = Payload();
        string asset = fixture.FileWithContent(@"proj\assets\models\model.onnx", data);
        string kept = fixture.FileWithContent(@"proj\bin\model.onnx", data);
        string bin = Path.GetDirectoryName(kept)!;

        ReclaimOutcome outcome = await Run(Folder(bin), Duplicate(asset, kept));

        ReclaimItemOutcome duplicate = outcome.Items.Single(i => i.Candidate.Path == asset);
        Assert.IsTrue(File.Exists(asset), "every copy went");
        Assert.AreEqual(ReclaimItemStatus.Failed, duplicate.Status);
        Assert.AreEqual(ReclaimChange.KeptCopyRemovedToo, duplicate.Change);
        StringAssert.StartsWith(duplicate.Message, $"kept — the copy kept in its place, {kept}, is being removed in this run too");
        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single(i => i.Candidate.Path == bin).Status);
    }

    [TestMethod]
    public async Task TheRunKeepsADuplicateWhoseKeptCopyIsGoneOrChanged()
    {
        using var fixture = new ReclaimFixture();
        byte[] data = Payload();
        string gone = fixture.FileWithContent(@"a\copy.bin", data);
        string goneKept = fixture.FileWithContent(@"a\kept.bin", data);
        string changed = fixture.FileWithContent(@"b\copy.bin", data);
        string changedKept = fixture.FileWithContent(@"b\kept.bin", data);
        File.Delete(goneKept);
        File.AppendAllText(changedKept, "edited after the scan");

        ReclaimOutcome outcome = await Run(Duplicate(gone, goneKept), Duplicate(changed, changedKept));

        Assert.IsTrue(File.Exists(gone) && File.Exists(changed), "a copy that may be the last one was removed");
        Assert.AreEqual(ReclaimChange.KeptCopyGone, outcome.Items.Single(i => i.Candidate.Path == gone).Change);
        Assert.AreEqual(ReclaimChange.KeptCopyChanged, outcome.Items.Single(i => i.Candidate.Path == changed).Change);
        Assert.IsTrue(outcome.Items.All(i => i.Message.StartsWith("kept — changed since the scan: ", StringComparison.Ordinal)));
        Assert.AreEqual(0, outcome.BytesFreed);
    }

    [TestMethod]
    public async Task ADuplicateWhoseKeptCopyStaysIsRemoved()
    {
        using var fixture = new ReclaimFixture();
        byte[] data = Payload();
        string copy = fixture.FileWithContent(@"a\copy.bin", data);
        string kept = fixture.FileWithContent(@"b\kept.bin", data);

        ReclaimOutcome outcome = await Run(Duplicate(copy, kept), Folder(fixture.Dir("unrelated")));

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single(i => i.Candidate.Path == copy).Status);
        Assert.IsFalse(File.Exists(copy));
        Assert.IsTrue(File.Exists(kept));
    }

    [TestMethod]
    public void ConsentingToAPermanentDeleteKeepsTheKeptCopy()
    {
        ReclaimCandidate row = Duplicate(@"C:\a\copy.bin", @"C:\b\kept.bin");

        Assert.AreEqual(@"C:\b\kept.bin", row.WithoutRecycleBin().KeptCopy);
    }
}
