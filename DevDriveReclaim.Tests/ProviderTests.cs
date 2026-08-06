using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

[TestClass]
public sealed class ProviderTests
{
    [TestMethod]
    public async Task BuildOutputsAreFoundAndAttributedToTheirRepository()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("MyApp");
        fixture.File(@"MyApp\src\bin\out.dll", 4L * 1024 * 1024);
        fixture.File(@"MyApp\src\obj\temp.o", 2L * 1024 * 1024);
        fixture.File(@"MyApp\src\Program.cs", 512);

        var provider = new BuildOutputReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
            Context(fixture), null, CancellationToken.None);

        Assert.HasCount(2, found);
        Assert.IsTrue(found.All(c => c.Risk == ReclaimRisk.Safe));
        Assert.IsTrue(found.All(c => c.DisplayName.StartsWith("MyApp", StringComparison.Ordinal)));
        Assert.IsTrue(found.All(c => c.RecoveryHint.Length > 0));
    }

    [TestMethod]
    public async Task NestedOutputInsideAnOutputFolderIsNotCountedTwice()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("Web");
        fixture.File(@"Web\node_modules\pkg\dist\bundle.js", 4L * 1024 * 1024);

        var provider = new BuildOutputReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
            Context(fixture), null, CancellationToken.None);

        // node_modules is reported; the dist inside it is already part of that measurement.
        Assert.HasCount(1, found);
        Assert.EndsWith("node_modules", found[0].Path, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task SourceFilesAreNeverReportedAsBuildOutput()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("Lib");
        fixture.File(@"Lib\src\Big.cs", 8L * 1024 * 1024);

        var provider = new BuildOutputReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
            Context(fixture), null, CancellationToken.None);

        Assert.IsEmpty(found);
    }

    [TestMethod]
    public async Task DuplicatesAreConfirmedByContentNotByName()
    {
        using var fixture = new ReclaimFixture();
        byte[] payload = new byte[6 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        byte[] different = new byte[6 * 1024 * 1024];
        Random.Shared.NextBytes(different);

        fixture.FileWithContent(@"a\shared.bin", payload);
        fixture.FileWithContent(@"b\shared.bin", payload);
        // Same name, same length, different bytes: a name+size heuristic would wrongly offer this.
        fixture.FileWithContent(@"c\shared.bin", different);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
            Context(fixture), null, CancellationToken.None);

        Assert.HasCount(1, found, "only the genuine byte-identical copy is offered");
        Assert.AreEqual(ReclaimRisk.Check, found[0].Risk);
    }

    [TestMethod]
    public async Task OneCopyOfADuplicateSetIsAlwaysKept()
    {
        using var fixture = new ReclaimFixture();
        byte[] payload = new byte[6 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        fixture.FileWithContent(@"one\f.bin", payload);
        fixture.FileWithContent(@"two\f.bin", payload);
        fixture.FileWithContent(@"three\f.bin", payload);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
            Context(fixture), null, CancellationToken.None);

        Assert.HasCount(2, found, "three copies means two are offered and one survives");

        // The surviving copy is named in the way back, not in the sub-line: every offered row shares
        // that one sentence, whereas the sub-line has to say which copy this row is.
        Assert.IsTrue(found.All(c => c.RecoveryHint.Contains("Copy it back from", StringComparison.Ordinal)));
        Assert.IsTrue(found.All(c => c.Reason.Contains("One copy is being kept", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SmallFilesAreNotConsideredForDuplication()
    {
        using var fixture = new ReclaimFixture();
        byte[] payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        fixture.FileWithContent(@"a\tiny.bin", payload);
        fixture.FileWithContent(@"b\tiny.bin", payload);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
            Context(fixture), null, CancellationToken.None);

        Assert.IsEmpty(found);
    }

    [TestMethod]
    public void RepositoriesAndWorktreesAreToldApart()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("main-repo");
        fixture.Worktree("side-tree", @"C:\main-repo\.git\worktrees\side");

        IReadOnlyList<DiscoveredRepository> found =
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None);

        Assert.HasCount(2, found);
        Assert.HasCount(1, found.Where(r => r.IsWorktree));
        Assert.AreEqual("side-tree", found.Single(r => r.IsWorktree).Name);
    }

    [TestMethod]
    public void TheWalkerDoesNotDescendIntoARepository()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("outer");
        // A .git deeper inside an existing repo is a submodule, not a separate clone to offer.
        Directory.CreateDirectory(Path.Combine(fixture.Root, "outer", "vendor", "inner", ".git"));

        IReadOnlyList<DiscoveredRepository> found =
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None);

        Assert.HasCount(1, found);
        Assert.AreEqual("outer", found[0].Name);
    }

    [TestMethod]
    public async Task PackageCachesAreMeasuredWhereTheEnvironmentVariablePointsThem()
    {
        using var fixture = new ReclaimFixture();
        string moved = fixture.Dir("moved-nuget");
        fixture.File(@"moved-nuget\pkg\a.nupkg", 4L * 1024 * 1024);

        string? original = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", moved);

            var provider = new PackageCacheReclaimProvider();
            IReadOnlyList<ReclaimCandidate> found = await provider.ScanAsync(
                Context(fixture), null, CancellationToken.None);

            ReclaimCandidate? nuget = found.FirstOrDefault(
                c => c.DisplayName.Contains("NuGet", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(nuget, "the relocated cache should be found at its new home");
            Assert.AreEqual(moved, nuget.Path);
            Assert.AreEqual(ReclaimRisk.Safe, nuget.Risk);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", original);
        }
    }

    [TestMethod]
    public void ACandidateKnowsWhichVolumeItWouldFreeSpaceOn()
    {
        var candidate = new ReclaimCandidate(
            "c", @"G:\projects\thing\bin", "thing", 100, ReclaimRisk.Safe, "r", "h");

        Assert.AreEqual(@"G:\", candidate.VolumeRoot);
    }

    [TestMethod]
    public void TheSharedRepositoryListCannotBeMutatedByAProvider()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("main-repo");

        IReadOnlyList<DiscoveredRepository> shared =
            Context(fixture).Repositories(CancellationToken.None);

        // Three providers read this concurrently, so one of them reaching through the interface
        // and mutating would corrupt the other two mid-enumeration.
        Assert.IsNotInstanceOfType<List<DiscoveredRepository>>(shared,
            "A shared list must not be castable back to the mutable one the walker built.");
    }

    [TestMethod]
    public void AFolderAmongWorktreesWithNoGitIsAnOrphan()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"lot\wt-a", @"C:\repo\.git\worktrees\a");
        fixture.Worktree(@"lot\wt-b", @"C:\repo\.git\worktrees\b");
        fixture.Dir(@"lot\leftover");

        IReadOnlyList<OrphanedWorktree> orphans = RepositoryWalker.FindOrphanedWorktrees(
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None));

        Assert.HasCount(1, orphans);
        Assert.AreEqual("leftover", orphans[0].Name);
        Assert.AreEqual(2, orphans[0].SiblingWorktreeCount);
    }

    [TestMethod]
    public void AGeneralSourceRootDoesNotIndictEveryFolderInIt()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"src\one-worktree", @"C:\repo\.git\worktrees\a");
        fixture.Repo(@"src\clone-a");
        fixture.Repo(@"src\clone-b");
        fixture.Dir(@"src\notes");
        fixture.Dir(@"src\scratch");

        IReadOnlyList<OrphanedWorktree> orphans = RepositoryWalker.FindOrphanedWorktrees(
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None));

        Assert.IsEmpty(orphans,
            "One worktree beside ordinary clones is a source root, not a parking lot. Calling " +
            "every plain folder there a leftover is how a reclaim tool loses trust.");
    }

    [TestMethod]
    public void WorktreesMustBeTheMajorityBeforeSiblingsAreSuspect()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"lot\wt-a", @"C:\repo\.git\worktrees\a");
        fixture.Worktree(@"lot\wt-b", @"C:\repo\.git\worktrees\b");
        fixture.Repo(@"lot\clone-a");
        fixture.Repo(@"lot\clone-b");
        fixture.Repo(@"lot\clone-c");
        fixture.Dir(@"lot\plain");

        IReadOnlyList<OrphanedWorktree> orphans = RepositoryWalker.FindOrphanedWorktrees(
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None));

        Assert.IsEmpty(orphans, "Two worktrees among three clones is not a parking lot.");
    }

    [TestMethod]
    public void AFolderWithRepositoriesUnderneathIsNeverAnOrphan()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"lot\wt-a", @"C:\repo\.git\worktrees\a");
        fixture.Worktree(@"lot\wt-b", @"C:\repo\.git\worktrees\b");
        fixture.Repo(@"lot\container\inner");

        IReadOnlyList<OrphanedWorktree> orphans = RepositoryWalker.FindOrphanedWorktrees(
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None));

        Assert.IsEmpty(orphans,
            "Deleting a folder that holds live repositories is the one mistake this must not make.");
    }

    [TestMethod]
    public void ASiblingIsNotMistakenForAnAncestorByNamePrefix()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"lot\wt-a", @"C:\repo\.git\worktrees\a");
        fixture.Worktree(@"lot\wt-b", @"C:\repo\.git\worktrees\b");
        fixture.Dir(@"lot\app");
        fixture.Repo(@"lot\app2\inner");

        IReadOnlyList<OrphanedWorktree> orphans = RepositoryWalker.FindOrphanedWorktrees(
            RepositoryWalker.Discover([fixture.Root], CancellationToken.None));

        // "app2\inner" starts with "app" as raw text but is not inside it. Comparing without a
        // separator boundary would let app2's repo vouch for app and hide a real leftover.
        Assert.HasCount(1, orphans);
        Assert.AreEqual("app", orphans[0].Name);
    }

    [TestMethod]
    public void FilesThatMatchOnlyAtTheStartAreNotCalledIdentical()
    {
        using var fixture = new ReclaimFixture();

        // 5 MB, identical everywhere except the final byte. The old 1 MB-prefix hash never read far
        // enough to tell these apart and offered one of them for deletion while the row claimed
        // "byte-for-byte identical" — the exact shape of a data-loss bug in a delete tool.
        byte[] original = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        byte[] diverges = [.. original];
        diverges[^1] ^= 0xFF;

        fixture.FileWithContent(@"x\one.bin", original);
        fixture.FileWithContent(@"y\two.bin", diverges);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsEmpty(found,
            "Same size and same opening megabyte is a shortlist, not a match.");
    }

    [TestMethod]
    public void TrulyIdenticalFilesAreStillFound()
    {
        using var fixture = new ReclaimFixture();

        byte[] payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        fixture.FileWithContent(@"x\one.bin", payload);
        fixture.FileWithContent(@"y\two.bin", payload);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        // One row: one copy is kept, the other offered. The guard above must not have been bought
        // by making the provider find nothing at all.
        Assert.HasCount(1, found);
        Assert.AreEqual(ReclaimRisk.Check, found[0].Risk);
        Assert.Contains("in full", found[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void FilesBelowTheScansOwnFloorAreNeverHashed()
    {
        using var fixture = new ReclaimFixture();

        byte[] payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        fixture.FileWithContent(@"x\one.bin", payload);
        fixture.FileWithContent(@"y\two.bin", payload);

        // ReclaimEngine drops everything under this floor before rendering, so reading these files
        // could only ever produce output nobody sees.
        var context = new ReclaimScanContext(
            [@"C:\"], [fixture.Root], minimumCandidateBytes: 32L * 1024 * 1024);

        IReadOnlyList<ReclaimCandidate> found = new DuplicateFileReclaimProvider()
            .ScanAsync(context, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsEmpty(found);
    }

    [TestMethod]
    public void CopiesOfOneFileAreToldApartByFolder()
    {
        using var fixture = new ReclaimFixture();

        byte[] payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        fixture.FileWithContent(@"keep\shared.pdb", payload);
        fixture.FileWithContent(@"alpha\out\shared.pdb", payload);
        fixture.FileWithContent(@"beta\out\shared.pdb", payload);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        // Every copy shares a file name, and the table shows the full path only as a tooltip. Rows a
        // reader cannot tell apart are rows a reader cannot safely tick.
        Assert.HasCount(2, found);
        Assert.AreNotEqual(found[0].Detail, found[1].Detail,
            "Two offered copies rendered the same sub-line.");
        Assert.IsTrue(found.Any(c => c.Detail!.Contains(@"alpha\out", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(found.Any(c => c.Detail!.Contains(@"beta\out", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CopiesSharingAFolderLayoutStillReadDifferently()
    {
        using var fixture = new ReclaimFixture();

        byte[] payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        fixture.FileWithContent(@"keep\shared.pdb", payload);

        // The same build layout in two repositories: a three-segment tail is identical for both.
        fixture.FileWithContent(@"alpha\x64\Debug\Symbols\shared.pdb", payload);
        fixture.FileWithContent(@"beta\x64\Debug\Symbols\shared.pdb", payload);

        var provider = new DuplicateFileReclaimProvider();
        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(2, found);
        Assert.AreNotEqual(found[0].Detail, found[1].Detail,
            "The tail widens until the copies differ, or two rows say the same thing.");
    }

    private static ReclaimScanContext Context(ReclaimFixture fixture) =>
        new([@"C:\"], [fixture.Root], minimumCandidateBytes: 1);

    [TestMethod]
    public void TheRepositoryWalkHappensOncePerContext()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("main-repo");
        fixture.Worktree("side-tree", @"C:\main-repo\.git\worktrees\side");

        ReclaimScanContext context = Context(fixture);

        IReadOnlyList<DiscoveredRepository> first = context.Repositories(CancellationToken.None);
        IReadOnlyList<DiscoveredRepository> second = context.Repositories(CancellationToken.None);

        Assert.HasCount(2, first);
        Assert.AreSame(first, second,
            "A second walk would be the same answer bought twice, and two lists free to disagree.");
    }

    [TestMethod]
    public async Task ConcurrentProvidersShareOneRepositoryWalk()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("main-repo");
        fixture.Worktree("side-tree", @"C:\main-repo\.git\worktrees\side");

        ReclaimScanContext context = Context(fixture);

        // The engine fans providers out with Task.WhenAll, so this is the shape that actually
        // happens: three callers arriving at once. Identity is the proof — separate walks could
        // not return the same list instance.
        IReadOnlyList<DiscoveredRepository>[] results = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ =>
                Task.Run(() => context.Repositories(CancellationToken.None))));

        Assert.AreSame(results[0], results[1]);
        Assert.AreSame(results[0], results[2]);
    }

    [TestMethod]
    public void ACancelledWalkDoesNotPoisonTheContext()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("main-repo");

        ReclaimScanContext context = Context(fixture);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => context.Repositories(cancelled.Token));

        // A Lazy<T> would have cached that exception and refused to ever answer again.
        Assert.HasCount(1, context.Repositories(CancellationToken.None));
    }
}
