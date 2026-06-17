using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for npm cache-path detection (Task B): the (1) <c>npm_config_cache</c> env var →
/// (2) <c>npm config get cache</c> → (3) corrected <c>%LocalAppData%\npm-cache</c> resolution chain,
/// both at the <see cref="NpmCacheLocator"/> level and end-to-end through <see cref="PackageCacheService"/>.
/// Everything uses fakes — NO real npm/process is launched.
/// </summary>
[TestClass]
public sealed class NpmCacheLocatorTests
{
    private const string CmdFile = "cmd.exe";

    private static FakeEnvironmentProvider StandardEnvironment() => new(new Dictionary<string, string>
    {
        ["UserProfile"] = @"C:\Users\test",
        ["AppData"] = @"C:\Users\test\AppData\Roaming",
        ["LocalAppData"] = @"C:\Users\test\AppData\Local",
    });

    private static PackageCacheInfo Npm(IReadOnlyList<PackageCacheInfo> caches) =>
        caches.Single(c => c.Name == "npm");

    // ---- NpmCacheLocator.Resolve: the three-step chain ---------------------------------------

    [TestMethod]
    public void Resolve_EnvironmentVariableSet_TakesPriorityAndSkipsNpm()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", @"D:\shell\npm");
        var runner = new RecordingProcessRunner();
        var locator = new NpmCacheLocator(env, runner);

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(@"D:\shell\npm", result.RawPath);
        Assert.AreEqual(NpmCacheSource.EnvironmentVariable, result.Source);
        Assert.AreEqual(0, runner.CallCount, "npm must NOT be launched when the env var is set.");
    }

    [TestMethod]
    public void Resolve_EnvironmentVariableSet_IsTrimmedAndUnquoted()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", "  \"C:\\.tools\\.npm\"  ");
        var locator = new NpmCacheLocator(env, new RecordingProcessRunner());

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(@"C:\.tools\.npm", result.RawPath);
        Assert.AreEqual(NpmCacheSource.EnvironmentVariable, result.Source);
    }

    [TestMethod]
    public void Resolve_EnvUnset_ReadsNpmConfigGetCache()
    {
        // Mirrors this machine: env var only at shell/process scope (not inherited), but npmrc records
        // a relocated cache that `npm config get cache` reports.
        FakeEnvironmentProvider env = StandardEnvironment(); // npm_config_cache NOT set
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "C:\\.tools\\.npm\r\n");
        var locator = new NpmCacheLocator(env, runner);

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(@"C:\.tools\.npm", result.RawPath);
        Assert.AreEqual(NpmCacheSource.NpmConfig, result.Source);
    }

    [TestMethod]
    public void Resolve_EnvUnset_NpmConfigFails_FallsBackToCorrectedDefault()
    {
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "", stderr: "'npm' is not recognized", exitCode: 1);
        var locator = new NpmCacheLocator(StandardEnvironment(), runner);

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(NpmCacheLocator.DefaultTemplate, result.RawPath);
        Assert.AreEqual(@"%LocalAppData%\npm-cache", result.RawPath);
        Assert.AreEqual(NpmCacheSource.Default, result.Source);
    }

    [TestMethod]
    public void Resolve_EnvUnset_NpmConfigTimedOut_FallsBackToCorrectedDefault()
    {
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "C:\\.tools\\.npm", exitCode: -1, timedOut: true);
        var locator = new NpmCacheLocator(StandardEnvironment(), runner);

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(NpmCacheSource.Default, result.Source);
        Assert.AreEqual(@"%LocalAppData%\npm-cache", result.RawPath);
    }

    [TestMethod]
    public void Resolve_EnvUnset_NpmConfigEmptyOutput_FallsBackToCorrectedDefault()
    {
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "   \r\n");
        var locator = new NpmCacheLocator(StandardEnvironment(), runner);

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(NpmCacheSource.Default, result.Source);
    }

    [TestMethod]
    public void Resolve_EnvUnset_NpmProcessThrows_FallsBackToCorrectedDefault()
    {
        var locator = new NpmCacheLocator(StandardEnvironment(), new ThrowingProcessRunner());

        NpmCacheResolution result = locator.Resolve();

        Assert.AreEqual(NpmCacheSource.Default, result.Source);
        Assert.AreEqual(@"%LocalAppData%\npm-cache", result.RawPath);
    }

    // ---- NpmCacheLocator.ParseCachePath: pure parser ----------------------------------------

    [TestMethod]
    public void ParseCachePath_DriveRootedPath_IsAccepted()
    {
        Assert.AreEqual(@"C:\Users\test\AppData\Local\npm-cache",
            NpmCacheLocator.ParseCachePath("C:\\Users\\test\\AppData\\Local\\npm-cache\n"));
    }

    [TestMethod]
    public void ParseCachePath_QuotedPath_IsUnquoted()
    {
        Assert.AreEqual(@"C:\.tools\.npm", NpmCacheLocator.ParseCachePath("\"C:\\.tools\\.npm\""));
    }

    [TestMethod]
    public void ParseCachePath_UncPath_IsAccepted()
    {
        Assert.AreEqual(@"\\server\share\npm", NpmCacheLocator.ParseCachePath(@"\\server\share\npm"));
    }

    [TestMethod]
    public void ParseCachePath_SkipsWarningLinesAndReturnsFirstPath()
    {
        const string stdout = "npm warn config foo deprecated\nC:\\.tools\\.npm\n";
        Assert.AreEqual(@"C:\.tools\.npm", NpmCacheLocator.ParseCachePath(stdout));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("undefined")]
    [DataRow("null")]
    [DataRow("npm warn only a warning, no path")]
    public void ParseCachePath_NonPaths_ReturnNull(string? stdout)
    {
        Assert.IsNull(NpmCacheLocator.ParseCachePath(stdout));
    }

    // ---- End-to-end through PackageCacheService ---------------------------------------------

    [TestMethod]
    public void GetPackageCaches_Npm_EnvUnset_UsesNpmConfigResolvedPath()
    {
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "D:\\custom\\npm\n");
        var fs = new FakeFileSystemProbe(new[] { @"D:\custom\npm" });
        var service = new PackageCacheService(StandardEnvironment(), fs, catalog: null, npmProcessRunner: runner);

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.AreEqual(@"D:\custom\npm", npm.ResolvedPath);
        Assert.IsTrue(npm.Detected);
        Assert.IsFalse(npm.EnvironmentVariableSet, "npm config is not the env var being set.");
        Assert.IsNull(npm.EnvironmentValue);
    }

    [TestMethod]
    public void GetPackageCaches_Npm_EnvUnset_NpmConfigUnavailable_UsesCorrectedDefault()
    {
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "", exitCode: 9009);
        var fs = new FakeFileSystemProbe(new[] { @"C:\Users\test\AppData\Local\npm-cache" });
        var service = new PackageCacheService(StandardEnvironment(), fs, catalog: null, npmProcessRunner: runner);

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.AreEqual(@"C:\Users\test\AppData\Local\npm-cache", npm.ResolvedPath);
        Assert.IsTrue(npm.Detected);
        Assert.IsFalse(npm.EnvironmentVariableSet);
    }

    [TestMethod]
    public void GetPackageCaches_Npm_EnvSet_HonoursEnvAndDoesNotLaunchNpm()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", @"G:\packages\npm");
        var runner = new RecordingProcessRunner();
        var fs = new FakeFileSystemProbe(new[] { @"G:\packages\npm" });
        var service = new PackageCacheService(env, fs, catalog: null, npmProcessRunner: runner);

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.AreEqual(@"G:\packages\npm", npm.ResolvedPath);
        Assert.IsTrue(npm.EnvironmentVariableSet);
        Assert.AreEqual(@"G:\packages\npm", npm.EnvironmentValue);
        Assert.IsTrue(npm.OnDevDrive);
        Assert.AreEqual(0, runner.CallCount, "npm must NOT be launched when the env var is set.");
    }

    [TestMethod]
    public void GetPackageCaches_NonNpm_ResolvesGenericallyAlongsideNpmRunner()
    {
        // A NuGet override must still resolve generically (env -> default) even though an npm runner
        // is wired. (npm itself legitimately consults the runner; only NuGet is asserted here.)
        FakeEnvironmentProvider env = StandardEnvironment().Set("NUGET_PACKAGES", @"G:\packages\nuget");
        var runner = new FakeProcessRunner().Set(CmdFile, stdout: "C:\\.tools\\.npm\n");
        var fs = new FakeFileSystemProbe(new[] { @"G:\packages\nuget" });
        var service = new PackageCacheService(env, fs, catalog: null, npmProcessRunner: runner);

        PackageCacheInfo nuget = service.GetPackageCaches('G').Single(c => c.EnvironmentVariable == "NUGET_PACKAGES");

        Assert.AreEqual(@"G:\packages\nuget", nuget.ResolvedPath);
        Assert.IsTrue(nuget.OnDevDrive);
        Assert.IsTrue(nuget.EnvironmentVariableSet);
    }

    /// <summary>An <see cref="IProcessRunner"/> that counts invocations (used to prove npm is/ isn't launched).</summary>
    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }

        public ProcessRunResult Run(string fileName, string arguments)
        {
            CallCount++;
            return new ProcessRunResult(-1, string.Empty, string.Empty);
        }
    }

    /// <summary>An <see cref="IProcessRunner"/> that throws, to prove detection degrades gracefully.</summary>
    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public ProcessRunResult Run(string fileName, string arguments) =>
            throw new InvalidOperationException("process launch blew up");
    }
}
