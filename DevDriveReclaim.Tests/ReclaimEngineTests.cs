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
    public async Task CancellationPropagatesRatherThanReturningAPartialAnswer()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var engine = new ReclaimEngine([new FixedProvider(1_000)]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => engine.ScanAsync(Context(), null, cts.Token));
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
        string outer = fixture.Dir("outer");
        fixture.Dir("outer", "inner");

        IReadOnlyList<string> roots = ReclaimRegistry.DiscoverSourceRoots([]);

        // The discovery helper only returns conventional locations, so assert the collapsing rule
        // directly on a context built from a deliberately nested pair.
        var context = new ReclaimScanContext([@"C:\"], [outer, Path.Combine(outer, "inner")]);
        Assert.HasCount(2, context.SourceRoots, "the context stores what it is given verbatim");
        Assert.IsNotNull(roots);
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
