using DevDriveCore.Abstractions;

namespace DevDriveCore.Services;

/// <summary>Where a resolved npm cache path came from (in priority order).</summary>
public enum NpmCacheSource
{
    /// <summary>The <c>npm_config_cache</c> environment variable was set.</summary>
    EnvironmentVariable,

    /// <summary>npm's own resolved value, read via <c>npm config get cache</c>.</summary>
    NpmConfig,

    /// <summary>The platform default (<c>%LocalAppData%\npm-cache</c>).</summary>
    Default,
}

/// <summary>The raw (unexpanded) npm cache path together with where it came from.</summary>
/// <param name="RawPath">The path as resolved, possibly still containing <c>%VAR%</c> tokens.</param>
/// <param name="Source">Which step of the resolution chain produced <paramref name="RawPath"/>.</param>
public readonly record struct NpmCacheResolution(string RawPath, NpmCacheSource Source);

/// <summary>
/// Resolves npm's cache directory authoritatively and <em>read-only</em>, in priority order:
/// <list type="number">
///   <item>the <c>npm_config_cache</c> environment variable, if set;</item>
///   <item><c>npm config get cache</c> — npm's own resolved value, honouring user/global npmrc;</item>
///   <item>the corrected platform default <c>%LocalAppData%\npm-cache</c>.</item>
/// </list>
/// Step 2 matters because <c>npm_config_cache</c> is frequently set only at the shell/process level
/// (so it is not inherited by this app) yet recorded in the user's npmrc — without it, a machine with
/// a relocated cache reads "Not found". Nothing here ever writes, moves, or creates anything.
/// </summary>
public sealed class NpmCacheLocator
{
    /// <summary>The environment variable npm honours to override its cache directory.</summary>
    public const string EnvironmentVariable = "npm_config_cache";

    /// <summary>The corrected platform default (npm &gt;= 5 uses LOCAL AppData, not Roaming).</summary>
    public const string DefaultTemplate = @"%LocalAppData%\npm-cache";

    // npm is launched via cmd.exe so Windows PATHEXT maps `npm` -> `npm.cmd` with npm's normal
    // environment. Launching `npm` (no extension) under UseShellExecute=false fails to find the file,
    // and launching `npm.cmd` directly is unreliable; `cmd.exe /c npm ...` is the portable form.
    internal const string ProbeFileName = "cmd.exe";
    internal const string ProbeArguments = "/c npm config get cache";

    private readonly IEnvironmentProvider _environment;
    private readonly IProcessRunner _processRunner;

    /// <summary>Creates a locator over the supplied environment + process-runner seams.</summary>
    public NpmCacheLocator(IEnvironmentProvider environment, IProcessRunner processRunner)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>
    /// Resolves the raw (unexpanded) npm cache path and its provenance. Read-only; never throws for
    /// ordinary failures (a missing/slow npm just falls through to the default).
    /// </summary>
    public NpmCacheResolution Resolve()
    {
        string? env = _environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new NpmCacheResolution(env.Trim().Trim('"'), NpmCacheSource.EnvironmentVariable);
        }

        string? configured = TryReadConfiguredCachePath();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new NpmCacheResolution(configured!, NpmCacheSource.NpmConfig);
        }

        return new NpmCacheResolution(DefaultTemplate, NpmCacheSource.Default);
    }

    private string? TryReadConfiguredCachePath()
    {
        ProcessRunResult result;
        try
        {
            result = _processRunner.Run(ProbeFileName, ProbeArguments);
        }
        catch
        {
            // A flaky probe must never break detection — degrade to the default.
            return null;
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        return ParseCachePath(result.StandardOutput);
    }

    /// <summary>
    /// Parses the first usable path line from <c>npm config get cache</c> output. Returns <c>null</c>
    /// for empty / <c>undefined</c> / <c>null</c> / non-path output (e.g. stray npm warnings). Pure.
    /// </summary>
    public static string? ParseCachePath(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return null;
        }

        foreach (string rawLine in standardOutput.Split('\n'))
        {
            string line = rawLine.Trim().Trim('"');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("undefined", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LooksLikePath(line))
            {
                return line;
            }
        }

        return null;
    }

    private static bool LooksLikePath(string value)
    {
        // Drive-rooted path, e.g. C:\... or C:/...
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            return true;
        }

        // UNC path, e.g. \\server\share
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        // An environment-variable template such as %LocalAppData%\npm-cache.
        return value.Contains('%') && (value.Contains('\\') || value.Contains('/'));
    }
}
