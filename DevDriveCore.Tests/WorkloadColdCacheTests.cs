using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>
/// Locks in the FAIR, symmetric cold first-build protocol: every build workload must put its package
/// cache on the SAME drive it is testing (the per-drive <c>cacheDir</c>), freshly re-populated cold from
/// the warm seed each iteration — never a shared cache on C:. These tests fake the process runner (no
/// real npm/dotnet/cargo/git is launched) but DO touch a throwaway temp directory that stands in for the
/// "drive under test", so they verify the real <c>MeasureCore</c> wiring end to end. They go
/// <see cref="Assert.Inconclusive(string)"/> (rather than failing) when the temp volume can't host the run.
/// </summary>
[TestClass]
public sealed class WorkloadColdCacheTests
{
    // Mirrors the internal WorkloadPaths.BenchFolderName (not visible to the test assembly).
    private const string BenchFolderName = "DevDriveManagerWorkloadBench";

    // The project path the dotnet workload builds, relative to the repo root (mirrors the workload).
    private const string DotnetProjectRelativePath =
        @"Rx.NET\Source\src\System.Reactive\System.Reactive.csproj";

    [TestMethod]
    public void Harness_RePopulatesPerDriveCacheCold_OnTestDrive_EachIteration()
    {
        var workload = new FakeColdCacheWorkload(new RecordingWorkloadRunner());
        string root = CreateTempRoot();
        string seedRoot = Path.Combine(root, "seed");
        try
        {
            workload.Prepare(new WorkloadEnvironment(root, root, seedRoot), CancellationToken.None);

            workload.MeasureOnce(root, CancellationToken.None);
            workload.MeasureOnce(root, CancellationToken.None);

            string expectedCache = Path.Combine(root, BenchFolderName, "fake-cold", "cache");

            // (1) The per-drive cache lives on the drive under test, under the bench folder — not on C:.
            CollectionAssert.AreEqual(
                new[] { expectedCache, expectedCache },
                workload.ObservedCacheDirs.ToArray());

            // (2) The warm seed was copied into the per-drive cache on EVERY iteration (a real cold copy).
            CollectionAssert.AreEqual(new[] { true, true }, workload.SeedPresentAfterPopulate.ToArray());

            // (3) Each iteration is COLD: iteration 2 starts with iteration 1's leftover present, and the
            //     re-populate RESETS the directory so that leftover is gone before the (timed) work runs.
            CollectionAssert.AreEqual(new[] { false, true }, workload.LeftoverBeforePopulate.ToArray());
            CollectionAssert.AreEqual(new[] { false, false }, workload.LeftoverAfterPopulate.ToArray());

            // Cleanup removes the per-drive bench folder wholesale (no leak).
            workload.Cleanup();
            Assert.IsFalse(Directory.Exists(Path.Combine(root, BenchFolderName)));
        }
        catch (WorkloadUnavailableException ex)
        {
            Assert.Inconclusive($"temp volume cannot host the run: {ex.Message}");
        }
        finally
        {
            workload.Cleanup();
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void Npm_PointsCacheAtPerDriveDirOnTestDrive_AndPopulatesItCold()
    {
        var runner = new RecordingWorkloadRunner();
        var npm = new NpmCiWorkload(runner) { Profile = WorkloadProfile.Quick };
        string root = CreateTempRoot();
        string seedRoot = Path.Combine(root, "seed");
        try
        {
            npm.Prepare(new WorkloadEnvironment(root, root, seedRoot), CancellationToken.None);
            double seconds = npm.MeasureOnce(root, CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(0.0, seconds);

            string expectedCache = Path.Combine(root, BenchFolderName, "npm-ci", "cache");

            // The TIMED `npm ci` install points npm's cache at the per-drive cache (on the test drive),
            // and runs offline — so cache + install + node_modules are all on the one drive. (The warm
            // `npm ci` in PrepareCore is online, so disambiguate by --offline.)
            WorkloadProcessRequest ci = runner.Requests.Single(
                r => r.FileName == "cmd.exe" && r.Arguments.Contains("npm ci") && r.Arguments.Contains("--offline"));
            StringAssert.Contains(ci.Arguments, "--offline");
            StringAssert.Contains(ci.Arguments, "--ignore-scripts"); // time the install, not vscode-eslint's postinstall build
            string? ciCache = QuotedAfter(ci.Arguments, "--cache");
            Assert.IsNotNull(ciCache);
            Assert.AreEqual(expectedCache, ciCache, ignoreCase: true);

            // The per-drive cache really received a cold copy of the warm seed.
            Assert.IsTrue(
                File.Exists(Path.Combine(expectedCache, RecordingWorkloadRunner.SeedMarker)),
                "the warm seed cache must be copied cold onto the per-drive cache");
        }
        catch (WorkloadUnavailableException ex)
        {
            Assert.Inconclusive($"npm cold-cache test could not run: {ex.Message}");
        }
        finally
        {
            npm.Cleanup();
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void Cargo_PointsCargoHomeAtPerDriveDirOnTestDrive_AndPopulatesItCold()
    {
        var runner = new RecordingWorkloadRunner();
        var cargo = new CargoBuildWorkload(runner) { Profile = WorkloadProfile.Quick };
        string root = CreateTempRoot();
        string seedRoot = Path.Combine(root, "seed");
        try
        {
            cargo.Prepare(new WorkloadEnvironment(root, root, seedRoot), CancellationToken.None);
            double seconds = cargo.MeasureOnce(root, CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(0.0, seconds);

            string expectedCache = Path.Combine(root, BenchFolderName, "cargo-build", "cache");

            // The TIMED `cargo build --offline` points CARGO_HOME at the per-drive cache (on the test drive).
            WorkloadProcessRequest build = runner.Requests.Single(
                r => r.FileName == "cargo" && r.Arguments.StartsWith("build", StringComparison.Ordinal));
            StringAssert.Contains(build.Arguments, "--offline");
            Assert.IsNotNull(build.Environment);
            Assert.IsTrue(build.Environment!.TryGetValue("CARGO_HOME", out string? cargoHome));
            Assert.AreEqual(expectedCache, cargoHome, ignoreCase: true);

            Assert.IsTrue(
                File.Exists(Path.Combine(expectedCache, RecordingWorkloadRunner.SeedMarker)),
                "the warm seed CARGO_HOME must be copied cold onto the per-drive cache");
        }
        catch (WorkloadUnavailableException ex)
        {
            Assert.Inconclusive($"cargo cold-cache test could not run: {ex.Message}");
        }
        finally
        {
            cargo.Cleanup();
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void Dotnet_ReRestoresOfflineBeforeTimedBuild_AllOnTestDrive()
    {
        var runner = new RecordingWorkloadRunner();
        var dotnet = new DotnetBuildWorkload(runner) { Profile = WorkloadProfile.Quick };
        string root = CreateTempRoot();
        string seedRoot = Path.Combine(root, "seed");
        try
        {
            dotnet.Prepare(new WorkloadEnvironment(root, root, seedRoot), CancellationToken.None);
            double seconds = dotnet.MeasureOnce(root, CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(0.0, seconds);

            string expectedCache = Path.Combine(root, BenchFolderName, "dotnet-build", "cache");

            // The TIMED build resolves NuGet from the per-drive cache and does NOT restore (it is timed).
            WorkloadProcessRequest build = runner.Requests.Single(
                r => r.FileName == "dotnet" && r.Arguments.StartsWith("build", StringComparison.Ordinal));
            StringAssert.Contains(build.Arguments, "--no-restore");
            Assert.IsTrue(build.Environment!.TryGetValue("NUGET_PACKAGES", out string? buildPackages));
            Assert.AreEqual(expectedCache, buildPackages, ignoreCase: true);

            // An OFFLINE re-restore (against an empty `--source`, on the per-drive cache) must precede the
            // timed build — that is what re-points obj at the per-drive cache so the build reads from it.
            WorkloadProcessRequest offlineRestore = runner.Requests.Single(
                r => r.FileName == "dotnet"
                     && r.Arguments.StartsWith("restore", StringComparison.Ordinal)
                     && r.Arguments.Contains("--source"));
            Assert.IsTrue(offlineRestore.Environment!.TryGetValue("NUGET_PACKAGES", out string? restorePackages));
            Assert.AreEqual(expectedCache, restorePackages, ignoreCase: true);

            int restoreIndex = runner.Requests.IndexOf(offlineRestore);
            int buildIndex = runner.Requests.IndexOf(build);
            Assert.IsTrue(
                restoreIndex >= 0 && restoreIndex < buildIndex,
                "the offline re-restore must run before the timed build");

            Assert.IsTrue(
                File.Exists(Path.Combine(expectedCache, RecordingWorkloadRunner.SeedMarker)),
                "the warm seed NuGet cache must be copied cold onto the per-drive cache");
        }
        catch (WorkloadUnavailableException ex)
        {
            Assert.Inconclusive($"dotnet cold-cache test could not run: {ex.Message}");
        }
        finally
        {
            dotnet.Cleanup();
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void Cleanup_PreexistingForeignBenchDir_IsNotDeletedWholesale()
    {
        // F9: a predictable per-drive bench <slug> dir that already holds the user's OWN data and carries
        // NO app ownership marker must NEVER be recursively deleted — only the sub-dirs the app creates
        // under it are cleaned up, and the dir itself is dropped only if it ends up empty.
        var workload = new FakeColdCacheWorkload(new RecordingWorkloadRunner());
        string root = CreateTempRoot();
        string seedRoot = Path.Combine(root, "seed");
        string foreignBenchDir = Path.Combine(root, BenchFolderName, "fake-cold");
        string foreignFile = Path.Combine(foreignBenchDir, "user-data.txt");
        Directory.CreateDirectory(foreignBenchDir);
        File.WriteAllText(foreignFile, "precious");
        try
        {
            workload.Prepare(new WorkloadEnvironment(root, root, seedRoot), CancellationToken.None);
            workload.MeasureOnce(root, CancellationToken.None);

            string ourCache = Path.Combine(foreignBenchDir, "cache");
            Assert.IsTrue(Directory.Exists(ourCache), "The app creates its own cache subdir under the bench dir.");

            workload.Cleanup();

            // The foreign bench dir and the user's file survive (never recursively deleted)...
            Assert.IsTrue(File.Exists(foreignFile), "F9: a pre-existing bench dir without our marker must not be deleted.");
            Assert.IsTrue(Directory.Exists(foreignBenchDir));
            // ...but the app's own cache subdir under it is cleaned up.
            Assert.IsFalse(Directory.Exists(ourCache), "The app's own cache subdir should still be removed.");
        }
        catch (WorkloadUnavailableException ex)
        {
            Assert.Inconclusive($"temp volume cannot host the run: {ex.Message}");
        }
        finally
        {
            workload.Cleanup();
            DeleteTempRoot(root);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Extracts the quoted argument that follows <paramref name="token"/> (e.g. <c>--cache "X"</c>).</summary>
    private static string? QuotedAfter(string args, string token)
    {
        int i = args.IndexOf(token, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        int q1 = args.IndexOf('"', i + token.Length);
        if (q1 < 0)
        {
            return null;
        }

        int q2 = args.IndexOf('"', q1 + 1);
        return q2 < 0 ? null : args.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-cold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of a throwaway temp dir.
        }
    }

    /// <summary>
    /// Tool-free <see cref="WorkloadBenchmarkBase"/> subclass that exercises the base harness +
    /// <c>PopulateColdCache</c> wiring without launching any process. It records the per-drive cache
    /// directory it is handed, whether the warm seed was present after the cold (re)populate, and whether
    /// a previous iteration's leftover survived (it must not — each iteration resets the cache cold).
    /// </summary>
    private sealed class FakeColdCacheWorkload : WorkloadBenchmarkBase
    {
        private const string SeedFileName = "seed-package.txt";
        private const string LeftoverFileName = "leftover-from-previous-iteration.txt";

        private string _seedCache = string.Empty;

        public FakeColdCacheWorkload(IWorkloadProcessRunner runner) : base(runner) { }

        public override string Name => "fake-cold";

        public override string Detail => "fake-cold";

        public override string RequiredTool => "fake";

        protected override string Slug => "fake-cold";

        public List<string> ObservedCacheDirs { get; } = new();

        public List<bool> SeedPresentAfterPopulate { get; } = new();

        public List<bool> LeftoverBeforePopulate { get; } = new();

        public List<bool> LeftoverAfterPopulate { get; } = new();

        protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
        {
            _seedCache = Path.Combine(SeedDirectory, "seed-cache");
            Directory.CreateDirectory(_seedCache);
            File.WriteAllText(Path.Combine(_seedCache, SeedFileName), "seed");
        }

        protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
        {
            string leftover = Path.Combine(cacheDir, LeftoverFileName);
            LeftoverBeforePopulate.Add(File.Exists(leftover));

            // The real workloads call this first thing — a cold copy of the warm seed onto the test drive.
            PopulateColdCache(_seedCache, cacheDir);

            LeftoverAfterPopulate.Add(File.Exists(leftover));
            SeedPresentAfterPopulate.Add(File.Exists(Path.Combine(cacheDir, SeedFileName)));
            ObservedCacheDirs.Add(cacheDir);

            // Dirty the per-drive cache so the NEXT iteration can prove the reset made it cold again.
            File.WriteAllText(leftover, "dirty");
            return 0.01;
        }
    }

    /// <summary>
    /// Recording <see cref="IWorkloadProcessRunner"/> that never launches a real process but performs the
    /// minimal filesystem side effects each workload's <c>PrepareCore</c> expects (materialize the cloned
    /// repo / lockfile, and drop a <see cref="SeedMarker"/> into whatever cache the seeding step warms),
    /// so the real <c>MeasureCore</c> can run end to end against a temp "drive". Every request is recorded
    /// in order for assertions.
    /// </summary>
    private sealed class RecordingWorkloadRunner : IWorkloadProcessRunner
    {
        /// <summary>Sentinel dropped into each warmed seed cache to prove the cold copy carried real content.</summary>
        public const string SeedMarker = "_seed-marker.txt";

        public List<WorkloadProcessRequest> Requests { get; } = new();

        public ProcessRunResult Run(WorkloadProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            string args = request.Arguments;

            // git clone: materialize the seed working tree at the (last-quoted) destination.
            if (string.Equals(request.FileName, "git", StringComparison.OrdinalIgnoreCase)
                && args.Contains("clone", StringComparison.Ordinal))
            {
                string? destination = LastQuoted(args);
                if (destination is not null)
                {
                    MaterializeClone(destination, args);
                }

                return Ok();
            }

            // npm: the warm `npm ci` (PrepareCore) installs the committed graph into the seed cache; the
            // measured `npm ci --offline` reads that cache. Only the online warm step warms the seed marker.
            if (string.Equals(request.FileName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Contains("npm ci", StringComparison.Ordinal) && !args.Contains("--offline", StringComparison.Ordinal))
                {
                    WriteMarker(QuotedAfter(args, "--cache"));
                }

                return Ok();
            }

            // cargo: `fetch` (PrepareCore) warms the seed CARGO_HOME; `build` is timed.
            if (string.Equals(request.FileName, "cargo", StringComparison.OrdinalIgnoreCase))
            {
                if (args.StartsWith("fetch", StringComparison.Ordinal))
                {
                    WriteMarker(EnvValue(request, "CARGO_HOME"));
                }

                return Ok();
            }

            // dotnet: `restore` warms NUGET_PACKAGES (both the seed restore and the per-drive re-restore);
            // `build` is timed.
            if (string.Equals(request.FileName, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                if (args.StartsWith("restore", StringComparison.Ordinal))
                {
                    WriteMarker(EnvValue(request, "NUGET_PACKAGES"));
                }

                return Ok();
            }

            return Ok();
        }

        private static void MaterializeClone(string destination, string args)
        {
            Directory.CreateDirectory(destination);

            if (args.Contains("dotnet/reactive", StringComparison.OrdinalIgnoreCase))
            {
                string projectDir = Path.Combine(destination, "Rx.NET", "Source", "src", "System.Reactive");
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, "System.Reactive.csproj"), "<Project />");
                File.WriteAllText(Path.Combine(projectDir, "Observable.cs"), "// source");

                // .git is kept by the copy (NBGV needs it); bin/obj must be skipped by MeasureCore.
                WriteInto(Path.Combine(destination, ".git"), "HEAD", "ref: refs/heads/main");
                WriteInto(Path.Combine(projectDir, "obj"), "project.assets.json", "{ \"stale\": true }");
                WriteInto(Path.Combine(projectDir, "bin"), "stale.dll", "stale");
            }
            else if (args.Contains("vscode-eslint", StringComparison.OrdinalIgnoreCase))
            {
                // microsoft/vscode-eslint (npm): the committed manifest + lockfile that `npm ci` reads.
                File.WriteAllText(Path.Combine(destination, "package.json"), "{ \"name\": \"vscode-eslint\" }");
                File.WriteAllText(Path.Combine(destination, "package-lock.json"), "{}");
                WriteInto(Path.Combine(destination, ".git"), "HEAD", "ref: refs/heads/main");
            }
            else
            {
                // microsoft/edit (cargo): a minimal Rust working tree, plus target/.git that must be skipped.
                File.WriteAllText(Path.Combine(destination, "Cargo.toml"), "[package]\nname = \"edit\"");
                File.WriteAllText(Path.Combine(destination, "Cargo.lock"), "# lock");
                WriteInto(Path.Combine(destination, "src"), "main.rs", "fn main() {}");
                WriteInto(Path.Combine(destination, ".git"), "HEAD", "ref: refs/heads/main");
                WriteInto(Path.Combine(destination, "target"), "stale.bin", "stale");
            }
        }

        private static void WriteInto(string directory, string fileName, string contents)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), contents);
        }

        private static void WriteMarker(string? cacheDir)
        {
            if (string.IsNullOrEmpty(cacheDir))
            {
                return;
            }

            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(Path.Combine(cacheDir, SeedMarker), "marker");
        }

        private static string? EnvValue(WorkloadProcessRequest request, string name) =>
            request.Environment is not null && request.Environment.TryGetValue(name, out string? value) ? value : null;

        private static string? LastQuoted(string args)
        {
            int q2 = args.LastIndexOf('"');
            if (q2 <= 0)
            {
                return null;
            }

            int q1 = args.LastIndexOf('"', q2 - 1);
            return q1 < 0 ? null : args.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static ProcessRunResult Ok() => new(0, string.Empty, string.Empty);
    }
}
