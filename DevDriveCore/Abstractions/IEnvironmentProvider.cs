namespace DevDriveCore.Abstractions;

/// <summary>
/// Read-only access to process/user environment variables. Isolated behind an interface so the
/// package-cache services are unit-testable against an in-memory environment.
/// </summary>
public interface IEnvironmentProvider
{
    /// <summary>Returns the value of an environment variable, or <c>null</c> when it is not set.</summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>Expands <c>%VAR%</c> tokens in <paramref name="template"/> using the current environment.</summary>
    string ExpandEnvironmentVariables(string template);
}
