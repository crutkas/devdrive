namespace DevDriveCore.Abstractions;

/// <summary>
/// Write access to <b>per-user</b> environment variables, isolated behind an interface so the
/// app-composed <see cref="DevDriveCore.Services.PackageCacheMover"/> can relocate a cache by
/// repointing its variable while staying unit-testable.
/// </summary>
/// <remarks>
/// <para>This is the write-side counterpart to the read-only <see cref="IEnvironmentProvider"/>.
/// The real implementation (<see cref="DevDriveCore.Platform.UserEnvironmentWriter"/>) targets
/// <see cref="System.EnvironmentVariableTarget.User"/> only — it never touches the machine/system
/// scope (which would require elevation) and never touches the volatile process scope.</para>
/// <para><b>SAFETY:</b> unit tests inject an in-memory fake; the real writer is never exercised by
/// the test suite, so no test mutates a real environment variable.</para>
/// </remarks>
public interface IEnvironmentWriter
{
    /// <summary>Returns the current per-user value of <paramref name="name"/>, or <c>null</c> when unset.</summary>
    string? GetUserVariable(string name);

    /// <summary>
    /// Sets the per-user variable <paramref name="name"/> to <paramref name="value"/>. Passing
    /// <c>null</c> (or empty) <b>removes</b> the variable, restoring the "unset" state.
    /// </summary>
    void SetUserVariable(string name, string? value);
}
