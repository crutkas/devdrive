using System.Diagnostics;
using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

[TestClass]
public sealed class ReclaimEngineTests
{
    [TestMethod]
    public async Task AFailingProviderDegradesItsCategoryNotTheScan()
    {
        var engine = new ReclaimEngine([new ThrowingProvider(), new FixedProvider(1_000)]);

        ReclaimResult result = await engine.ScanAsync(Context(), null, CancellationToken.None);

        Assert.HasCount(2, result.Categories);
        Assert.HasCount(1, result.FailedCategories);
        Assert.AreEqual(1_000, result.TotalBytes, "the healthy category still contributed its bytes");
    }

    [TestMethod]
    public async Task CandidatesBelowTheMinimumAreDropped()
    {
        var engine = new ReclaimEngine([new FixedProvider(10)]);

        ReclaimResult result = await engine.ScanAsync(
            new ReclaimScanContext([@"C:\"], [], minimumCandidateBytes: 1_000),
            null, CancellationToken.None);

        Assert.IsEmpty(result.AllCandidates);
    }

    [TestMethod]
    public async Task ResultsAreCappedPerCategory()
    {
        var engine = new ReclaimEngine([new ManyProvider(50)]);

        ReclaimResult result = await engine.ScanAsync(
            new ReclaimScanContext([@"C:\"], [], minimumCandidateBytes: 1, maximumCandidatesPerCategory: 10),
            null, CancellationToken.None);

        Assert.HasCount(10, result.AllCandidates);
        // Capping keeps the largest, because a truncated list that dropped the biggest rows would
        // understate the reclaimable total in the most misleading way possible.
        Assert.AreEqual(50, result.AllCandidates[0].SizeBytes);
    }

    [TestMethod]
    public async Task CancellingBeforeAnythingStartsReportsEveryCategoryUnreached()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var engine = new ReclaimEngine([new FixedProvider(1_000)]);

        ReclaimResult result = await engine.ScanAsync(Context(), null, cts.Token);

        // Not an exception. A cancel is an answer about what was checked, and an exception erases
        // that answer — which matters far more mid-scan than here, but the contract has to be one
        // contract or the room needs two code paths for the same gesture.
        Assert.IsFalse(result.Complete, "A cancelled scan must never claim to be complete.");
        Assert.HasCount(1, result.CancelledCategories);
        Assert.IsEmpty(result.AllCandidates);
        Assert.AreEqual(0, result.TotalBytes);
    }

    [TestMethod]
    public async Task CancellingMidScanKeepsTheCategoriesThatFinished()
    {
        using var cts = new CancellationTokenSource();

        // Finishes, then cancels. The slow provider is therefore guaranteed to observe the cancel,
        // and the fast one is guaranteed to have already produced its answer when it does.
        var fast = new SignallingProvider(500, cts);
        var slow = new BlockingProvider();

        var engine = new ReclaimEngine([fast, slow]);

        ReclaimResult result = await engine.ScanAsync(Context(), null, cts.Token);

        // The whole point: 497 s cold, and stopping at 480 s used to return nothing at all.
        Assert.AreEqual(500, result.TotalBytes, "The finished category's bytes were thrown away.");
        Assert.HasCount(1, result.AllCandidates);
        Assert.HasCount(1, result.CancelledCategories);
        Assert.AreEqual("blocking", result.CancelledCategories[0].Category.Id);
        Assert.IsFalse(result.Complete, "A total that is missing a category is a floor, not a fact.");
    }

    [TestMethod]
    public void BytesAreProjectedPerVolume()
    {
        ReclaimCandidate[] selection =
        [
            new("c", @"C:\a", "a", 100, ReclaimRisk.Safe, "r", "h"),
            new("c", @"C:\b", "b", 200, ReclaimRisk.Safe, "r", "h"),
            new("c", @"G:\c", "c", 700, ReclaimRisk.Safe, "r", "h"),
        ];

        IReadOnlyDictionary<string, long> byVolume = ReclaimResult.BytesByVolume(selection);

        Assert.AreEqual(300, byVolume[@"C:\"]);
        Assert.AreEqual(700, byVolume[@"G:\"]);
    }

    [TestMethod]
    public void RiskRollupsCountBytesAndItems()
    {
        var result = new ReclaimResult(
        [
            new ReclaimCategoryResult(BuildOutputReclaimProvider.ReclaimCategory,
            [
                new("c", @"C:\a", "a", 100, ReclaimRisk.Safe, "r", "h"),
                new("c", @"C:\b", "b", 200, ReclaimRisk.Safe, "r", "h"),
                new("c", @"C:\c", "c", 400, ReclaimRisk.Careful, "r", "h"),
            ]),
        ], DateTimeOffset.UtcNow);

        Assert.AreEqual(300, result.BytesFor(ReclaimRisk.Safe));
        Assert.AreEqual(2, result.CountFor(ReclaimRisk.Safe));
        Assert.AreEqual(400, result.BytesFor(ReclaimRisk.Careful));
        Assert.AreEqual(700, result.TotalBytes);
    }

    [TestMethod]
    public void TheDefaultRegistryExposesEveryBuiltInCategoryInRailOrder()
    {
        ReclaimEngine engine = ReclaimRegistry.CreateDefaultEngine();

        var ids = engine.Categories.Select(c => c.Id).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "recycle-bin", "build-outputs", "package-caches",
                "worktrees", "dormant-projects", "duplicates",
            },
            ids);
    }

    [TestMethod]
    public void NestedSourceRootsAreCollapsedSoBytesAreNotCountedTwice()
    {
        using var fixture = new ReclaimFixture();
        string lot = fixture.Dir("lot");
        string outer = fixture.Dir("lot", "source");
        string inner = fixture.Dir("lot", "source", "src");

        // DiscoverSourceRoots probes a fixed set of conventional folder names under every volume root
        // it is given, so feeding it BOTH "lot" and "lot\source" makes it generate a genuinely nested
        // pair: "lot" yields "lot\source", and "lot\source" yields "lot\source\src". That is the only
        // way to drive the collapsing branch, which is why the previous version of this test — which
        // asserted that a hand-built context "stores what it is given verbatim", the opposite of its
        // own name, plus IsNotNull on a list that is never null — could not fail and never once
        // executed the rule it was named after.
        IReadOnlyList<string> roots = ReclaimRegistry.DiscoverSourceRoots([lot, outer]);

        Assert.IsTrue(
            roots.Contains(outer, StringComparer.OrdinalIgnoreCase),
            $"the outermost source root should survive; got [{string.Join(", ", roots)}]");
        Assert.IsFalse(
            roots.Contains(inner, StringComparer.OrdinalIgnoreCase),
            "a source root nested inside another would count the same bytes twice");

        // And the rule in general, which also covers whatever conventional folders happen to exist on
        // the machine running this: nothing that survives may sit inside anything else that survived.
        foreach (string a in roots)
        {
            foreach (string b in roots)
            {
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Assert.IsFalse(
                    a.StartsWith(b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    $"'{a}' is nested inside '{b}', so the headline byte total would double-count it.");
            }
        }
    }

    private static ReclaimScanContext Context() =>
        new([@"C:\"], [], minimumCandidateBytes: 1);

    private sealed class ThrowingProvider : IReclaimProvider
    {
        public ReclaimCategory Category { get; } = new("boom", "Boom", "d", "\uE783", 99);

        public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
            ReclaimScanContext context, IProgress<ReclaimScanProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new IOException("the disk said no");
    }

    private sealed class FixedProvider(long size) : IReclaimProvider
    {
        public ReclaimCategory Category { get; } = new("fixed", "Fixed", "d", "\uE783", 1);

        public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
            ReclaimScanContext context, IProgress<ReclaimScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ReclaimCandidate>>(
                [new("fixed", @"C:\x", "x", size, ReclaimRisk.Safe, "r", "h")]);
        }
    }

    /// <summary>Answers, then trips the cancel — so a later provider is certain to see it.</summary>
    private sealed class SignallingProvider(long size, CancellationTokenSource cts) : IReclaimProvider
    {
        public ReclaimCategory Category { get; } = new("signalling", "Signalling", "d", "\uE783", 1);

        public async Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
            ReclaimScanContext context, IProgress<ReclaimScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            var found = new ReclaimCandidate[]
            {
                new("signalling", @"C:\x", "x", size, ReclaimRisk.Safe, "r", "h"),
            };

            await cts.CancelAsync();
            return found;
        }
    }

    /// <summary>Never finishes on its own; only cancellation ends it.</summary>
    private sealed class BlockingProvider : IReclaimProvider
    {
        public ReclaimCategory Category { get; } = new("blocking", "Blocking", "d", "\uE783", 2);

        public async Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
            ReclaimScanContext context, IProgress<ReclaimScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class ManyProvider(int count) : IReclaimProvider
    {
        public ReclaimCategory Category { get; } = new("many", "Many", "d", "\uE783", 1);

        public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
            ReclaimScanContext context, IProgress<ReclaimScanProgress>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReclaimCandidate>>(
            [
                .. Enumerable.Range(1, count).Select(i =>
                    new ReclaimCandidate("many", $@"C:\x{i}", $"x{i}", i, ReclaimRisk.Safe, "r", "h"))
            ]);
    }
}
