using System.Collections.Immutable;
using DevDriveManager.ViewModels;
using DevDriveReclaim;

namespace DevDriveCore.Tests;

/// <summary>
/// The Reclaim room's two account-giving lines: how the scan went, and how the run went. Treated as
/// behaviour rather than copy for the same reason the confirmation is — they are the only account a
/// person gets of an operation that removed things, and the distinctions they draw survive a rewrite
/// by looking right while being wrong.
/// </summary>
[TestClass]
public sealed class ReclaimNarrationTests
{
    private static ReclaimCategory Category(string id, int order) =>
        new(id, id, "d", "\uE783", order);

    private static ReclaimCandidate Candidate(string categoryId, string path, long size) =>
        new(categoryId, path, Path.GetFileName(path), size, ReclaimRisk.Safe, "r", "h");

    private static ReclaimCategoryResult Found(string id, int order, long size) =>
        new(Category(id, order), [Candidate(id, $@"C:\{id}", size)]);

    private static ReclaimCategoryResult Cancelled(string id, int order) =>
        new(Category(id, order), [], "Cancelled before this finished", default, Cancelled: true);

    private static ReclaimCategoryResult Failed(string id, int order) =>
        new(Category(id, order), [], "the disk said no");

    private static ReclaimResult Result(params ReclaimCategoryResult[] categories) =>
        new(categories, DateTimeOffset.UtcNow);

    [TestMethod]
    public void ACleanScanJustStatesWhatItFound()
    {
        string line = ReclaimNarration.DescribeScan(Result(Found("a", 1, 1_000_000_000)));

        StringAssert.StartsWith(line, "Found ");
        Assert.DoesNotContain("floor", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancelled", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The headline case for the whole change: stopping a 497-second scan at 480 seconds must still
    /// report what the finished categories found, and must say the number is a floor. Silently
    /// presenting it as a total is how someone concludes there is nothing left to reclaim.
    /// </summary>
    [TestMethod]
    public void AStoppedScanKeepsItsTotalAndCallsItAFloor()
    {
        string line = ReclaimNarration.DescribeScan(
            Result(Found("a", 1, 1_000_000_000), Cancelled("b", 2)));

        StringAssert.StartsWith(line, "Cancelled.");
        StringAssert.Contains(line, "Found ");
        StringAssert.Contains(line, "1 category not reached");
        StringAssert.Contains(line, "floor");
    }

    /// <summary>
    /// A stop that arrived before any category finished has no total to report, so it must not
    /// announce "Found 0 B" — which reads as a fact about the disk rather than about the scan.
    /// </summary>
    [TestMethod]
    public void AScanStoppedBeforeAnythingFinishedSaysSoRatherThanReportingZero()
    {
        string line = ReclaimNarration.DescribeScan(Result(Cancelled("a", 1), Cancelled("b", 2)));

        Assert.AreEqual("Cancelled before anything finished.", line);
        Assert.DoesNotContain("Found", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelled and failed are different words because they are different events: one is the user's
    /// own doing and the other is the app hitting something it could not handle. Calling a Cancel a
    /// failure is both wrong and alarming; calling a failure a cancel hides a defect.
    /// </summary>
    [TestMethod]
    public void CancelledAndFailedCategoriesAreCountedSeparately()
    {
        string line = ReclaimNarration.DescribeScan(
            Result(Found("a", 1, 500), Cancelled("b", 2), Failed("c", 3)));

        StringAssert.Contains(line, "1 category not reached");
        StringAssert.Contains(line, "1 category could not be checked");
    }

    [TestMethod]
    public void AFailureWithoutACancelDoesNotClaimTheUserStoppedAnything()
    {
        string line = ReclaimNarration.DescribeScan(Result(Found("a", 1, 500), Failed("b", 2)));

        Assert.DoesNotContain("Cancelled", line, StringComparison.Ordinal);
        StringAssert.Contains(line, "could not be checked");
        StringAssert.Contains(line, "floor");
    }

    [TestMethod]
    [DataRow(1, "1 category")]
    [DataRow(2, "2 categories")]
    public void CategoryCountsAgreeWithTheirOwnNumber(int count, string expected)
    {
        ReclaimCategoryResult[] categories =
            [Found("kept", 0, 500), .. Enumerable.Range(1, count).Select(i => Cancelled($"c{i}", i))];

        StringAssert.Contains(ReclaimNarration.DescribeScan(Result(categories)), $"{expected} not reached");
    }

    private static ReclaimOutcome Outcome(
        int removed = 0, int failed = 0, long freed = 0, bool cancelled = false)
    {
        ImmutableArray<ReclaimItemOutcome> items =
        [
            .. Enumerable.Range(0, removed).Select(i => new ReclaimItemOutcome(
                Candidate("build-output", $@"C:\gone{i}", freed), ReclaimItemStatus.Deleted,
                i == 0 ? freed : 0, "removed")),
            .. Enumerable.Range(0, failed).Select(i => new ReclaimItemOutcome(
                Candidate("build-output", $@"C:\stuck{i}", 1), ReclaimItemStatus.Failed, 0, "locked")),
        ];

        return new ReclaimOutcome(items, cancelled);
    }

    /// <summary>
    /// The defect this replaced: a stopped run said "Nothing else was touched" unconditionally, which
    /// erased every real failure and contradicted the failure banner shown right beside it. An item
    /// that was reached and refused was very much touched.
    /// </summary>
    [TestMethod]
    public void AStoppedRunThatAlsoFailedDoesNotClaimNothingElseWasTouched()
    {
        string line = ReclaimNarration.SummarizeRun(
            Outcome(removed: 1, failed: 1, freed: 4096, cancelled: true));

        StringAssert.StartsWith(line, "Stopped after freeing");
        StringAssert.Contains(line, "1 item could not be removed and is still listed");
        Assert.DoesNotContain(
            "Nothing else was touched",
            line,
            StringComparison.Ordinal,
            "a run that failed on something touched it");
    }

    [TestMethod]
    public void AStoppedRunWithNoFailuresMaySayNothingElseWasTouched()
    {
        string line = ReclaimNarration.SummarizeRun(
            Outcome(removed: 2, freed: 4096, cancelled: true));

        StringAssert.Contains(line, "Nothing else was touched");
    }

    [TestMethod]
    public void ARunThatRemovedNothingAndFailedAtNothingSaysSoPlainly()
    {
        Assert.AreEqual("Nothing was removed.", ReclaimNarration.SummarizeRun(Outcome()));
    }

    [TestMethod]
    [DataRow(1, "1 item")]
    [DataRow(2, "2 items")]
    public void ItemCountsAgreeWithTheirOwnNumber(int failed, string expected)
    {
        string line = ReclaimNarration.SummarizeRun(Outcome(removed: 1, failed: failed, freed: 4096));

        StringAssert.Contains(line, $"{expected} could not be removed");
    }
}
