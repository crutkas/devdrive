using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IEnvironmentWriter"/> over <see cref="Environment"/>, scoped to
/// <see cref="EnvironmentVariableTarget.User"/> (per-user, no elevation, persisted across sessions).
/// </summary>
/// <remarks>
/// <b>COMPOSED BY THE APP (M4):</b> the app's <see cref="Services.PackageCacheMover"/> constructs this to
/// repoint a cache's per-user variable, but only after an explicit user confirmation. The test suite
/// always injects an in-memory fake instead, and the UI-test seam substitutes a SAFE fake, so no test
/// mutates a real environment variable.
/// </remarks>
public sealed class UserEnvironmentWriter : IEnvironmentWriter
{
    /// <inheritdoc />
    public string? GetUserVariable(string name) =>
        string.IsNullOrEmpty(name) ? null : Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    /// <inheritdoc />
    public void SetUserVariable(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // An empty/null value removes the variable (restores the "unset" state).
        Environment.SetEnvironmentVariable(
            name,
            string.IsNullOrEmpty(value) ? null : value,
            EnvironmentVariableTarget.User);
    }
}
