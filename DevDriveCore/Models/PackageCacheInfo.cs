namespace DevDriveCore.Models;

/// <summary>
/// Detection snapshot for a single developer tool's package cache. Pure data produced by
/// <see cref="DevDriveCore.Services.IPackageCacheService"/> from environment variables and a
/// directory-exists probe — it performs no mutation and (by default) carries no size until the
/// caller asks for one.
/// </summary>
public sealed record PackageCacheInfo
{
    /// <summary>Tool display name, e.g. "npm".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Unexpanded default path template, e.g. <c>%AppData%\npm-cache</c>.</summary>
    public string PathTemplate { get; init; } = string.Empty;

    /// <summary>Environment variable that points the tool at its cache, e.g. <c>npm_config_cache</c>.</summary>
    public string EnvironmentVariable { get; init; } = string.Empty;

    /// <summary>Current (raw, unexpanded) value of <see cref="EnvironmentVariable"/>, or <c>null</c> when unset.</summary>
    public string? EnvironmentValue { get; init; }

    /// <summary>True when <see cref="EnvironmentVariable"/> currently has a non-empty value.</summary>
    public bool EnvironmentVariableSet => !string.IsNullOrWhiteSpace(EnvironmentValue);

    /// <summary>Fully expanded/normalized path the tool actually uses (from the env var if set, else the template).</summary>
    public string ResolvedPath { get; init; } = string.Empty;

    /// <summary>True when <see cref="ResolvedPath"/> exists on disk.</summary>
    public bool Detected { get; init; }

    /// <summary>Drive letter of <see cref="ResolvedPath"/> (uppercased), or <c>null</c> when undeterminable.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>True when <see cref="DriveLetter"/> equals the Dev Drive letter.</summary>
    public bool OnDevDrive { get; init; }

    /// <summary>Best-effort size in bytes, or <c>null</c> when not yet calculated.</summary>
    public ulong? SizeBytes { get; init; }
}
