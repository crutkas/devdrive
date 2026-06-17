using System.Globalization;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Times <c>dotnet build</c> of a real, Microsoft-owned .NET project — <b>dotnet/reactive</b>
/// (System.Reactive / Rx.NET) — pinned to a tagged release and built for a single modern TFM, on each
/// drive.
/// </summary>
/// <remarks>
/// <para>The fixture is the actual <c>System.Reactive</c> library at tag <c>rxnet-v6.1.0</c> (the
/// latest stable), built for <c>net6.0</c> only. <see cref="PrepareCore"/> does the one network step: a
/// <b>full</b> <c>git clone</c> of the pinned tag and a <c>dotnet restore</c> that warms a seed
/// <c>NUGET_PACKAGES</c> folder. Each measured iteration <b>freshly copies that warm seed cache onto the
/// drive under test</b> (a cold, just-written per-drive NuGet cache), copies the source onto the same
/// drive, re-restores <em>offline</em> against the per-drive cache (untimed setup, see below), then
/// <b>times</b> <c>dotnet build … -f net6.0 -c Release --no-restore</c> — so the package cache, the
/// build's package reads, and the <c>obj</c>/<c>bin</c> it churns out all live on the one drive being
/// measured (the honest "everything on this drive" comparison, not cache-on-C: for both runs).</para>
/// <para><b>Why an untimed re-restore:</b> <c>dotnet build --no-restore</c> reads packages from the
/// absolute global-packages path baked into <c>obj</c> at restore time — setting <c>NUGET_PACKAGES</c>
/// at build time does <em>not</em> re-point those reads. So the seed's <c>obj</c> is deliberately
/// <em>not</em> copied; instead each iteration runs a fast, fully-offline <c>dotnet restore … --source
/// &lt;empty&gt;</c> that resolves from the just-copied per-drive cache and regenerates <c>obj</c>
/// pointing at it. That restore is <b>setup, not measured</b>; only the subsequent compile is timed.</para>
/// <para><b>Why a full clone:</b> this repo versions itself with Nerdbank.GitVersioning, which throws on
/// a <em>shallow</em> clone. The clone therefore omits <c>--depth</c>, and the per-iteration copy keeps
/// <c>.git</c> so NBGV can compute the version during both the re-restore and the build; only <c>bin</c>
/// and <c>obj</c> are skipped so each iteration is a genuine clean restore + compile.</para>
/// <para>Only the single <c>net6.0</c> TFM is built — the repo's full multi-TFM build (net472, uap, …)
/// is fragile, so the benchmark deliberately pins one modern, reliable target. The
/// <see cref="WorkloadProfile"/> only affects the honest <see cref="Detail"/> wording; the fixture is
/// the real project either way.</para>
/// <para><b>Best-effort:</b> if git or the network is unavailable the clone or restore fails and the
/// benchmark skips with a clear reason rather than fabricating a number.</para>
/// </remarks>
public sealed class DotnetBuildWorkload : WorkloadBenchmarkBase
{
    /// <summary>The real, Microsoft-owned project this benchmark builds.</summary>
    private const string RepoUrl = "https://github.com/dotnet/reactive.git";

    /// <summary>The pinned latest-stable release tag (NOT the v7 previews or the ix-net tags).</summary>
    private const string RepoTag = "rxnet-v6.1.0";

    /// <summary>The single project built, relative to the repo root.</summary>
    private const string ProjectRelativePath = @"Rx.NET\Source\src\System.Reactive\System.Reactive.csproj";

    /// <summary>The single modern TFM built (the repo's full multi-TFM build is fragile).</summary>
    private const string TargetFramework = "net6.0";

    private const int CloneTimeoutMs = 300_000;
    private const int RestoreTimeoutMs = 300_000;
    private const int BuildTimeoutMs = 300_000;

    private string _repoSeed = string.Empty;

    // The warm SEED global-packages folder, populated once (with network) in PrepareCore and never
    // measured directly: each iteration copies it cold onto the drive under test. Lives under %TEMP%.
    private string _seedCache = string.Empty;

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    public DotnetBuildWorkload(IWorkloadProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    public override string Name => "dotnet build";

    /// <inheritdoc />
    public override string Detail => IsThorough
        ? $"dotnet/reactive (System.Reactive) {RepoTag} \u00B7 {TargetFramework} \u00B7 cold cache on the test drive \u00B7 offline (Thorough)"
        : $"dotnet/reactive (System.Reactive) {RepoTag} \u00B7 {TargetFramework} \u00B7 cold cache on the test drive \u00B7 offline (Quick)";

    /// <inheritdoc />
    public override string RequiredTool => "dotnet";

    /// <inheritdoc />
    protected override string Slug => "dotnet-build";

    /// <summary>A full reactive clone (~120&#160;MB working tree + ~46&#160;MB <c>.git</c>) plus per-iteration build copies; require a little more headroom than the default.</summary>
    protected override long MinFreeBytesPerDrive => 4L * 1024 * 1024 * 1024;

    private bool IsThorough => Profile == WorkloadProfile.Thorough;

    /// <inheritdoc />
    protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        _repoSeed = Path.Combine(SeedDirectory, "reactive");
        _seedCache = Path.Combine(SeedDirectory, "nuget");
        Directory.CreateDirectory(_seedCache);

        // The single network-dependent step (1/2): a FULL clone of the pinned tag. NOT shallow —
        // Nerdbank.GitVersioning (this repo's versioning) throws on a shallow clone, so the full history
        // is required. `-c core.longpaths=true` is REQUIRED on Windows: reactive ships UWP DeviceRunner
        // test artifacts whose paths exceed MAX_PATH (260), so without it `git checkout` aborts
        // ("Filename too long", exit 128) AFTER the objects fetched fine — which previously surfaced as a
        // bogus "no network" skip on machines whose git lacks core.longpaths. Skip gracefully (reporting
        // the REAL reason) when git/network is genuinely unavailable.
        ProcessRunResult clone = Runner.Run(
            new WorkloadProcessRequest("git", $"-c core.longpaths=true clone --branch {RepoTag} {RepoUrl} \"{_repoSeed}\"", SeedDirectory, TimeoutMs: CloneTimeoutMs),
            cancellationToken);

        if (clone.TimedOut || clone.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"could not clone dotnet/reactive {RepoTag}: {DescribeProcessFailure(clone, CloneTimeoutMs)}");
        }

        // The single network-dependent step (2/2): warm a seed global-packages folder once. Measured
        // iterations copy this cold onto the test drive and re-restore offline from the copy. Fail fast
        // (skip) when there is no network.
        string projectPath = Path.Combine(_repoSeed, ProjectRelativePath);
        ProcessRunResult restore = Runner.Run(
            new WorkloadProcessRequest("dotnet", $"restore \"{projectPath}\" --nologo", _repoSeed, NugetEnvironment(_seedCache), RestoreTimeoutMs),
            cancellationToken);

        if (restore.TimedOut || restore.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"could not restore NuGet packages for dotnet/reactive: {DescribeProcessFailure(restore, RestoreTimeoutMs)}");
        }
    }

    /// <summary>
    /// Builds a concise, HONEST skip reason from a failed process result — a timeout, or the last
    /// meaningful stderr/stdout line — instead of guessing "no network" (which mis-attributed real
    /// failures such as the long-path checkout error above).
    /// </summary>
    private static string DescribeProcessFailure(ProcessRunResult result, int timeoutMs)
    {
        if (result.TimedOut)
        {
            return $"timed out after {timeoutMs / 1000}s";
        }

        string detail = LastMeaningfulLine(result.StandardError);
        if (string.IsNullOrEmpty(detail))
        {
            detail = LastMeaningfulLine(result.StandardOutput);
        }

        return string.IsNullOrEmpty(detail) ? $"exit {result.ExitCode}" : $"exit {result.ExitCode} — {detail}";
    }

    /// <summary>Returns the last non-blank line of <paramref name="text"/>, trimmed and length-capped.</summary>
    private static string LastMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 0) ?? string.Empty;

        const int max = 200;
        return line.Length <= max ? line : string.Concat(line.AsSpan(0, max), "\u2026");
    }

    /// <inheritdoc />
    protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
    {
        // SETUP (untimed) 1/3: freshly populate the per-drive NuGet cache with a cold copy of the warm
        // seed global-packages folder, so the timed build's package reads land on the SAME drive under
        // test (not a cache on C:).
        PopulateColdCache(_seedCache, cacheDir);

        // SETUP (untimed) 2/3: copy the source onto the drive, keeping .git (NBGV computes the version
        // from it). Skip bin AND obj: obj is regenerated by the offline re-restore below so its baked
        // global-packages paths point at the per-drive cache — copying the seed's obj would instead leave
        // the build reading packages from the seed cache on C: (dotnet build --no-restore resolves from
        // the absolute path baked into obj, which NUGET_PACKAGES does not override at build time).
        CopyDirectory(_repoSeed, workDir, skipDirectory: name => name is "bin" or "obj");

        string projectPath = Path.Combine(workDir, ProjectRelativePath);
        IReadOnlyDictionary<string, string> nugetEnvironment = NugetEnvironment(cacheDir);

        // SETUP (untimed) 3/3: re-restore OFFLINE against the per-drive cache so obj is regenerated
        // pointing at it. `--source <empty>` overrides all feeds, guaranteeing no network — every package
        // is resolved from the just-copied per-drive global-packages folder. This is fast (a no-op-style
        // resolve over an already-populated folder) and is deliberately NOT timed.
        string offlineFeed = Path.Combine(workDir, "_offline-feed");
        Directory.CreateDirectory(offlineFeed);
        RunChecked(
            new WorkloadProcessRequest(
                "dotnet",
                $"restore \"{projectPath}\" --nologo --source \"{offlineFeed}\"",
                workDir,
                nugetEnvironment,
                RestoreTimeoutMs),
            cancellationToken,
            "dotnet restore (offline, per-drive cache)");

        // Time only the single net6.0 TFM compile, resolving from the per-drive cache. The package cache,
        // the build's reads, and the obj/bin it churns out are therefore all on this drive.
        return TimeProcess(
            new WorkloadProcessRequest(
                "dotnet",
                $"build \"{projectPath}\" -f {TargetFramework} -c Release --no-restore --nologo",
                workDir,
                nugetEnvironment,
                BuildTimeoutMs),
            cancellationToken,
            "dotnet build");
    }

    /// <summary>Parses the SDK version ("10.0.100") into a TFM ("net10.0"). Returns <c>null</c> when unparseable.</summary>
    /// <remarks>
    /// Retained as a reusable, tested pure helper (the TFM this benchmark builds is fixed to
    /// <see cref="TargetFramework"/>); see the pre-flight unit tests.
    /// </remarks>
    public static string? ParseTargetFramework(string? dotnetVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(dotnetVersionOutput))
        {
            return null;
        }

        string first = dotnetVersionOutput.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        // Strip any leading non-digits (e.g. a stray "v").
        first = new string(first.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major) && major >= 5
            ? $"net{major}.0"
            : null;
    }

    private static Dictionary<string, string> NugetEnvironment(string packagesDir)
    {
        // C7: redirect EVERY NuGet/dotnet cache under the supplied packages dir, not just the global
        // packages folder. Otherwise NuGet's HTTP cache, plugins cache, scratch, and the .NET CLI home
        // still default to the real user profile — leaving artifacts behind and polluting real caches.
        // For the seed restore this is the %TEMP% seed tree; for a measured iteration it is the per-drive
        // cache (so all NuGet/dotnet I/O stays on the drive under test). Both are tracked and deleted in
        // Cleanup. The tools assume these directories exist, so create them up-front (idempotent).
        string httpCache = Path.Combine(packagesDir, "http-cache");
        string pluginsCache = Path.Combine(packagesDir, "plugins-cache");
        string scratch = Path.Combine(packagesDir, "scratch");
        string cliHome = Path.Combine(packagesDir, "cli-home");
        Directory.CreateDirectory(httpCache);
        Directory.CreateDirectory(pluginsCache);
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(cliHome);

        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = packagesDir,
            ["NUGET_HTTP_CACHE_PATH"] = httpCache,
            ["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache,
            ["NUGET_SCRATCH"] = scratch,
            ["DOTNET_CLI_HOME"] = cliHome,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };
    }
}
