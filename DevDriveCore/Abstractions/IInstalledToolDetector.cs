using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Detects which developer tools are installed, and at what version, using read-only probes
/// (a <c>--version</c>-style invocation through <see cref="IProcessRunner"/> and a <c>PATH</c>
/// lookup through <see cref="IPathProbe"/>).
/// </summary>
/// <remarks>
/// <para>This engine is <b>read-only</b>: it launches each tool only to print its version. It is
/// composed by the app via <see cref="DevDriveCore.Services.WorkloadBenchmarkService"/> to gate which
/// build benchmarks can run, and changes nothing on the machine.</para>
/// <para>Unit tests inject a fake <see cref="IProcessRunner"/> / <see cref="IPathProbe"/>, so the
/// suite never launches a real process.</para>
/// </remarks>
public interface IInstalledToolDetector
{
    /// <summary>Probes every tool in the catalogue and returns one <see cref="InstalledToolInfo"/> per tool.</summary>
    IReadOnlyList<InstalledToolInfo> DetectAll(CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes a single tool by its catalogue display name (case-insensitive). Returns a "not found"
    /// <see cref="InstalledToolInfo"/> when the name is not in the catalogue.
    /// </summary>
    InstalledToolInfo Detect(string toolName, CancellationToken cancellationToken = default);
}
