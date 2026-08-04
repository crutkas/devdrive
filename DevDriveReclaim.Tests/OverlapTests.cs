using DevDriveReclaim;

namespace DevDriveReclaim.Tests;

/// <summary>
/// Guards the arithmetic. A reclaim tool's headline number is a promise about how much room the
/// machine will have afterwards, so a total that counts the same bytes twice is not a rounding
/// problem — it is the tool lying about the only thing it exists to tell you.
/// </summary>
[TestClass]
public sealed class OverlapTests
{
    private static ReclaimCandidate Candidate(
        string path, long size, string category = "test", ReclaimRisk risk = ReclaimRisk.Safe) =>
        new(category, path, Path.GetFileName(path.TrimEnd('\\')), size, risk,
            "reason", "recovery hint");

    [TestMethod]
    public void NestedCandidateIsNotCountedTwice()
    {
        // The exact shape found on the real machine: a worktree row and a build-output row for the
        // obj folder inside that same worktree. Naive summing reported 72.74 GB of a possible 41.49.
        ReclaimCandidate worktree = Candidate(@"C:\src\crutkas-solid-winner", 41_490_000_000);
        ReclaimCandidate obj = Candidate(@"C:\src\crutkas-solid-winner\obj", 31_250_000_000, "build-outputs");

        long total = ReclaimOverlapResolver.ReclaimableBytes([worktree, obj]);

        Assert.AreEqual(41_490_000_000, total,
            "Deleting the worktree also deletes its obj folder, so the machine gets back the " +
            "worktree's size — not the worktree plus the obj.");
    }

    [TestMethod]
    public void SiblingsAreBothCounted()
    {
        ReclaimCandidate a = Candidate(@"C:\src\alpha", 100);
        ReclaimCandidate b = Candidate(@"C:\src\beta", 200);

        Assert.AreEqual(300, ReclaimOverlapResolver.ReclaimableBytes([a, b]));
    }

    [TestMethod]
    public void PrefixMatchIsNotTreatedAsContainment()
    {
        // C:\src\app does not contain C:\src\app-tests. Without a separator-aware check this is a
        // silent under-count, which is the more dangerous direction: the tool quietly under-promises
        // and the missing folder never gets offered.
        ReclaimCandidate app = Candidate(@"C:\src\app", 100);
        ReclaimCandidate tests = Candidate(@"C:\src\app-tests", 200);

        Assert.AreEqual(300, ReclaimOverlapResolver.ReclaimableBytes([app, tests]));
    }

    [TestMethod]
    public void DeeplyNestedCollapsesToTheOutermost()
    {
        ReclaimCandidate root = Candidate(@"C:\src\repo", 500);
        ReclaimCandidate mid = Candidate(@"C:\src\repo\src", 300);
        ReclaimCandidate leaf = Candidate(@"C:\src\repo\src\obj", 100);

        Assert.AreEqual(500, ReclaimOverlapResolver.ReclaimableBytes([leaf, mid, root]),
            "Order of discovery must not change the answer.");
    }

    [TestMethod]
    public void ContainmentIsCaseInsensitiveLikeWindows()
    {
        ReclaimCandidate parent = Candidate(@"C:\Src\Repo", 500);
        ReclaimCandidate child = Candidate(@"c:\src\repo\obj", 100);

        Assert.AreEqual(500, ReclaimOverlapResolver.ReclaimableBytes([parent, child]));
    }

    [TestMethod]
    public void SelectingOnlyTheChildStillCountsTheChild()
    {
        // Deleting just the obj folder and keeping the branch is a legitimate thing to want, so the
        // child row must keep its full value when its container is not selected.
        ReclaimCandidate obj = Candidate(@"C:\src\repo\obj", 100);

        Assert.AreEqual(100, ReclaimOverlapResolver.ReclaimableBytes([obj]));
    }

    [TestMethod]
    public void PerVolumeTotalsResolveNestingToo()
    {
        ReclaimCandidate worktree = Candidate(@"G:\src\repo", 500);
        ReclaimCandidate obj = Candidate(@"G:\src\repo\obj", 300);
        ReclaimCandidate onC = Candidate(@"C:\cache\pkg", 200);

        IReadOnlyDictionary<string, long> byVolume =
            ReclaimOverlapResolver.ReclaimableBytesByVolume([worktree, obj, onC]);

        Assert.AreEqual(500, byVolume[@"G:\"]);
        Assert.AreEqual(200, byVolume[@"C:\"]);
    }

    [TestMethod]
    public void ContainerLookupNamesTheRowThatAbsorbsIt()
    {
        ReclaimCandidate worktree = Candidate(@"C:\src\repo", 500);
        ReclaimCandidate obj = Candidate(@"C:\src\repo\obj", 300);

        IReadOnlyDictionary<string, string> map =
            ReclaimOverlapResolver.ContainerByPath([worktree, obj]);

        Assert.AreEqual(@"C:\src\repo", map[@"C:\src\repo\obj"],
            "A row contributing nothing to the total has to be able to say why.");
        Assert.IsFalse(map.ContainsKey(@"C:\src\repo"));
    }

    [TestMethod]
    public void ResultTotalUsesResolvedBytesAndExposesTheGap()
    {
        var worktrees = new ReclaimCategory("worktrees", "Worktrees", "desc", "\uE8B7", 1);
        var builds = new ReclaimCategory("build-outputs", "Build outputs", "desc", "\uE8B7", 2);

        var result = new ReclaimResult(
            [
                new ReclaimCategoryResult(worktrees, [Candidate(@"C:\src\repo", 500)]),
                new ReclaimCategoryResult(builds, [Candidate(@"C:\src\repo\obj", 300, "build-outputs")]),
            ],
            DateTimeOffset.UtcNow);

        Assert.AreEqual(500, result.TotalBytes, "The number a user reads must be reclaimable.");
        Assert.AreEqual(800, result.UncollapsedTotalBytes, "The naive sum stays available to compare against.");
    }

    [TestMethod]
    public void RiskTiersAreEachCorrectButAreNotAdditive()
    {
        // A real scan produced Safe 441.94 + Check 258.77 + Careful 100.97 = 801.68 GB against a
        // 493.57 GB total, because Safe build-output rows sit inside Check worktree rows. Each tier
        // is individually right — selecting exactly that tier really does return that much — but any
        // UI that adds the three tiles together reintroduces the double-count this class removed.
        // The only correct way to total a mixed selection is to put the selection through the
        // resolver, which is what ReclaimResult.BytesByVolume does.
        ReclaimCandidate worktree = Candidate(@"C:\src\repo", 500, risk: ReclaimRisk.Check);
        ReclaimCandidate obj = Candidate(@"C:\src\repo\obj", 300, "build-outputs");

        var result = new ReclaimResult(
            [
                new ReclaimCategoryResult(
                    new ReclaimCategory("mixed", "Mixed", "desc", "\uE8B7", 1), [worktree, obj]),
            ],
            DateTimeOffset.UtcNow);

        Assert.AreEqual(300, result.BytesFor(ReclaimRisk.Safe), "Safe alone deletes just the obj.");
        Assert.AreEqual(500, result.BytesFor(ReclaimRisk.Check), "Check alone deletes the whole worktree.");
        Assert.AreEqual(800, result.BytesFor(ReclaimRisk.Safe) + result.BytesFor(ReclaimRisk.Check),
            "Adding the tiers overstates — this is the trap being documented, not the answer.");

        Assert.AreEqual(500, ReclaimOverlapResolver.ReclaimableBytes([worktree, obj]),
            "Selecting both must return the worktree's size, which is the only true answer.");
        Assert.AreEqual(500, ReclaimResult.BytesByVolume([worktree, obj])[@"C:\"]);
    }
}
