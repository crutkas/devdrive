namespace DevDriveCore.Models;

/// <summary>
/// A SAFE, read-only description of what moving a package cache to the Dev Drive <em>would</em> do.
/// Building this plan changes nothing on disk or in the environment. Executing it is the job of
/// <see cref="DevDriveCore.Abstractions.IPackageCacheMover"/>, which the app composes and runs only
/// after an explicit per-row user confirmation.
/// </summary>
public sealed record PackageCacheMovePlan
{
    /// <summary>Tool whose cache the plan targets.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Current cache location (the source of the would-be move).</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Proposed Dev Drive location, e.g. <c>G:\packages\npm</c>.</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>Environment variable that would be set (per-user) to <see cref="TargetPath"/>.</summary>
    public string EnvironmentVariable { get; init; } = string.Empty;
}
