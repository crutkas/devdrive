using System.Text.RegularExpressions;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Default <see cref="IInstalledToolDetector"/>. Composes an <see cref="IPathProbe"/> (resolves the
/// executable on <c>PATH</c>) and an <see cref="IProcessRunner"/> (runs a read-only version probe).
/// All classification logic lives here so it is unit-testable against fakes — no real process is
/// launched in tests.
/// </summary>
/// <remarks>
/// Composed by the app via <see cref="WorkloadBenchmarkService"/> (<see cref="CreateDefault"/>): the
/// build-benchmark suite uses it to decide which workloads can run. Detection is READ-ONLY — it only
/// resolves executables on <c>PATH</c> and runs version probes — so it changes nothing on the machine.
/// No real process is launched in tests.
/// </remarks>
public sealed class InstalledToolDetector : IInstalledToolDetector
{
    // First version-looking token: MAJOR.MINOR[.PATCH][-/+ prerelease]. Tolerant of surrounding text
    // such as "git version 2.43.0.windows.1", "v20.11.1", "Python 3.12.1", "go version go1.22.0".
    private static readonly Regex VersionRegex =
        new(@"\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.\-]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _processRunner;
    private readonly IPathProbe _pathProbe;
    private readonly IReadOnlyList<ToolProbeSpec> _catalog;

    /// <summary>Creates a detector over the supplied probes and (optional) catalogue.</summary>
    public InstalledToolDetector(IProcessRunner processRunner, IPathProbe pathProbe, IReadOnlyList<ToolProbeSpec>? catalog = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _pathProbe = pathProbe ?? throw new ArgumentNullException(nameof(pathProbe));
        _catalog = catalog ?? InstalledToolCatalog.Default;
    }

    /// <summary>Convenience factory wiring the real <see cref="ProcessRunner"/> + <see cref="PathProbe"/>.</summary>
    public static InstalledToolDetector CreateDefault() => new(new ProcessRunner(), new PathProbe());

    /// <inheritdoc />
    public IReadOnlyList<InstalledToolInfo> DetectAll(CancellationToken cancellationToken = default)
    {
        var results = new List<InstalledToolInfo>(_catalog.Count);
        foreach (ToolProbeSpec spec in _catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Probe(spec));
        }

        return results;
    }

    /// <inheritdoc />
    public InstalledToolInfo Detect(string toolName, CancellationToken cancellationToken = default)
    {
        ToolProbeSpec? spec = _catalog.FirstOrDefault(s => string.Equals(s.Name, toolName, StringComparison.OrdinalIgnoreCase));
        return spec is null
            ? new InstalledToolInfo { Name = toolName ?? string.Empty, Found = false }
            : Probe(spec);
    }

    private InstalledToolInfo Probe(ToolProbeSpec spec)
    {
        string? resolvedPath = _pathProbe.Resolve(spec.Executable);

        ProcessRunResult run = _processRunner.Run(spec.Executable, spec.VersionArguments);
        // ProcessRunner surfaces "tool not found" / timeout as a negative exit code; only parse a
        // version when the probe actually ran.
        bool launched = run is not null && run.ExitCode >= 0 && !run.TimedOut;
        string? version = launched ? ParseVersion(run!.StandardOutput, run.StandardError) : null;

        return new InstalledToolInfo
        {
            Name = spec.Name,
            Found = resolvedPath is not null || version is not null,
            Version = version,
            Path = resolvedPath,
        };
    }

    /// <summary>
    /// Extracts the first version-looking token from a tool's combined output (stdout preferred, then
    /// stderr — <c>java -version</c> writes to stderr). Returns <c>null</c> when none is found. Public so
    /// it can be unit-tested directly.
    /// </summary>
    public static string? ParseVersion(string? standardOutput, string? standardError)
    {
        foreach (string? stream in new[] { standardOutput, standardError })
        {
            if (string.IsNullOrEmpty(stream))
            {
                continue;
            }

            Match match = VersionRegex.Match(stream);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }
}
