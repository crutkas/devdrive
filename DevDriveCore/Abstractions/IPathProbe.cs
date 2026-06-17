namespace DevDriveCore.Abstractions;

/// <summary>
/// Resolves the full path of an executable as it would be found on the user's <c>PATH</c>
/// ("where"-style), isolated behind an interface so <see cref="DevDriveCore.Services.InstalledToolDetector"/>
/// is unit-testable without launching processes or reading the real environment.
/// </summary>
/// <remarks>
/// The real implementation (<see cref="DevDriveCore.Platform.PathProbe"/>) is read-only: it walks
/// the <c>PATH</c> directories applying <c>PATHEXT</c> and returns the first match. It never executes
/// the resolved file.
/// </remarks>
public interface IPathProbe
{
    /// <summary>
    /// Returns the absolute path of the first <paramref name="executableName"/> found on <c>PATH</c>
    /// (trying each <c>PATHEXT</c> extension when the name has none), or <c>null</c> when not found.
    /// </summary>
    string? Resolve(string executableName);
}
