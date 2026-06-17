using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>Real <see cref="IEnvironmentProvider"/> over <see cref="Environment"/>.</summary>
public sealed class SystemEnvironmentProvider : IEnvironmentProvider
{
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name) =>
        string.IsNullOrEmpty(name) ? null : Environment.GetEnvironmentVariable(name);

    /// <inheritdoc />
    public string ExpandEnvironmentVariables(string template) =>
        Environment.ExpandEnvironmentVariables(template ?? string.Empty);
}
