using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the read-only <see cref="InstalledToolDetector"/> (composed by the app via the build-
/// benchmark suite). Every probe is faked — no real process is launched and no real PATH is read.
/// </summary>
[TestClass]
public sealed class InstalledToolDetectorTests
{
    // ---- ParseVersion (pure) -------------------------------------------------------------------

    [TestMethod]
    [DataRow("git version 2.43.0.windows.1", "", "2.43.0")]   // trailing ".windows.1" is dropped
    [DataRow("v20.11.1", "", "20.11.1")]                       // node prefix
    [DataRow("10.8.2", "", "10.8.2")]                          // npm bare
    [DataRow("Python 3.12.1", "", "3.12.1")]                   // python prefix
    [DataRow("go version go1.22.0 windows/amd64", "", "1.22.0")] // go embeds version in token
    [DataRow("cargo 1.77.2 (e52e36006 2024-03-26)", "", "1.77.2")]
    [DataRow("8.0.100", "", "8.0.100")]                        // dotnet bare
    [DataRow("1.2.3-preview.4", "", "1.2.3-preview.4")]        // prerelease suffix kept
    [DataRow("1.2.3+build.5", "", "1.2.3+build.5")]            // build metadata kept
    public void ParseVersion_ExtractsFirstVersionToken(string stdout, string stderr, string expected)
    {
        Assert.AreEqual(expected, InstalledToolDetector.ParseVersion(stdout, stderr));
    }

    [TestMethod]
    public void ParseVersion_PrefersStdout_ThenFallsBackToStderr()
    {
        // java prints its version to STDERR.
        string? version = InstalledToolDetector.ParseVersion(string.Empty, "openjdk version \"21.0.2\" 2024-01-16");
        Assert.AreEqual("21.0.2", version);
    }

    [TestMethod]
    public void ParseVersion_StdoutWins_WhenBothPresent()
    {
        Assert.AreEqual("1.0.0", InstalledToolDetector.ParseVersion("tool 1.0.0", "fallback 9.9.9"));
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", "")]
    [DataRow("no version here", "still nothing")]
    [DataRow("major only 7", "")] // needs at least MAJOR.MINOR
    public void ParseVersion_ReturnsNull_WhenNoVersionToken(string? stdout, string? stderr)
    {
        Assert.IsNull(InstalledToolDetector.ParseVersion(stdout, stderr));
    }

    // ---- Detect (single tool) ------------------------------------------------------------------

    [TestMethod]
    public void Detect_OnPathAndVersionProbed_ReturnsFoundWithBoth()
    {
        var runner = new FakeProcessRunner().Set("git", "git version 2.43.0", string.Empty);
        var probe = new FakePathProbe().Add("git", @"C:\Program Files\Git\cmd\git.exe");
        var detector = new InstalledToolDetector(runner, probe);

        InstalledToolInfo info = detector.Detect("git");

        Assert.IsTrue(info.Found);
        Assert.AreEqual("git", info.Name);
        Assert.AreEqual("2.43.0", info.Version);
        Assert.AreEqual(@"C:\Program Files\Git\cmd\git.exe", info.Path);
    }

    [TestMethod]
    public void Detect_IsCaseInsensitiveOnName()
    {
        var runner = new FakeProcessRunner().Set("git", "git version 2.43.0");
        var detector = new InstalledToolDetector(runner, new FakePathProbe());

        Assert.AreEqual("git", detector.Detect("GIT").Name);
        Assert.AreEqual("2.43.0", detector.Detect("GiT").Version);
    }

    [TestMethod]
    public void Detect_OnPathButVersionProbeFails_StillFound_NoVersion()
    {
        // Resolvable on PATH, but the version probe "didn't launch" (negative exit code).
        var runner = new FakeProcessRunner(); // git not configured -> exit -1
        var probe = new FakePathProbe().Add("git", @"C:\bin\git.exe");
        var detector = new InstalledToolDetector(runner, probe);

        InstalledToolInfo info = detector.Detect("git");

        Assert.IsTrue(info.Found);
        Assert.IsNull(info.Version);
        Assert.AreEqual(@"C:\bin\git.exe", info.Path);
    }

    [TestMethod]
    public void Detect_VersionButNotOnPath_StillFound_NoPath()
    {
        var runner = new FakeProcessRunner().Set("node", "v20.11.1");
        var detector = new InstalledToolDetector(runner, new FakePathProbe()); // not on PATH

        InstalledToolInfo info = detector.Detect("node");

        Assert.IsTrue(info.Found);
        Assert.AreEqual("20.11.1", info.Version);
        Assert.IsNull(info.Path);
    }

    [TestMethod]
    public void Detect_NeitherOnPathNorLaunchable_NotFound()
    {
        var detector = new InstalledToolDetector(new FakeProcessRunner(), new FakePathProbe());

        InstalledToolInfo info = detector.Detect("yarn");

        Assert.IsFalse(info.Found);
        Assert.IsNull(info.Version);
        Assert.IsNull(info.Path);
    }

    [TestMethod]
    public void Detect_TimedOutProbe_DoesNotParseVersion()
    {
        var spec = new[] { new ToolProbeSpec("demo", "demo", "--version") };
        var runner = new FakeProcessRunner().Set("demo", "demo 1.2.3", string.Empty, exitCode: 0, timedOut: true);
        var detector = new InstalledToolDetector(runner, new FakePathProbe(), spec);

        InstalledToolInfo info = detector.Detect("demo");

        Assert.IsNull(info.Version);
        Assert.IsFalse(info.Found);
    }

    [TestMethod]
    public void Detect_JavaUsesStderr()
    {
        var runner = new FakeProcessRunner().Set("java", string.Empty, "openjdk version \"21.0.2\" 2024-01-16");
        var detector = new InstalledToolDetector(runner, new FakePathProbe().Add("java", @"C:\java\bin\java.exe"));

        Assert.AreEqual("21.0.2", detector.Detect("java").Version);
    }

    [TestMethod]
    public void Detect_UnknownToolName_ReturnsNotFound()
    {
        var detector = new InstalledToolDetector(new FakeProcessRunner(), new FakePathProbe());

        InstalledToolInfo info = detector.Detect("not-a-real-tool");

        Assert.IsFalse(info.Found);
        Assert.AreEqual("not-a-real-tool", info.Name);
    }

    // ---- DetectAll -----------------------------------------------------------------------------

    [TestMethod]
    public void DetectAll_ReturnsOnePerCatalogueEntry()
    {
        var detector = new InstalledToolDetector(new FakeProcessRunner(), new FakePathProbe());

        IReadOnlyList<InstalledToolInfo> all = detector.DetectAll();

        Assert.HasCount(InstalledToolCatalog.Default.Count, all);
        Assert.HasCount(14, all);
        // None configured -> all "not found", but still reported.
        Assert.IsTrue(all.All(t => !t.Found));
    }

    [TestMethod]
    public void DetectAll_MixedAvailability()
    {
        var runner = new FakeProcessRunner()
            .Set("git", "git version 2.43.0")
            .Set("dotnet", "8.0.100");
        var probe = new FakePathProbe()
            .Add("git", @"C:\bin\git.exe")
            .Add("dotnet", @"C:\dotnet\dotnet.exe");
        var detector = new InstalledToolDetector(runner, probe);

        IReadOnlyList<InstalledToolInfo> all = detector.DetectAll();

        Assert.IsTrue(all.Single(t => t.Name == "git").Found);
        Assert.AreEqual("2.43.0", all.Single(t => t.Name == "git").Version);
        Assert.IsTrue(all.Single(t => t.Name == "dotnet").Found);
        Assert.IsFalse(all.Single(t => t.Name == "node").Found);
    }

    [TestMethod]
    public void DetectAll_CustomCatalogue_IsHonoured()
    {
        var catalog = new[]
        {
            new ToolProbeSpec("alpha", "alpha", "--version"),
            new ToolProbeSpec("beta", "beta", "--version"),
        };
        var runner = new FakeProcessRunner().Set("alpha", "alpha 1.0.0");
        var detector = new InstalledToolDetector(runner, new FakePathProbe(), catalog);

        IReadOnlyList<InstalledToolInfo> all = detector.DetectAll();

        Assert.HasCount(2, all);
        Assert.AreEqual("1.0.0", all.Single(t => t.Name == "alpha").Version);
    }

    [TestMethod]
    public void DetectAll_Cancellation_Throws()
    {
        var detector = new InstalledToolDetector(new FakeProcessRunner(), new FakePathProbe());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => detector.DetectAll(cts.Token));
    }

    // ---- composition ---------------------------------------------------------------------------

    [TestMethod]
    public void Ctor_NullArgs_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new InstalledToolDetector(null!, new FakePathProbe()));
        Assert.ThrowsExactly<ArgumentNullException>(() => new InstalledToolDetector(new FakeProcessRunner(), null!));
    }

    [TestMethod]
    public void CreateDefault_ReturnsInstance()
    {
        Assert.IsNotNull(InstalledToolDetector.CreateDefault());
    }

    [TestMethod]
    public void Catalogue_CoversTheRequestedTools()
    {
        string[] names = InstalledToolCatalog.Default.Select(s => s.Name).ToArray();
        foreach (string expected in new[]
        {
            "git", "node", "npm", "pnpm", "yarn", "dotnet", "cargo", "rustc",
            "python", "pip", "java", "maven", "gradle", "go",
        })
        {
            CollectionAssert.Contains(names, expected);
        }

        // java uses the single-dash flag; go uses the "version" sub-command.
        Assert.AreEqual("-version", InstalledToolCatalog.Default.Single(s => s.Name == "java").VersionArguments);
        Assert.AreEqual("version", InstalledToolCatalog.Default.Single(s => s.Name == "go").VersionArguments);
        Assert.AreEqual("mvn", InstalledToolCatalog.Default.Single(s => s.Name == "maven").Executable);
    }
}
