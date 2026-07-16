using System.Diagnostics;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Headless tests for <see cref="EcosystemsViewModel"/> — the Concept A top-level adapter that regroups
/// the reused package-cache rows + the reused benchmark rows into one card per language ecosystem. It adds
/// NO new engine logic, so the behaviour to pin is the restructure itself: each catalogue tool lands in
/// the right card, a benchmark is attached ONLY to Node/.NET/Rust, and the top summary counts honestly.
/// Detection/moving run through the REAL coordinator over in-memory seams; the benchmark service is a
/// substitute that is never invoked, so NO real cache, variable, disk, or workload process is touched.
/// </summary>
[TestClass]
public sealed class EcosystemsViewModelTests
{
    [TestMethod]
    public async Task BuildCards_GroupsEveryToolIntoItsEcosystem_AndBuildsOneCardPerEcosystem()
    {
        EcosystemsViewModel eco = BuildEcosystems(out _, out _);
        eco.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => eco.Cards.Count == EcosystemCatalog.Default.Count);

        Assert.HasCount(9, eco.Cards, "One card per ecosystem (incl. the Git benchmark-only card).");

        EcosystemCardViewModel node = Card(eco, "Node");
        CollectionAssert.AreEquivalent(new[] { "npm", "Yarn" }, node.Rows.Select(r => r.Header).ToList());

        EcosystemCardViewModel dotnet = Card(eco, ".NET");
        CollectionAssert.AreEquivalent(new[] { "NuGet global packages" }, dotnet.Rows.Select(r => r.Header).ToList());

        EcosystemCardViewModel python = Card(eco, "Python");
        CollectionAssert.AreEquivalent(new[] { "pip" }, python.Rows.Select(r => r.Header).ToList());

        // Ecosystems with no returned tools still get a card (so "Map path" is offered), just with no rows.
        Assert.IsEmpty(Card(eco, "Go").Rows);
    }

    [TestMethod]
    public async Task BuildCards_OrdersDetectedFirst_ThenAlphabetical()
    {
        EcosystemsViewModel eco = BuildEcosystems(out _, out _);
        eco.Initialize(@"C:\", @"G:\", 'G', 'C');

        // Node, .NET and Python are detected on C:; everything else is undetected.
        await WaitUntilAsync(() => eco.Cards.Count == EcosystemCatalog.Default.Count
            && eco.Cards.Count(c => c.IsDetected) == 3);

        // Git (benchmark-only) is pinned first; then detected ecosystems (alphabetical by visible letters,
        // so ".NET" sorts under "N"); then undetected ones, also alphabetical (so "Rust" is last despite
        // having a benchmark — ordering is presence-based, not capability-based).
        CollectionAssert.AreEqual(
            new[] { "Git", ".NET", "Node", "Python", "C++", "Dart / Flutter", "Go", "Java", "Rust" },
            eco.Cards.Select(c => c.Name).ToList(),
            "Git pinned first, then detected (alphabetical), then undetected (alphabetical).");
    }

    [TestMethod]
    public async Task BuildCards_AttachesABenchmark_OnlyToGitNodeDotNetRust()
    {
        EcosystemsViewModel eco = BuildEcosystems(out _, out _);
        eco.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => eco.Cards.Count == EcosystemCatalog.Default.Count);

        Assert.IsTrue(Card(eco, "Git").HasBenchmark, "Git is measured by the git clone workload.");
        Assert.IsTrue(Card(eco, "Node").HasBenchmark, "Node is measured by the npm workload.");
        Assert.IsTrue(Card(eco, ".NET").HasBenchmark, ".NET is measured by the dotnet workload.");
        Assert.IsTrue(Card(eco, "Rust").HasBenchmark, "Rust is measured by the cargo workload.");

        foreach (string unmeasured in new[] { "Python", "Java", "Go", "C++", "Dart / Flutter" })
        {
            EcosystemCardViewModel card = Card(eco, unmeasured);
            Assert.IsFalse(card.HasBenchmark, $"'{unmeasured}' has no workload and must show no benchmark.");
            Assert.IsTrue(card.ShowNoBenchmarkNote, $"'{unmeasured}' must show the honest 'not available' note.");
        }
    }

    [TestMethod]
    public async Task SummaryText_IsHonest_CountingOnlyDetectedEcosystems()
    {
        EcosystemsViewModel eco = BuildEcosystems(out _, out _);
        eco.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => eco.Cards.Count == EcosystemCatalog.Default.Count);

        // npm, Yarn, NuGet and pip are detected on C:; Cargo is undetected. So 3 ecosystems are detected
        // (Node, .NET, Python) and none is on the Dev Drive yet.
        StringAssert.Contains(eco.SummaryText, "0 of 3 ecosystems");
    }

    [TestMethod]
    public async Task NoDevDrive_StillBuildsDetectedCacheInventory()
    {
        EcosystemsViewModel eco = BuildEcosystems(out _, out _);

        eco.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');
        await WaitUntilAsync(() => eco.Cards.Count == EcosystemCatalog.Default.Count
            && eco.Caches.Caches.Count == 5
            && eco.Cards.Count(c => c.IsDetected) == 3);

        Assert.IsFalse(eco.Caches.HasDevDrive);
        Assert.IsTrue(eco.Caches.HasDetectedCaches);
        Assert.IsTrue(eco.Caches.Caches.Where(c => c.Info.Detected).All(c => !c.CanMove));
        Assert.IsFalse(eco.Suite.HasDevDrive);
        Assert.IsTrue(eco.Suite.RunAllCommand.CanExecute(null));
        Assert.IsTrue(Card(eco, "Node").HasBenchmark);
        Assert.IsFalse(Card(eco, "Node").Benchmark!.HasComparison);
        StringAssert.Contains(eco.SummaryText, "3 ecosystems detected on this PC");
    }

    [TestMethod]
    public async Task DriveQueryFailure_RestartsDetectionWithoutMoveCapability()
    {
        EcosystemsViewModel eco = BuildEcosystems(out _, out _);
        eco.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => eco.Caches.Caches.Count == 5);
        PackageCacheRowViewModel oldRow = eco.Caches.Caches[0];

        eco.SetDevDriveUnavailable("Drive status is unavailable.");
        await WaitUntilAsync(() => eco.Caches.Caches.Count == 5
            && !ReferenceEquals(eco.Caches.Caches[0], oldRow));

        Assert.IsFalse(eco.Caches.HasDevDrive);
        Assert.IsTrue(eco.Caches.HasDetectedCaches);
        Assert.IsTrue(eco.Caches.Caches.All(row => !row.CanMove));
        Assert.IsTrue(eco.Suite.ShowSystemDriveBaseline);
        Assert.AreEqual("Drive status is unavailable.", eco.Caches.DevDriveNoticeMessage);
    }

    [TestMethod]
    public async Task MovingAToolUpdatesItsCard_AndTheTopSummary_Live()
    {
        EcosystemsViewModel eco = BuildEcosystems(out FakeEnvironmentWriter env, out _);
        eco.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => eco.Cards.Count == EcosystemCatalog.Default.Count
            && Card(eco, ".NET").Rows.Count == 1 && Card(eco, ".NET").Rows[0].SizeBytes > 0);

        EcosystemCardViewModel dotnet = Card(eco, ".NET");
        PackageCacheRowViewModel nuget = dotnet.Rows[0];

        nuget.ConfirmMoveCommand.Execute(null); // real (in-memory) move through the reused coordinator
        await WaitUntilAsync(() => nuget.IsSet && !nuget.IsMoving && nuget.ShowMoveResult);

        Assert.AreEqual(1, dotnet.OnDevDriveCount, "The .NET card now reports its tool on the Dev Drive.");
        Assert.AreEqual("dev", dotnet.LocationKind);
        StringAssert.Contains(eco.SummaryText, "1 of 3 ecosystems");
        Assert.AreEqual(@"G:\packages\nuget global packages", env.GetUserVariable("NUGET_PACKAGES"));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static EcosystemCardViewModel Card(EcosystemsViewModel eco, string name) =>
        eco.Cards.First(c => c.Name == name);

    private static EcosystemsViewModel BuildEcosystems(out FakeEnvironmentWriter env, out InMemoryFileSystem fs)
    {
        fs = new InMemoryFileSystem();
        // Seed the detected sources so the real in-memory mover can copy + hash-verify them.
        fs.AddFile(@"C:\Users\dev\npm\a.txt", "alpha");
        fs.AddFile(@"C:\Users\dev\yarn\a.txt", "alpha");
        fs.AddFile(@"C:\Users\dev\nuget\a.txt", "alpha");
        fs.AddFile(@"C:\Users\dev\pip\a.txt", "alpha");

        env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        var coordinator = new PackageCacheMoveCoordinator(new PackageCacheMover(fs, env, store), store);

        var caches = new[]
        {
            Detected("npm", "npm_config_cache", @"C:\Users\dev\npm"),
            Detected("Yarn", "YARN_CACHE_FOLDER", @"C:\Users\dev\yarn"),
            Detected("NuGet global packages", "NUGET_PACKAGES", @"C:\Users\dev\nuget"),
            Detected("pip", "PIP_CACHE_DIR", @"C:\Users\dev\pip"),
            Undetected("Cargo", "CARGO_HOME"),
        };

        var cachesVm = new PackageCachesViewModel(new FakeCacheService(caches), coordinator);
        var suite = new PerformanceSuiteViewModel(Substitute.For<IWorkloadBenchmarkService>());
        return new EcosystemsViewModel(cachesVm, suite);
    }

    private static PackageCacheInfo Detected(string name, string envVar, string path) => new()
    {
        Name = name,
        EnvironmentVariable = envVar,
        ResolvedPath = path,
        Detected = true,
        DriveLetter = 'C',
        OnDevDrive = false,
    };

    private static PackageCacheInfo Undetected(string name, string envVar) => new()
    {
        Name = name,
        EnvironmentVariable = envVar,
        ResolvedPath = string.Empty,
        Detected = false,
        DriveLetter = null,
        OnDevDrive = false,
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("The expected ViewModel state was not reached within the timeout.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>In-memory <see cref="IPackageCacheService"/>: returns a fixed set and a Dev-Drive-targeted plan.</summary>
    private sealed class FakeCacheService : IPackageCacheService
    {
        private readonly IReadOnlyList<PackageCacheInfo> _caches;

        public FakeCacheService(IReadOnlyList<PackageCacheInfo> caches) => _caches = caches;

        public IReadOnlyList<PackageCacheInfo> GetPackageCaches(char? devDriveLetter) => _caches;

        public Task<ulong> CalculateSizeAsync(PackageCacheInfo cache, TimeSpan timeBudget, CancellationToken cancellationToken = default) =>
            Task.FromResult(cache.Detected ? 24UL * 1024 * 1024 : 0UL);

        public PackageCacheMovePlan BuildMovePlan(PackageCacheInfo cache, char devDriveLetter) => new()
        {
            ToolName = cache.Name,
            SourcePath = cache.ResolvedPath,
            TargetPath = $@"{char.ToUpperInvariant(devDriveLetter)}:\packages\{cache.Name.ToLowerInvariant()}",
            EnvironmentVariable = cache.EnvironmentVariable,
        };
    }
}
