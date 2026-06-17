using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Headless tests for <see cref="EcosystemCardViewModel"/> — the Concept A per-ecosystem adapter. It is a
/// pure projection over the existing engines, so the behaviour worth pinning is (1) its aggregation of the
/// member rows into an honest location chip / status summary / on-Dev-Drive count, recomputed live as a
/// row's state changes, and (2) the non-negotiable honesty rule: a benchmark is exposed ONLY when this
/// ecosystem has a real workload AND a matching suite row was supplied — never a fabricated number.
/// </summary>
[TestClass]
public sealed class EcosystemCardViewModelTests
{
    private static readonly Action<PackageCacheRowViewModel> Noop = _ => { };

    private static PackageCacheRowViewModel Row(string name, string envVar, bool detected, bool onDev, ulong size = 0UL)
    {
        var info = new PackageCacheInfo
        {
            Name = name,
            EnvironmentVariable = envVar,
            ResolvedPath = onDev ? $@"G:\packages\{name}" : $@"C:\Users\dev\{name}",
            Detected = detected,
            DriveLetter = detected ? 'C' : null,
            OnDevDrive = onDev,
        };
        var row = new PackageCacheRowViewModel(info, 'G', 'C', Noop, Noop, Noop, Noop, Noop, Noop);
        row.SetSize(size);
        return row;
    }

    private static EcosystemDefinition Def(string name) => EcosystemCatalog.Default.First(e => e.Name == name);

    private static PerfSuiteRowViewModel NpmBenchmarkRow() =>
        new PerformanceSuiteViewModel(Substitute.For<IWorkloadBenchmarkService>())
            .BuildsGroup.Rows.First(r => r.RequiredTool == "npm");

    [TestMethod]
    public void Node_AllToolsOnSystemDrive_ShowsActionableSystemChip_AndHonestFraction()
    {
        var rows = new[]
        {
            Row("npm", "npm_config_cache", detected: true, onDev: false, size: 100UL * 1024 * 1024),
            Row("Yarn", "YARN_CACHE_FOLDER", detected: true, onDev: false, size: 50UL * 1024 * 1024),
        };

        var card = new EcosystemCardViewModel(Def("Node"), rows, null, 'G', 'C');

        Assert.AreEqual("system", card.LocationKind);
        StringAssert.Contains(card.LocationText, "On C:");
        Assert.AreEqual(0, card.OnDevDriveCount);
        Assert.IsTrue(card.IsDetected);
        StringAssert.Contains(card.StatusSummary, "0 of 2");
    }

    [TestMethod]
    public void Node_MixedLocations_CountsOnDev_ButKeepsTheChipActionableWhileSomethingIsMovable()
    {
        var rows = new[]
        {
            Row("npm", "npm_config_cache", detected: true, onDev: true),
            Row("Yarn", "YARN_CACHE_FOLDER", detected: true, onDev: false, size: 50UL * 1024 * 1024),
        };

        var card = new EcosystemCardViewModel(Def("Node"), rows, null, 'G', 'C');

        Assert.AreEqual("system", card.LocationKind, "There is still an upside to act on, so the chip stays 'system'.");
        Assert.AreEqual(1, card.OnDevDriveCount);
        StringAssert.Contains(card.StatusSummary, "1 of 2");
    }

    [TestMethod]
    public void Node_AllOnDevDrive_ShowsDevChip()
    {
        var rows = new[]
        {
            Row("npm", "npm_config_cache", detected: true, onDev: true),
            Row("Yarn", "YARN_CACHE_FOLDER", detected: true, onDev: true),
        };

        var card = new EcosystemCardViewModel(Def("Node"), rows, null, 'G', 'C');

        Assert.AreEqual("dev", card.LocationKind, "An active state must never be coloured grey/notfound.");
        Assert.AreEqual("On your Dev Drive", card.LocationText);
        Assert.AreEqual(2, card.OnDevDriveCount);
    }

    [TestMethod]
    public void Undetected_SingleToolEcosystem_ReadsNotDetected_AndShowsNoFakeBenchmark()
    {
        var rows = new[] { Row("Cargo", "CARGO_HOME", detected: false, onDev: false) };

        // Rust HAS a workload, but with no suite row supplied the card must fall back to the honest note.
        var card = new EcosystemCardViewModel(Def("Rust"), rows, null, 'G', 'C');

        Assert.AreEqual("notfound", card.LocationKind);
        Assert.AreEqual("Not detected", card.LocationText);
        Assert.IsFalse(card.IsDetected);
        Assert.IsFalse(card.HasBenchmark);
        Assert.IsTrue(card.ShowNoBenchmarkNote);
        StringAssert.Contains(card.NoBenchmarkNote, "not available");
    }

    [TestMethod]
    public void NonBenchmarkedEcosystem_NeverExposesABenchmark_EvenIfOneIsMisHandedIn()
    {
        // Python has NO workload; even if a stray benchmark row is passed, the card must ignore it.
        var rows = new[] { Row("pip", "PIP_CACHE_DIR", detected: true, onDev: false, size: 10UL * 1024 * 1024) };

        var card = new EcosystemCardViewModel(Def("Python"), rows, NpmBenchmarkRow(), 'G', 'C');

        Assert.IsFalse(card.HasBenchmark, "An unmeasured ecosystem must never advertise a speed-up.");
        Assert.IsNull(card.Benchmark);
        Assert.IsTrue(card.ShowNoBenchmarkNote);
    }

    [TestMethod]
    public void BenchmarkedEcosystem_WithMatchingSuiteRow_ExposesTheRealBenchmark()
    {
        PerfSuiteRowViewModel npm = NpmBenchmarkRow();
        var rows = new[] { Row("npm", "npm_config_cache", detected: true, onDev: false, size: 100UL * 1024 * 1024) };

        var card = new EcosystemCardViewModel(Def("Node"), rows, npm, 'G', 'C');

        Assert.IsTrue(card.HasBenchmark);
        Assert.IsFalse(card.ShowNoBenchmarkNote);
        Assert.AreSame(npm, card.Benchmark);
    }

    [TestMethod]
    public void Aggregates_RecomputeLive_WhenARowChangesState()
    {
        PackageCacheRowViewModel yarn = Row("Yarn", "YARN_CACHE_FOLDER", detected: true, onDev: false, size: 50UL * 1024 * 1024);
        var rows = new[]
        {
            Row("npm", "npm_config_cache", detected: true, onDev: false, size: 100UL * 1024 * 1024),
            yarn,
        };
        var card = new EcosystemCardViewModel(Def("Node"), rows, null, 'G', 'C');
        Assert.AreEqual(0, card.OnDevDriveCount);

        // Simulate the live flip a successful move performs (ApplyMoveOutcome sets CanMove=false, IsSet=true).
        yarn.CanMove = false;
        yarn.IsSet = true;

        Assert.AreEqual(1, card.OnDevDriveCount, "The card recomputes from the row's PropertyChanged.");
        StringAssert.Contains(card.StatusSummary, "1 of 2");
    }

    [TestMethod]
    public void MappedRow_CountsAndColoursByItsActualDrive_NotAlwaysDev()
    {
        // Mapped to a Dev Drive folder → counts as on the Dev Drive (accent chip).
        PackageCacheRowViewModel onDev = Row("pip", "PIP_CACHE_DIR", detected: false, onDev: false);
        onDev.MapPath = @"G:\caches\pip";
        onDev.IsMapped = true;
        var devCard = new EcosystemCardViewModel(Def("Python"), new[] { onDev }, null, 'G', 'C');
        Assert.AreEqual("dev", devCard.LocationKind, "Mapped onto the Dev Drive counts as on-Dev.");
        Assert.AreEqual(1, devCard.OnDevDriveCount);

        // Mapped to a folder still on C: → must NOT show the Dev Drive chip, and must not count as on-Dev.
        PackageCacheRowViewModel onSystem = Row("pip", "PIP_CACHE_DIR", detected: false, onDev: false);
        onSystem.MapPath = @"C:\caches\pip";
        onSystem.IsMapped = true;
        var systemCard = new EcosystemCardViewModel(Def("Python"), new[] { onSystem }, null, 'G', 'C');
        Assert.AreEqual("system", systemCard.LocationKind, "Mapped to a C: folder is NOT on the Dev Drive.");
        Assert.AreEqual(0, systemCard.OnDevDriveCount);
    }

    [TestMethod]
    public void Header_ToolsLine_AndAutomationId_AreDerivedFromTheDefinition()
    {
        var card = new EcosystemCardViewModel(Def("Node"), Array.Empty<PackageCacheRowViewModel>(), null, 'G', 'C');

        Assert.AreEqual("Node", card.Name);
        Assert.AreEqual("Ecosystem_Node", card.AutomationId);
        StringAssert.Contains(card.ToolsLine, "npm");
        StringAssert.Contains(card.ToolsLine, "Deno");
        Assert.IsFalse(card.IsDetected, "An empty card is honestly 'not detected'.");
    }
}
