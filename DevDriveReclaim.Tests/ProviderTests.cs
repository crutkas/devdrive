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
        Assert.IsTrue(found.All(c => c.Detail!.Contains("keeping", StringComparison.OrdinalIgnoreCase)));
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

    private static ReclaimScanContext Context(ReclaimFixture fixture) =>
        new([@"C:\"], [fixture.Root], minimumCandidateBytes: 1);
}
