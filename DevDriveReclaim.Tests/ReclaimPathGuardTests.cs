namespace DevDriveReclaim.Tests;

/// <summary>
/// The gate every deletion passes through.
/// </summary>
/// <remarks>
/// These are the highest-consequence tests in the repository. Everything else here can be wrong and
/// produce a bad number; this can be wrong and produce a deleted profile. So the cases are written
/// against the failure — "does it refuse C:\" rather than "does it allow a temp folder" — and the
/// paths under test are the ones a provider could plausibly emit by accident.
/// </remarks>
[TestClass]
public sealed class ReclaimPathGuardTests
{
    private static ReclaimCandidate Candidate(string path, string categoryId = "build-output") =>
        new(categoryId,
            path,
            "candidate",
            1024,
            ReclaimRisk.Safe,
            "test",
            "test");

    [TestMethod]
    [DataRow(@"C:\")]
    [DataRow(@"C:")]
    [DataRow(@"G:\")]
    [DataRow(@"C:/")]
    public void AVolumeRootIsRefused(string root)
    {
        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(root));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision, verdict.Explanation);
        StringAssert.Contains(verdict.Explanation, "volume");
    }

    /// <summary>
    /// The Recycle Bin is the one candidate whose path is legitimately a volume root, and it is only
    /// legitimate because of what it means — empty the bin, not delete the drive.
    /// </summary>
    [TestMethod]
    public void OnlyTheRecycleBinMayNameAWholeVolume()
    {
        ReclaimGuardVerdict bin = ReclaimPathGuard.Inspect(Candidate(@"C:\", "recycle-bin"));

        Assert.AreEqual(ReclaimGuardDecision.Allow, bin.Decision, bin.Explanation);
        Assert.AreEqual(ReclaimTargetKind.RecycleBin, bin.Kind);
    }

    /// <summary>
    /// The case the string-only checks miss: a path that is not a root until it is resolved. A
    /// provider that joins a root with a relative segment can produce this without anyone noticing.
    /// </summary>
    [TestMethod]
    public void APathThatClimbsBackToTheVolumeRootIsRefused()
    {
        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(@"C:\Windows\.."));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision);
        StringAssert.Contains(verdict.Explanation, "volume root");
    }

    [TestMethod]
    public void TheWindowsDirectoryIsRefused()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(windows));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision);
        StringAssert.Contains(verdict.Explanation, "protected");
    }

    [TestMethod]
    public void TheUserProfileIsRefused()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(profile));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision);
    }

    /// <summary>
    /// The rule that makes the protected list short. Nothing lists <c>C:\Users</c>, but deleting it
    /// would take the profile with it, so containment refuses it for free.
    /// </summary>
    [TestMethod]
    public void AFolderThatContainsAProtectedLocationIsRefused()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string parent = Path.GetDirectoryName(profile)!;

        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(parent));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision);
        StringAssert.Contains(verdict.Explanation, "contains the protected location");
    }

    /// <summary>
    /// A build-output detector pointed at the tree this app was built from will find its own
    /// directory. Deleting the running process is not a recoverable state.
    /// </summary>
    [TestMethod]
    public void TheApplicationsOwnDirectoryIsRefused()
    {
        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(AppContext.BaseDirectory));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision);
    }

    [TestMethod]
    public void AnOrdinaryFolderIsAllowedAndResolvedToADirectory()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("project", "obj");

        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(folder));

        Assert.AreEqual(ReclaimGuardDecision.Allow, verdict.Decision, verdict.Explanation);
        Assert.AreEqual(ReclaimTargetKind.Directory, verdict.Kind);
        Assert.IsNotNull(verdict.CanonicalPath);
    }

    [TestMethod]
    public void AnOrdinaryFileIsAllowedAndResolvedToAFile()
    {
        using var fixture = new ReclaimFixture();
        string file = fixture.File(@"dupes\big.bin", 4096);

        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(file));

        Assert.AreEqual(ReclaimGuardDecision.Allow, verdict.Decision, verdict.Explanation);
        Assert.AreEqual(ReclaimTargetKind.File, verdict.Kind);
    }

    /// <summary>
    /// A scan takes minutes and the world moves underneath it. Something a build already removed is
    /// not an error to report — it is the outcome the user wanted, reached without us.
    /// </summary>
    [TestMethod]
    public void APathThatNoLongerExistsIsAlreadyGoneRatherThanAFailure()
    {
        using var fixture = new ReclaimFixture();

        ReclaimGuardVerdict verdict =
            ReclaimPathGuard.Inspect(Candidate(Path.Combine(fixture.Root, "deleted-since-the-scan")));

        Assert.AreEqual(ReclaimGuardDecision.AlreadyGone, verdict.Decision);
    }

    /// <summary>
    /// Quoting and trailing separators come from paths that passed through a shell or a config file.
    /// Both must resolve to the same target, and neither may become a different one.
    /// </summary>
    [TestMethod]
    [DataRow("\"{0}\"")]
    [DataRow("{0}\\")]
    [DataRow(" {0} ")]
    public void SurfaceNoiseResolvesToTheSameTarget(string format)
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("project", "bin");

        string canonical = ReclaimPathGuard.Inspect(Candidate(folder)).CanonicalPath!;
        ReclaimGuardVerdict noisy =
            ReclaimPathGuard.Inspect(Candidate(string.Format(format, folder)));

        Assert.AreEqual(ReclaimGuardDecision.Allow, noisy.Decision, noisy.Explanation);
        Assert.AreEqual(canonical, noisy.CanonicalPath, ignoreCase: true);
    }

    /// <summary>
    /// The canonical path is what the volume stores, not what the provider typed. Without this, two
    /// candidates for one folder look like two folders and the second delete reports a failure for
    /// something that already succeeded.
    /// </summary>
    [TestMethod]
    public void CaseIsCorrectedToWhatTheVolumeActuallyStores()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("MixedCaseFolder");

        ReclaimGuardVerdict shouted =
            ReclaimPathGuard.Inspect(Candidate(folder.ToUpperInvariant()));

        Assert.AreEqual(ReclaimGuardDecision.Allow, shouted.Decision, shouted.Explanation);
        Assert.AreEqual("MixedCaseFolder", Path.GetFileName(shouted.CanonicalPath!));
    }

    /// <summary>
    /// A junction's bytes live on the other side of it, where the measurer never looked. Removing it
    /// would free almost nothing while risking a folder the scan never saw, so it is refused
    /// outright rather than unlinked.
    /// </summary>
    [TestMethod]
    public void AJunctionIsRefusedRatherThanFollowed()
    {
        using var fixture = new ReclaimFixture();
        string real = fixture.Dir("real-data");
        fixture.File(@"real-data\payload.bin", 4096);
        string junction = fixture.Junction("link-to-real", real);

        Assert.IsTrue(Directory.Exists(junction), "mklink /J did not create the junction");

        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(Candidate(junction));

        Assert.AreEqual(ReclaimGuardDecision.Refuse, verdict.Decision, verdict.Explanation);
        StringAssert.Contains(verdict.Explanation, "junction");

        // The point of refusing: what it pointed at is untouched.
        Assert.IsTrue(File.Exists(Path.Combine(real, "payload.bin")));
    }
}
