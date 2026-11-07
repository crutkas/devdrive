using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// A Recycle Bin is never Safe: emptying it is final, and it can hold what a Reclaim run just recycled under a promise that it
/// could be restored. Check by default, Careful while it holds a Careful item Reclaim recycled.
/// </summary>
[TestClass]
public sealed class RecycleBinGradingTests
{
    private static ReclaimCandidate Item(string path, ReclaimRisk risk) =>
        new("dormant-projects", path, Path.GetFileName(path), 4096, risk, "test", "test");

    [TestMethod]
    public void ABinIsCheckWhenReclaimPutNothingThere()
    {
        ReclaimCandidate bin = RecycleBinReclaimProvider.Candidate(@"C:\", 500_000_000, 3, []);

        Assert.AreEqual(ReclaimRisk.Check, bin.Risk);
        Assert.IsFalse(bin.SupportsRecycleBin);
        StringAssert.Contains(bin.Reason, "emptying the bin is final");
    }

    [TestMethod]
    public void ABinThatHoldsWhatReclaimRecycledAsSafeOrCheckIsCheck()
    {
        RecycledByReclaim recycled = new();
        recycled.Recycled(@"C:\", Item(@"C:\src\app\obj", ReclaimRisk.Safe));
        recycled.Recycled(@"C:\", Item(@"C:\src\old-tool", ReclaimRisk.Check));

        ReclaimCandidate bin = RecycleBinReclaimProvider.Candidate(@"C:\", 9_000, 2, recycled.In(@"C:\", _ => false));

        Assert.AreEqual(ReclaimRisk.Check, bin.Risk);
    }

    [TestMethod]
    public void ABinThatHoldsACarefulItemReclaimRecycledIsCareful()
    {
        RecycledByReclaim recycled = new();
        recycled.Recycled(@"C:\", Item(@"C:\src\app\obj", ReclaimRisk.Safe));
        recycled.Recycled(@"C:\", Item(@"C:\src\unfinished", ReclaimRisk.Careful));

        ReclaimCandidate bin = RecycleBinReclaimProvider.Candidate(@"C:\", 9_000, 2, recycled.In(@"C:\", _ => false));

        Assert.AreEqual(ReclaimRisk.Careful, bin.Risk);
        StringAssert.Contains(bin.Reason, "unfinished");
        StringAssert.Contains(bin.Reason, "deletes it for good");
    }

    [TestMethod]
    public void ABinThatHoldsSeveralCarefulItemsCountsThem()
    {
        RecycledByReclaim recycled = new();
        recycled.Recycled(@"D:\", Item(@"D:\src\one", ReclaimRisk.Careful));
        recycled.Recycled(@"D:\", Item(@"D:\src\two", ReclaimRisk.Careful));

        ReclaimCandidate bin = RecycleBinReclaimProvider.Candidate(@"D:\", 9_000, 2, recycled.In(@"D:\", _ => false));

        Assert.AreEqual(ReclaimRisk.Careful, bin.Risk);
        StringAssert.Contains(bin.Reason, "2 items");
    }

    [TestMethod]
    public void OnlyTheVolumeTheItemWentToIsCareful()
    {
        RecycledByReclaim recycled = new();
        recycled.Recycled(@"D:\", Item(@"D:\src\unfinished", ReclaimRisk.Careful));

        Assert.IsEmpty(recycled.In(@"C:\", _ => false));
        Assert.HasCount(1, recycled.In("D:", _ => false));
    }

    [TestMethod]
    public void AnItemRestoredFromTheBinDropsOut()
    {
        RecycledByReclaim recycled = new();
        recycled.Recycled(@"C:\", Item(@"C:\src\unfinished", ReclaimRisk.Careful));

        Assert.IsEmpty(recycled.In(@"C:\", path => path == @"C:\src\unfinished"));
        Assert.IsEmpty(recycled.In(@"C:\", _ => false));
    }

    [TestMethod]
    public void EmptyingABinForgetsWhatReclaimPutThere()
    {
        RecycledByReclaim recycled = new();
        recycled.Recycled(@"C:\", Item(@"C:\src\unfinished", ReclaimRisk.Careful));
        recycled.Recycled(@"D:\", Item(@"D:\src\other", ReclaimRisk.Careful));

        recycled.Emptied(@"C:\");

        Assert.IsEmpty(recycled.In(@"C:\", _ => false));
        Assert.HasCount(1, recycled.In(@"D:\", _ => false));
    }

    /// <summary>The case that made the bin Careful: a run recycles a Careful item, and the next scan grades the bin by it.</summary>
    [TestMethod]
    public async Task ARunThatRecyclesACarefulItemMakesTheNextScanCareful()
    {
        using ReclaimFixture fixture = new();
        string repo = fixture.Dir("unfinished-work");
        File.WriteAllText(Path.Combine(repo, "notes.txt"), "only copy");
        ReclaimCandidate careful = new("dormant-projects", repo, "unfinished-work", 1024, ReclaimRisk.Careful, "test", "test");
        RecycledByReclaim recycled = new();
        ReclaimExecutor executor = new(RecycleBinPreflight.ReadSettings, Providers.Models.LocalModelRemoval.Default,
            new GitWorktreeInspector(), recycled);

        ReclaimOutcome outcome = await executor.ExecuteAsync([careful], null, CancellationToken.None);

        try
        {
            Assert.AreEqual(ReclaimItemStatus.Recycled, outcome.Items[0].Status);
            string volume = Path.GetPathRoot(repo)!;
            IReadOnlyList<ReclaimCandidate> bins = await new RecycleBinReclaimProvider(recycled).ScanAsync(
                new ReclaimScanContext([volume], [], minimumCandidateBytes: 0), null, CancellationToken.None);

            ReclaimCandidate bin = bins.Single();
            Assert.AreEqual(ReclaimRisk.Careful, bin.Risk);
            StringAssert.Contains(bin.Reason, "unfinished-work");
        }
        finally
        {
            ReclaimRecycleTests.PurgeFromRecycleBin(Path.GetFileName(repo));
        }
    }
}
