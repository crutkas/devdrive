namespace DevDriveCore.Models;

/// <summary>
/// Detection snapshot for a single developer tool, produced by
/// <see cref="DevDriveCore.Services.InstalledToolDetector"/> from a read-only version probe and a
/// <c>PATH</c> lookup. Pure data — performs no mutation.
/// </summary>
public sealed record InstalledToolInfo
{
    /// <summary>Tool display name, e.g. "git", "node", "dotnet".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True when the tool was resolved on <c>PATH</c> and/or reported a version.</summary>
    public bool Found { get; init; }

    /// <summary>Parsed version string (e.g. "2.43.0"), or <c>null</c> when not found / unparseable.</summary>
    public string? Version { get; init; }

    /// <summary>Absolute path of the resolved executable, or <c>null</c> when it was not found on <c>PATH</c>.</summary>
    public string? Path { get; init; }
}
