using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Times <c>npm ci</c> (clean install from a committed <c>package-lock.json</c>) into each drive against
/// a real, <b>Microsoft-owned</b> project — <b>microsoft/vscode-eslint</b>, the VS Code ESLint extension
/// — pinned to a tagged release, so the dependency graph is a genuine real-world one we own.
/// </summary>
/// <remarks>
/// <para>The fixture is the actual <c>microsoft/vscode-eslint</c> repo at tag <c>release/3.0.24</c>, with
/// its <b>committed</b> <c>package.json</c> + <c>package-lock.json</c> (~213 real, transitive packages) —
/// not a hand-authored manifest. <see cref="PrepareCore"/> does the network steps: a shallow
/// <c>git clone</c> of the pinned tag and an <c>npm ci</c> that warms a seed cache. Each measured
/// iteration <b>freshly copies that warm seed cache onto the drive under test</b> (a cold, just-written
/// per-drive cache), copies the repo's committed <c>package.json</c> + <c>package-lock.json</c> into a
/// fresh working directory on that same drive, and runs <c>npm ci --offline --cache &lt;per-drive
/// cache&gt;</c> — so the package cache, the install, and the resulting <c>node_modules</c> all live on
/// the one drive being measured (the honest "everything on this drive" comparison, not cache-on-C: for
/// both runs).</para>
/// <para>The <see cref="WorkloadProfile"/> only affects the honest <see cref="Detail"/> wording; the
/// real repo's committed dependency graph is installed either way.</para>
/// <para><b>Best-effort:</b> if git or the network is unavailable the clone or cache warm fails and the
/// benchmark skips with a clear reason rather than fabricating a number.</para>
/// <para><b>Quirk:</b> <c>npm</c> is a <c>.cmd</c>/<c>.ps1</c> shim, not an <c>.exe</c>, so it is
/// launched through <c>cmd.exe /c</c> (a bare <c>Process.Start("npm")</c> with
/// <c>UseShellExecute=false</c> cannot launch it).</para>
/// </remarks>
public sealed class NpmCiWorkload : WorkloadBenchmarkBase
{
    /// <summary>The real, Microsoft-owned project this benchmark installs.</summary>
    private const string RepoUrl = "https://github.com/microsoft/vscode-eslint.git";

    /// <summary>The pinned release tag (snapped so the fixture is stable and reproducible).</summary>
    private const string RepoTag = "release/3.0.24";

    private const int TimeoutMs = 240_000;

    private const int CloneTimeoutMs = 180_000;

    // The warm `npm ci` pulls the full committed graph over the network into an empty cache, so it gets
    // more headroom than the offline measured `npm ci` — matching the larger network-step budgets the
    // cargo/dotnet workloads use.
    private const int WarmInstallTimeoutMs = 420_000;

    // The cloned seed working tree; its committed package.json + package-lock.json are the template each
    // measured iteration installs from.
    private string _repoSeed = string.Empty;

    // The warm SEED cache, populated once (with network) in PrepareCore and never measured directly:
    // each iteration copies it cold onto the drive under test. It lives under the %TEMP% seed tree.
    private string _seedCache = string.Empty;

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    public NpmCiWorkload(IWorkloadProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    public override string Name => "npm ci";

    /// <inheritdoc />
    public override string Detail => Profile == WorkloadProfile.Thorough
        ? $"microsoft/vscode-eslint (VS Code ESLint extension) {RepoTag} \u00B7 npm ci \u00B7 cold cache on the test drive \u00B7 offline (Thorough)"
        : $"microsoft/vscode-eslint (VS Code ESLint extension) {RepoTag} \u00B7 npm ci \u00B7 cold cache on the test drive \u00B7 offline (Quick)";

    /// <inheritdoc />
    public override string RequiredTool => "npm";

    /// <inheritdoc />
    protected override string Slug => "npm-ci";

    /// <inheritdoc />
    protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        _repoSeed = Path.Combine(SeedDirectory, "vscode-eslint");
        _seedCache = Path.Combine(SeedDirectory, "npm-cache");
        Directory.CreateDirectory(_seedCache);

        // The single network-dependent step (1/2): shallow-clone the pinned tag. A shallow clone is fine
        // for `npm ci` (it reads the committed package.json + package-lock.json, not git history).
        // `-c core.longpaths=true` is defensive against an over-MAX_PATH checkout masquerading as a "no
        // network" skip. Skip gracefully when git or the network is unavailable.
        ProcessRunResult clone = Runner.Run(
            new WorkloadProcessRequest("git", $"-c core.longpaths=true clone --depth 1 --branch {RepoTag} {RepoUrl} \"{_repoSeed}\"", SeedDirectory, TimeoutMs: CloneTimeoutMs),
            cancellationToken);

        if (clone.TimedOut || clone.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"could not clone microsoft/vscode-eslint {RepoTag} (no network or git unavailable)");
        }

        // The repo must carry a committed lockfile — `npm ci` (unlike `npm install`) requires one and
        // never writes one. If it is missing the fixture is unusable, so skip rather than fabricate.
        if (!File.Exists(Path.Combine(_repoSeed, "package.json")) || !File.Exists(Path.Combine(_repoSeed, "package-lock.json")))
        {
            throw new WorkloadUnavailableException($"microsoft/vscode-eslint {RepoTag} did not include a committed package.json + package-lock.json");
        }

        // The single network-dependent step (2/2): a real `npm ci` that resolves + downloads the committed
        // graph into the seed cache once. Measured runs then install offline from a cold per-drive copy.
        ProcessRunResult warm = Runner.Run(
            Cmd(
                "npm ci --ignore-scripts --no-audit --no-fund --fetch-retries=1 --fetch-retry-mintimeout=1000 " +
                $"--fetch-timeout=120000 --cache \"{_seedCache}\"",
                _repoSeed,
                WarmInstallTimeoutMs),
            cancellationToken);

        if (warm.TimedOut || warm.ExitCode != 0)
        {
            throw new WorkloadUnavailableException("no network to populate the npm package cache");
        }

        // Keep only the committed package.json + package-lock.json as the template; node_modules is
        // recreated per measured run on the drive under test.
        DeleteTree(Path.Combine(_repoSeed, "node_modules"));
    }

    /// <inheritdoc />
    protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
    {
        // SETUP (untimed): freshly populate the per-drive cache with a cold copy of the warm seed, so the
        // timed install reads a just-written cache on the SAME drive under test (not a cache on C:).
        PopulateColdCache(_seedCache, cacheDir);

        File.Copy(Path.Combine(_repoSeed, "package.json"), Path.Combine(workDir, "package.json"), overwrite: true);
        File.Copy(Path.Combine(_repoSeed, "package-lock.json"), Path.Combine(workDir, "package-lock.json"), overwrite: true);

        // Time only the offline install, with npm's cache pointed at the per-drive cache. `--ignore-scripts`
        // skips lifecycle scripts so we time the package install itself (the filesystem-bound work a Dev
        // Drive accelerates) — vscode-eslint's `postinstall` builds sub-packages and needs the network, so
        // it both can't run offline and would add unrelated CPU. The cache, the install, and the resulting
        // node_modules are all on this drive.
        return TimeProcess(
            Cmd($"npm ci --offline --ignore-scripts --no-audit --no-fund --cache \"{cacheDir}\"", workDir),
            cancellationToken,
            "npm ci");
    }

    private static WorkloadProcessRequest Cmd(string commandLine, string workingDirectory, int timeoutMs = TimeoutMs) =>
        new("cmd.exe", $"/c {commandLine}", workingDirectory, TimeoutMs: timeoutMs);
}
