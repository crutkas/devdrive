namespace DevDriveCore.Models;

/// <summary>Well-known <see cref="ReversibilityEntry.Kind"/> discriminators.</summary>
public static class ReversibilityKinds
{
    /// <summary>A package cache that was relocated to the Dev Drive (env var + optional file move).</summary>
    public const string PackageCacheMove = "PackageCacheMove";

    /// <summary>A virtual disk that was created and surfaced.</summary>
    public const string VhdProvision = "VhdProvision";
}

/// <summary>
/// A JSON-serializable record of a single reversible mutation performed by one of the app's reversible
/// engines. It captures enough <em>prior</em> state to undo the change later — possibly in a different
/// process — via <see cref="DevDriveCore.Abstractions.IReversibilityStore"/>.
/// </summary>
/// <remarks>
/// The fields are deliberately a flat, explicit union across the engine kinds (rather than a loose
/// dictionary) so the record round-trips cleanly through <c>System.Text.Json</c> and the revert logic
/// is strongly typed. Only the fields relevant to a given <see cref="Kind"/> are populated.
/// </remarks>
public sealed record ReversibilityEntry
{
    /// <summary>Stable, unique key for this entry (e.g. <c>package-cache:npm_config_cache</c>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Discriminator; see <see cref="ReversibilityKinds"/>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>When the mutation was recorded (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Human-readable tool/subject name, e.g. "npm", "VS Code".</summary>
    public string? ToolName { get; init; }

    // ---- Environment variable (PackageCacheMove) ----------------------------------------------

    /// <summary>Environment variable that was changed, if any.</summary>
    public string? EnvironmentVariable { get; init; }

    /// <summary>True when <see cref="EnvironmentVariable"/> had a value before the change (so revert restores it).</summary>
    public bool EnvironmentValueWasSet { get; init; }

    /// <summary>The variable's value before the change (<c>null</c> when it was unset; revert then removes it).</summary>
    public string? PriorEnvironmentValue { get; init; }

    // ---- Filesystem move (PackageCacheMove) ----------------------------------------------------

    /// <summary>Original location of the moved data (the source of the move).</summary>
    public string? SourcePath { get; init; }

    /// <summary>New location of the moved data (the target on the Dev Drive).</summary>
    public string? TargetPath { get; init; }

    /// <summary>True when the move deleted the source after verifying the copy (revert then copies it back).</summary>
    public bool SourceDeleted { get; init; }
}
