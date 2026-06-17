using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Times <c>cargo build</c> of a real, Microsoft-owned Rust project — <b>microsoft/edit</b>, the Rust
/// console text editor — pinned to a tagged release, on each drive.
/// </summary>
/// <remarks>
/// <para>The fixture is the actual <c>microsoft/edit</c> source at tag <c>v2.0.0</c> (so the supply
/// chain is one we own and the result is honest about what it compiles). <see cref="PrepareCore"/> does
/// the one network step: a shallow <c>git clone</c> of the pinned tag and a <c>cargo fetch</c> that
/// warms a seed <c>CARGO_HOME</c>. Each measured iteration <b>freshly copies that warm seed
/// <c>CARGO_HOME</c> onto the drive under test</b> (a cold, just-written per-drive registry cache),
/// copies the checked-out sources (never <c>target/</c> or <c>.git</c> — cargo needs neither) onto the
/// same drive, and runs <c>cargo build --offline</c> with <c>CARGO_HOME</c> pointed at the per-drive
/// cache — so the registry cache, the compile, and the fresh <c>target/</c> all live on the one drive
/// being measured. The copy preserves crate-file timestamps (cargo fingerprints on mtime), so the build
/// is a real clean compile, not a no-op.</para>
/// <para>The project builds on <b>stable</b> Rust (the nightly <c>build-std</c> config is opt-in via
/// <c>--config</c> and is deliberately not used). The <see cref="WorkloadProfile"/> only affects the
/// honest <see cref="Detail"/> wording; the fixture is the real project either way.</para>
/// <para><b>Best-effort:</b> if git or the network is unavailable the clone or fetch fails and the
/// benchmark skips with a clear reason rather than fabricating a number.</para>
/// </remarks>
public sealed class CargoBuildWorkload : WorkloadBenchmarkBase
{
    /// <summary>The real, Microsoft-owned project this benchmark compiles.</summary>
    private const string RepoUrl = "https://github.com/microsoft/edit.git";

    /// <summary>The pinned release tag (snapped so the fixture is stable and reproducible).</summary>
    private const string RepoTag = "v2.0.0";

    /// <summary>Offline debug build of the workspace's default member (the editor binary).</summary>
    private const string BuildArguments = "build --offline -q";

    private const int CloneTimeoutMs = 180_000;
    private const int FetchTimeoutMs = 420_000;
    private const int BuildTimeoutMs = 420_000;

    private string _repoSeed = string.Empty;

    // The warm SEED CARGO_HOME, populated once (with network) in PrepareCore and never measured
    // directly: each iteration copies it cold onto the drive under test. Lives under the %TEMP% seed tree.
    private string _seedCargoHome = string.Empty;

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    public CargoBuildWorkload(IWorkloadProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    public override string Name => "cargo build";

    /// <inheritdoc />
    public override string Detail => IsThorough
        ? $"microsoft/edit (Rust console editor) {RepoTag} \u00B7 cargo build \u00B7 cold cache on the test drive \u00B7 offline (Thorough)"
        : $"microsoft/edit (Rust console editor) {RepoTag} \u00B7 cargo build \u00B7 cold cache on the test drive \u00B7 offline (Quick)";

    /// <inheritdoc />
    public override string RequiredTool => "cargo";

    /// <inheritdoc />
    protected override string Slug => "cargo-build";

    private bool IsThorough => Profile == WorkloadProfile.Thorough;

    /// <inheritdoc />
    protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        _repoSeed = Path.Combine(SeedDirectory, "edit");
        _seedCargoHome = Path.Combine(SeedDirectory, "cargo-home");
        Directory.CreateDirectory(_seedCargoHome);

        // The single network-dependent step (1/2): shallow-clone the pinned tag. A shallow clone is fine
        // for cargo (it reads the working tree + Cargo.lock, not git history). `-c core.longpaths=true` is
        // defensive: microsoft/edit has no over-MAX_PATH paths today, but the flag prevents the same
        // long-path checkout failure that bit the dotnet/reactive workload from ever masquerading as a
        // "no network" skip here. Skip gracefully when git or the network is unavailable.
        ProcessRunResult clone = Runner.Run(
            new WorkloadProcessRequest("git", $"-c core.longpaths=true clone --depth 1 --branch {RepoTag} {RepoUrl} \"{_repoSeed}\"", SeedDirectory, TimeoutMs: CloneTimeoutMs),
            cancellationToken);

        if (clone.TimedOut || clone.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"could not clone microsoft/edit {RepoTag} (no network or git unavailable)");
        }

        // The single network-dependent step (2/2): resolve + download the crate graph into the seed
        // CARGO_HOME once. Measured builds then compile offline from a cold per-drive copy of it.
        ProcessRunResult fetch = Runner.Run(
            new WorkloadProcessRequest("cargo", "fetch", _repoSeed, CargoEnvironment(_seedCargoHome), FetchTimeoutMs),
            cancellationToken);

        if (fetch.TimedOut || fetch.ExitCode != 0)
        {
            throw new WorkloadUnavailableException("no network to populate the cargo registry cache");
        }
    }

    /// <inheritdoc />
    protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
    {
        // SETUP (untimed): freshly populate the per-drive CARGO_HOME with a cold copy of the warm seed, so
        // the timed compile reads a just-written registry cache on the SAME drive under test (not on C:).
        // CopyDirectory preserves crate-file mtimes, so cargo's fingerprints stay valid and the build is a
        // genuine clean compile rather than a no-op. (We deliberately do NOT touch the cache's mtimes.)
        PopulateColdCache(_seedCargoHome, cacheDir);

        // Copy the checked-out sources (+ Cargo.lock) only — never target/ (a clean compile) or .git
        // (cargo does not need it) — so each iteration churns a fresh target/ on the drive.
        CopyDirectory(_repoSeed, workDir, skipDirectory: name => name is "target" or ".git");

        // Time only the offline compile, with CARGO_HOME pointed at the per-drive cache. The registry
        // cache, the compile, and the fresh target/ are therefore all on this drive.
        return TimeProcess(
            new WorkloadProcessRequest("cargo", BuildArguments, workDir, CargoEnvironment(cacheDir), BuildTimeoutMs),
            cancellationToken,
            "cargo build");
    }

    private static Dictionary<string, string> CargoEnvironment(string cargoHome) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CARGO_HOME"] = cargoHome,
        };
}
