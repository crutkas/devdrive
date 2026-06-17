using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the pure <see cref="EcosystemCatalog"/> grouping that the Concept A per-ecosystem cards
/// render. The catalogue is data-only: it must cover every <see cref="PackageCacheCatalog"/> tool exactly
/// once, claim a benchmark ONLY for the three stacks that have a real workload (Node → npm, .NET → dotnet,
/// Rust → cargo), and resolve a tool to its owning ecosystem case-insensitively. This pins the honesty
/// contract — no ecosystem without a workload may ever advertise a measured speed-up.
/// </summary>
[TestClass]
public sealed class EcosystemCatalogTests
{
    private static EcosystemDefinition Def(string name) =>
        EcosystemCatalog.Default.First(e => e.Name == name);

    [TestMethod]
    public void Default_HasNineEcosystems_InPresentationOrder()
    {
        List<string> names = EcosystemCatalog.Default.Select(e => e.Name).ToList();
        CollectionAssert.AreEqual(
            new[] { "Git", "Node", ".NET", "Rust", "Python", "Java", "Go", "C++", "Dart / Flutter" },
            names,
            "Git (benchmark-only) leads, then the benchmarked stacks (Node, .NET, Rust).");
    }

    [TestMethod]
    public void Git_IsBenchmarkOnly_WithAWorkloadButNoMovableTools()
    {
        EcosystemDefinition git = Def("Git");
        Assert.IsTrue(git.IsBenchmarkOnly, "Git is a benchmark-only card.");
        Assert.IsTrue(git.HasBenchmark, "Git clone is a real measured workload.");
        Assert.AreEqual("git", git.BenchmarkRequiredTool);
        Assert.IsEmpty(git.ToolNames, "Git has no movable package cache.");
    }

    [TestMethod]
    public void EveryCatalogueTool_BelongsToExactlyOneEcosystem()
    {
        foreach (PackageCacheDefinition def in PackageCacheCatalog.Default)
        {
            int owners = EcosystemCatalog.Default
                .Count(e => e.ToolNames.Any(n => string.Equals(n, def.Name, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(1, owners, $"Catalogue tool '{def.Name}' must belong to exactly one ecosystem.");
        }
    }

    [TestMethod]
    public void EveryEcosystemTool_ExistsInThePackageCacheCatalogue()
    {
        HashSet<string> catalogue = PackageCacheCatalog.Default
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (EcosystemDefinition def in EcosystemCatalog.Default)
        {
            foreach (string tool in def.ToolNames)
            {
                Assert.Contains(tool, catalogue, $"Ecosystem '{def.Name}' references unknown tool '{tool}'.");
            }
        }
    }

    [TestMethod]
    public void AllFourteenCatalogueTools_AreGroupedExactlyOnce()
    {
        List<string> grouped = EcosystemCatalog.Default.SelectMany(e => e.ToolNames).ToList();
        Assert.HasCount(14, PackageCacheCatalog.Default, "The catalogue is expected to hold 14 tools.");
        Assert.HasCount(14, grouped, "No tool may be grouped twice (or dropped).");
        Assert.AreEqual(14, grouped.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Grouped tool names must be unique.");
    }

    [TestMethod]
    public void OnlyGitNodeDotNetRust_CarryABenchmark_WithTheCorrectRequiredTool()
    {
        List<EcosystemDefinition> benched = EcosystemCatalog.Default.Where(e => e.HasBenchmark).ToList();
        CollectionAssert.AreEqual(
            new[] { "Git", "Node", ".NET", "Rust" },
            benched.Select(e => e.Name).ToList(),
            "Only the stacks/tools with a real workload may advertise a benchmark.");

        Assert.AreEqual("git", Def("Git").BenchmarkRequiredTool);
        Assert.AreEqual("npm", Def("Node").BenchmarkRequiredTool);
        Assert.AreEqual("dotnet", Def(".NET").BenchmarkRequiredTool);
        Assert.AreEqual("cargo", Def("Rust").BenchmarkRequiredTool);
    }

    [TestMethod]
    public void EveryUnmeasuredEcosystem_HonestlyDeclaresNoBenchmark()
    {
        foreach (EcosystemDefinition def in EcosystemCatalog.Default.Where(e => e.Name is not ("Git" or "Node" or ".NET" or "Rust")))
        {
            Assert.IsNull(def.BenchmarkRequiredTool, $"'{def.Name}' has no workload and must NOT claim a benchmark tool.");
            Assert.IsFalse(def.HasBenchmark, $"'{def.Name}' must report HasBenchmark == false.");
        }
    }

    [TestMethod]
    public void Node_GroupsAllFiveJavaScriptRuntimes_InOrder()
    {
        CollectionAssert.AreEqual(
            new[] { "npm", "Yarn", "pnpm", "Bun", "Deno" },
            Def("Node").ToolNames.ToList());
    }

    [TestMethod]
    public void Python_GroupsPipUvPoetry()
    {
        CollectionAssert.AreEqual(new[] { "pip", "uv", "Poetry" }, Def("Python").ToolNames.ToList());
    }

    [TestMethod]
    public void ForTool_ResolvesOwningEcosystem_CaseInsensitively()
    {
        Assert.AreEqual("Node", EcosystemCatalog.ForTool("npm")?.Name);
        Assert.AreEqual("Node", EcosystemCatalog.ForTool("NPM")?.Name);
        Assert.AreEqual("Node", EcosystemCatalog.ForTool("Deno")?.Name);
        Assert.AreEqual(".NET", EcosystemCatalog.ForTool("NuGet global packages")?.Name);
        Assert.AreEqual("Rust", EcosystemCatalog.ForTool("cargo")?.Name);
        Assert.AreEqual("Dart / Flutter", EcosystemCatalog.ForTool("Pub (Dart/Flutter)")?.Name);
    }

    [TestMethod]
    public void ForTool_ReturnsNull_ForUnknownOrBlank()
    {
        Assert.IsNull(EcosystemCatalog.ForTool("conda"));
        Assert.IsNull(EcosystemCatalog.ForTool(""));
        Assert.IsNull(EcosystemCatalog.ForTool("   "));
    }
}
