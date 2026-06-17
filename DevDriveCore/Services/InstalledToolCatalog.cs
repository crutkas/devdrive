namespace DevDriveCore.Services;

/// <summary>
/// How to probe a single developer tool: the executable to launch and the read-only argument that
/// makes it print its version.
/// </summary>
/// <param name="Name">Display name, e.g. "git", "maven".</param>
/// <param name="Executable">Executable to resolve/launch, e.g. <c>git</c>, <c>mvn</c>.</param>
/// <param name="VersionArguments">Read-only version argument(s), e.g. <c>--version</c> (note <c>java</c> uses <c>-version</c>).</param>
public sealed record ToolProbeSpec(string Name, string Executable, string VersionArguments);

/// <summary>
/// The built-in catalogue of developer tools <see cref="InstalledToolDetector"/> probes. Each entry
/// is a <em>read-only</em> version probe — the executable is launched only to print its version.
/// </summary>
public static class InstalledToolCatalog
{
    /// <summary>The default catalogue (the tools this milestone targets).</summary>
    public static IReadOnlyList<ToolProbeSpec> Default { get; } = new[]
    {
        new ToolProbeSpec("git", "git", "--version"),
        new ToolProbeSpec("node", "node", "--version"),
        new ToolProbeSpec("npm", "npm", "--version"),
        new ToolProbeSpec("pnpm", "pnpm", "--version"),
        new ToolProbeSpec("yarn", "yarn", "--version"),
        new ToolProbeSpec("dotnet", "dotnet", "--version"),
        new ToolProbeSpec("cargo", "cargo", "--version"),
        new ToolProbeSpec("rustc", "rustc", "--version"),
        new ToolProbeSpec("python", "python", "--version"),
        new ToolProbeSpec("pip", "pip", "--version"),
        // java prints its version to STDERR and uses a single-dash flag.
        new ToolProbeSpec("java", "java", "-version"),
        new ToolProbeSpec("maven", "mvn", "--version"),
        new ToolProbeSpec("gradle", "gradle", "--version"),
        // go uses the sub-command form: "go version".
        new ToolProbeSpec("go", "go", "version"),
    };
}
