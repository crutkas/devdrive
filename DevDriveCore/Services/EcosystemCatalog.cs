namespace DevDriveCore.Services;

/// <summary>
/// Static definition of one developer <em>ecosystem</em> (a language/runtime stack) for the unified
/// per-ecosystem cards: its display name, a Segoe Fluent Icons glyph, the catalogue tools that belong
/// to it (matched by <see cref="PackageCacheDefinition.Name"/>), and — when a real benchmark exists —
/// the <c>RequiredTool</c> of the matching workload row.
/// </summary>
/// <param name="Name">Ecosystem display name, e.g. "Node".</param>
/// <param name="Glyph">Segoe Fluent Icons glyph (no emoji), e.g. <c>"\uE943"</c>.</param>
/// <param name="ToolNames">
/// The member catalogue tools, in display order, matched against <see cref="PackageCacheDefinition.Name"/>.
/// </param>
/// <param name="BenchmarkRequiredTool">
/// The <c>RequiredTool</c> of the workload row that measures this ecosystem (e.g. "npm", "dotnet",
/// "cargo"), or <c>null</c> when no real benchmark exists for it. ONLY git/npm/dotnet/cargo have
/// workloads, so only Node (npm), .NET (dotnet) and Rust (cargo) carry a measured speed-up — every
/// other ecosystem is movable but honestly unmeasured.
/// </param>
public sealed record EcosystemDefinition(
    string Name,
    string Glyph,
    IReadOnlyList<string> ToolNames,
    string? BenchmarkRequiredTool)
{
    /// <summary>True when a real workload benchmark backs this ecosystem (an honest "N× faster" exists).</summary>
    public bool HasBenchmark => !string.IsNullOrEmpty(BenchmarkRequiredTool);

    /// <summary>
    /// True for a benchmark-only card (e.g. Git): it has a workload but NO movable package cache, so the
    /// card shows just the measured benchmark — no member tools, location chip, status line, or Map path.
    /// </summary>
    public bool IsBenchmarkOnly { get; init; }

    /// <summary>Optional card subtitle; when null the member tool names are joined instead.</summary>
    public string? Subtitle { get; init; }
}

/// <summary>
/// The static ecosystem grouping that the per-ecosystem cards render. It is pure data: it maps the
/// <see cref="PackageCacheCatalog"/> tools into language ecosystems and records which ecosystems have a
/// real benchmark (Node → npm, .NET → dotnet, Rust → cargo). It introduces NO new mutation or
/// benchmark logic — the card view-model adapts these groups over the existing move coordinator and
/// workload benchmark service.
/// </summary>
public static class EcosystemCatalog
{
    /// <summary>
    /// The default ecosystem grouping, in presentation order — the three benchmarked stacks first
    /// (Node, .NET, Rust), then the movable-but-unmeasured ones. Tool names match
    /// <see cref="PackageCacheCatalog.Default"/> exactly.
    /// </summary>
    public static IReadOnlyList<EcosystemDefinition> Default { get; } = new[]
    {
        // Universal filesystem baseline: a real workload (git clone) but NO movable package cache, so it
        // renders as a benchmark-only card pinned at the very top.
        new EcosystemDefinition("Git", "\uE8C8", Array.Empty<string>(), "git")
        {
            IsBenchmarkOnly = true,
            Subtitle = "git clone speed \u00B7 a universal filesystem baseline (no network)",
        },

        // Benchmarked (a real PerfSuite workload row exists).
        new EcosystemDefinition("Node", "\uE943", new[] { "npm", "Yarn", "pnpm", "Bun", "Deno" }, "npm"),
        new EcosystemDefinition(".NET", "\uE8B7", new[] { "NuGet global packages" }, "dotnet"),
        new EcosystemDefinition("Rust", "\uE950", new[] { "Cargo" }, "cargo"),

        // Movable but unmeasured (no workload — honest "Run test not available yet").
        new EcosystemDefinition("Python", "\uE756", new[] { "pip", "uv", "Poetry" }, null),
        new EcosystemDefinition("Java", "\uE8F1", new[] { "Gradle" }, null),
        new EcosystemDefinition("Go", "\uE81E", new[] { "Go modules" }, null),
        new EcosystemDefinition("C++", "\uE74C", new[] { "vcpkg" }, null),
        new EcosystemDefinition("Dart / Flutter", "\uE7C1", new[] { "Pub (Dart/Flutter)" }, null),
    };

    /// <summary>
    /// Returns the ecosystem that owns the catalogue tool named <paramref name="toolName"/> (matched
    /// case-insensitively), or <c>null</c> when the tool is not grouped by any ecosystem.
    /// </summary>
    public static EcosystemDefinition? ForTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        foreach (EcosystemDefinition ecosystem in Default)
        {
            foreach (string member in ecosystem.ToolNames)
            {
                if (string.Equals(member, toolName, StringComparison.OrdinalIgnoreCase))
                {
                    return ecosystem;
                }
            }
        }

        return null;
    }
}
