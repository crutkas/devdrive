namespace DevDriveCore.Abstractions;

/// <summary>
/// A request to run an external tool for a benchmark, with the working directory, extra environment
/// variables, and a generous timeout that <see cref="IProcessRunner"/> deliberately lacks.
/// </summary>
/// <remarks>
/// <see cref="IProcessRunner"/> is intentionally minimal (15&#160;s timeout, no cwd/env) and is part of
/// an M1 contract used by the Dev Drive service for <c>fsutil</c>; benchmarks need a longer timeout,
/// a working directory, and per-drive cache-isolation env vars (e.g. <c>NUGET_PACKAGES</c>,
/// <c>CARGO_HOME</c>), so this is a separate seam rather than a breaking change to that one.
/// </remarks>
/// <param name="FileName">Executable to launch (e.g. <c>git</c>, <c>cmd</c>, <c>dotnet</c>, <c>cargo</c>).</param>
/// <param name="Arguments">Command-line arguments.</param>
/// <param name="WorkingDirectory">Working directory, or <c>null</c> to inherit the current one.</param>
/// <param name="Environment">Extra environment variables to set for the child process (added to the inherited block).</param>
/// <param name="TimeoutMs">Kill the process after this many milliseconds. Defaults to 10 minutes.</param>
public sealed record WorkloadProcessRequest(
    string FileName,
    string Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int TimeoutMs = 600_000);

/// <summary>
/// Runs external tools for the real-workload benchmarks, with working-directory, environment, and
/// timeout control. Returns the same <see cref="ProcessRunResult"/> shape as <see cref="IProcessRunner"/>.
/// </summary>
/// <remarks>The execution seam for benchmark tools — benchmark unit tests inject a fake so the suite
/// never launches a real process.</remarks>
public interface IWorkloadProcessRunner
{
    /// <summary>Runs the requested process to completion (or timeout) and captures its output.</summary>
    ProcessRunResult Run(WorkloadProcessRequest request, CancellationToken cancellationToken = default);
}
