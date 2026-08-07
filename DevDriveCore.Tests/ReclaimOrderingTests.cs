using DevDriveManager.Services;
using DevDriveReclaim;

namespace DevDriveCore.Tests;

/// <summary>
/// Ordering and reporting rules that decide whether a run tells the truth, asserted through the safe
/// fake because both are decisions about sequence and bookkeeping rather than about the shell.
/// </summary>
/// <remarks>
/// The fake records what it was asked to remove, in order, and shares the real executor's ordering
/// and absorbed-reporting logic. That makes it the only place these can be checked without emptying
/// the developer's actual Recycle Bin, which is a thing no test may do.
/// </remarks>
[TestClass]
public sealed class ReclaimOrderingTests
{
    private static ReclaimCandidate Bin(string volumeRoot) =>
        new(
            "recycle-bin",
            volumeRoot,
            $"Recycle Bin on {volumeRoot.TrimEnd('\\')}",
            5_000_000,
            ReclaimRisk.Safe,
            "test",
            "test",
            supportsRecycleBin: false);

    private static ReclaimCandidate Folder(string path, long size = 1_000_000) =>
        new(
            "build-output",
            path,
            Path.GetFileName(path.TrimEnd('\\')),
            size,
            ReclaimRisk.Safe,
            "test",
            "test",
            supportsRecycleBin: true);

    /// <summary>
    /// The one that mattered most. A bin candidate's path IS the volume root, so ordering by depth
    /// alone made it the shortest path in any selection and therefore the last thing processed —
    /// after every other item had been recycled into that same bin. Emptying then destroyed them
    /// permanently, while the confirmation had just promised they could be restored, and the receipt
    /// reported full success.
    /// <para>
    /// Both are graded Safe and both are ticked by the automatic "select safe" after every scan, so
    /// this was the default path through the feature, not a corner of it.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TheRecycleBinIsEmptiedBeforeAnythingIsRecycledIntoIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-order-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "obj");
        Directory.CreateDirectory(folder);
        string volumeRoot = Path.GetPathRoot(folder)!;
        try
        {
            SafeFakeReclaimExecutor executor = new();

            await executor.ExecuteAsync([Folder(folder), Bin(volumeRoot)], null, default);

            Assert.HasCount(2, executor.Removed);
            Assert.AreEqual(
                volumeRoot,
                executor.Removed[0],
                "The bin must be emptied first, or everything recycled into it this run is destroyed.");
            Assert.AreEqual(folder, executor.Removed[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Depth ordering still holds among ordinary items, so a run cancelled halfway has removed leaves
    /// rather than trunks.
    /// </summary>
    [TestMethod]
    public async Task DeeperItemsStillGoFirstAmongOrdinaryCandidates()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-order-" + Guid.NewGuid().ToString("N"));
        string shallow = Path.Combine(root, "a");
        string deep = Path.Combine(root, "b", "c", "d");
        Directory.CreateDirectory(shallow);
        Directory.CreateDirectory(deep);
        try
        {
            SafeFakeReclaimExecutor executor = new();

            await executor.ExecuteAsync([Folder(shallow), Folder(deep)], null, default);

            Assert.AreEqual(deep, executor.Removed[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A child inherits its container's fate. Reporting "absorbed" for every non-root regardless of
    /// what happened to the root turned a guard refusal into a reported success — and, because the
    /// ViewModel drops every row it is told was removed, made the row vanish from the table while the
    /// folder was still on disk, unreachable again without a full rescan.
    /// </summary>
    [TestMethod]
    public async Task AChildOfARefusedContainerIsNotReportedAsRemoved()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string child = Path.Combine(windows, "Temp");

        SafeFakeReclaimExecutor executor = new();

        ReclaimOutcome outcome = await executor.ExecuteAsync(
            [Folder(windows), Folder(child)], null, default);

        Assert.AreEqual(0, outcome.RemovedCount, "Nothing was removed, so nothing may be reported removed.");
        Assert.AreEqual(2, outcome.FailedCount, "Both the refused container and its child need attention.");
        Assert.IsEmpty(executor.Removed);
        Assert.IsTrue(Directory.Exists(windows));
    }

    /// <summary>
    /// The success case still reports absorption, so a nested selection is counted once rather than
    /// dropped or double counted.
    /// </summary>
    [TestMethod]
    public async Task AChildOfARemovedContainerIsStillReportedAbsorbed()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-order-" + Guid.NewGuid().ToString("N"));
        string outer = Path.Combine(root, "repo");
        string inner = Path.Combine(outer, "bin");
        Directory.CreateDirectory(inner);
        try
        {
            SafeFakeReclaimExecutor executor = new();

            ReclaimOutcome outcome = await executor.ExecuteAsync(
                [Folder(outer), Folder(inner)], null, default);

            Assert.AreEqual(2, outcome.RemovedCount);
            Assert.AreEqual(
                ReclaimItemStatus.Absorbed,
                outcome.Items.Single(i => i.Candidate.Path == inner).Status);
            Assert.AreEqual(1_000_000, outcome.BytesFreed, "The container's bytes count once.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
