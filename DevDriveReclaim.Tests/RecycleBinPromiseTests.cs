namespace DevDriveReclaim.Tests;

/// <summary>
/// The promise the confirmation makes — "this can be restored from the Recycle Bin" — checked against
/// the settings that decide whether the shell keeps it, before and during a run.
/// </summary>
/// <remarks>
/// Every case here describes a bin through injected settings. Reproducing a real one would mean
/// switching off the developer's Recycle Bin or filling it past its limit, which no test may do.
/// </remarks>
[TestClass]
public sealed class RecycleBinPromiseTests
{
    private static RecycleBinVolumeSettings Bin(
        string root = @"C:\", bool has = true, bool disabled = false, long? max = null) =>
        new(root, has, disabled, max);

    private static ReclaimCandidate Candidate(string path, long size = 1024, bool recyclable = true) =>
        new("build-output", path, Path.GetFileName(path.TrimEnd('\\')), size, ReclaimRisk.Safe, "test", "test",
            supportsRecycleBin: recyclable);

    [TestMethod]
    public void AnItemThatFitsIsRecycled() =>
        Assert.AreEqual(RecycleBinVerdict.WillRecycle, RecycleBinPreflight.Evaluate(100, Bin(max: 1000)).Verdict);

    [TestMethod]
    public void AnItemLargerThanTheBinWouldBeDeletedPermanently()
    {
        RecycleBinCheck check = RecycleBinPreflight.Evaluate(2000, Bin(max: 1000));

        Assert.IsTrue(check.WillDeletePermanently);
        StringAssert.Contains(check.Reason, "too large for the Recycle Bin on C:");
    }

    [TestMethod]
    public void ABinSwitchedOffDeletesEverythingPermanently()
    {
        RecycleBinCheck check = RecycleBinPreflight.Evaluate(1, Bin(disabled: true));

        Assert.IsTrue(check.WillDeletePermanently);
        StringAssert.Contains(check.Reason, "turned off");
    }

    [TestMethod]
    public void AVolumeWithNoBinDeletesEverythingPermanently() =>
        Assert.IsTrue(RecycleBinPreflight.Evaluate(1, Bin(@"E:\", has: false)).WillDeletePermanently);

    /// <summary>
    /// Unreadable settings are not a reason to refuse every delete on the machine; the executor's
    /// shell call still asks before destroying anything it could not recycle.
    /// </summary>
    [TestMethod]
    public void UnreadableSettingsAreUnknownNotPermanent()
    {
        RecycleBinCheck check = RecycleBinPreflight.Evaluate(1, null);

        Assert.AreEqual(RecycleBinVerdict.Unknown, check.Verdict);
        Assert.IsFalse(check.WillDeletePermanently);
    }

    [TestMethod]
    public void TheCheckerReadsEachVolumeOnce()
    {
        int reads = 0;
        Func<ReclaimCandidate, RecycleBinCheck> check = RecycleBinPreflight.CreateChecker(root =>
        {
            reads++;
            return Bin(root);
        });

        check(Candidate(@"C:\a"));
        check(Candidate(@"C:\b"));
        check(Candidate(@"D:\c"));

        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public void ThisMachinesSystemVolumeCanBeRead()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;

        RecycleBinVolumeSettings? settings = RecycleBinPreflight.ReadSettings(root);

        Assert.IsNotNull(settings, "a fixed system volume should always describe its bin");
        Assert.IsTrue(settings.HasRecycleBin);
    }

    [TestMethod]
    public void ANetworkShareHasNoBin()
    {
        RecycleBinVolumeSettings? settings = RecycleBinPreflight.ReadSettings(@"\\build-server\share\");

        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.HasRecycleBin);
        Assert.IsTrue(RecycleBinPreflight.Evaluate(1, settings).WillDeletePermanently);
    }

    [TestMethod]
    public void ThePlanMovesARefusedItemIntoThePermanentGroupWithItsReason()
    {
        ReclaimCandidate fits = Candidate(@"C:\src\a\obj", 10);
        ReclaimCandidate huge = Candidate(@"C:\src\b\node_modules", 5_000);
        ReclaimCandidate bin = new("recycle-bin", @"C:\", "Recycle Bin on C:", 300, ReclaimRisk.Safe, "t", "t",
            supportsRecycleBin: false);

        ReclaimPlan plan = ReclaimPlan.Create(
            [fits, huge, bin], RecycleBinPreflight.CreateChecker(root => Bin(root, max: 1_000)));

        Assert.AreEqual(5_300, plan.FreedBytes, "the refused item and the bin are both freed now");
        Assert.AreEqual(10, plan.RecycledBytes);
        PlannedRemoval refused = plan.Items.Single(i => i.Candidate.Path == huge.Path);
        Assert.IsTrue(refused.IsPermanent);
        StringAssert.Contains(refused.PermanentLabel, "too large");
        Assert.IsNull(plan.Items.Single(i => i.Candidate.Path == bin.Path).Reason);
    }

    [TestMethod]
    public void OnlyItemsThePlanNamedAsPermanentAreReleasedForPermanentDeletion()
    {
        ReclaimCandidate fits = Candidate(@"C:\src\a\obj", 10);
        ReclaimCandidate huge = Candidate(@"C:\src\b\node_modules", 5_000);
        ReclaimCandidate inside = Candidate(@"C:\src\b\node_modules\.cache", 100);

        ReclaimPlan plan = ReclaimPlan.Create(
            [fits, huge, inside], RecycleBinPreflight.CreateChecker(root => Bin(root, max: 1_000)));

        IReadOnlyList<ReclaimCandidate> run = plan.ConsentedSelection([fits, huge, inside]);

        Assert.IsTrue(run.Single(c => c.Path == fits.Path).SupportsRecycleBin);
        Assert.IsFalse(run.Single(c => c.Path == huge.Path).SupportsRecycleBin);
        Assert.IsTrue(
            run.Single(c => c.Path == inside.Path).SupportsRecycleBin,
            "only roots are consented; a nested row goes with its container");
    }

    [TestMethod]
    public void PlannedBytesAreSplitByVolume()
    {
        ReclaimPlan plan = ReclaimPlan.Create(
            [Candidate(@"C:\a", 10), Candidate(@"D:\b", 5_000)],
            RecycleBinPreflight.CreateChecker(root => Bin(root, max: 1_000)));

        Assert.AreEqual(10, plan.RecycledBytesByVolume[@"C:\"]);
        Assert.AreEqual(5_000, plan.FreedBytesByVolume[@"D:\"]);
        Assert.IsFalse(plan.FreedBytesByVolume.ContainsKey(@"C:\"));
    }

    /// <summary>
    /// The trust fix at run time. An item that asked for the bin, and that the bin would now refuse —
    /// because its settings changed after the confirmation, or because the confirmation never saw it —
    /// is kept and reported, never deleted. Nothing in this test reaches the shell.
    /// </summary>
    [TestMethod]
    public async Task TheExecutorKeepsAnItemTheBinWouldRefuse()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("too-big");
        fixture.File(@"too-big\payload.bin", 4096);

        ReclaimOutcome outcome = await new ReclaimExecutor(root => Bin(root, disabled: true))
            .ExecuteAsync([Candidate(folder, 4096)], null, CancellationToken.None);

        Assert.IsTrue(Directory.Exists(folder), "a promised-restorable item must not be destroyed");
        Assert.AreEqual(ReclaimItemStatus.Failed, outcome.Items.Single().Status);
        StringAssert.Contains(outcome.Items.Single().Message, "kept");
        Assert.AreEqual(0, outcome.BytesFreed);
        Assert.AreEqual(0, outcome.BytesRecycled);
    }

    /// <summary>
    /// The other half: once the plan has named an item as permanent and the user confirmed, the
    /// executor deletes it outright and counts it as freed.
    /// </summary>
    [TestMethod]
    public async Task AConsentedItemIsDeletedAndCountedAsFreed()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("too-big");
        fixture.File(@"too-big\payload.bin", 4096);
        ReclaimCandidate candidate = Candidate(folder, 4096);
        Func<string, RecycleBinVolumeSettings?> settings = root => Bin(root, max: 1);

        ReclaimPlan plan = ReclaimPlan.Create([candidate], RecycleBinPreflight.CreateChecker(settings));
        ReclaimOutcome outcome = await new ReclaimExecutor(settings)
            .ExecuteAsync(plan.ConsentedSelection([candidate]), null, CancellationToken.None);

        Assert.IsFalse(Directory.Exists(folder));
        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status);
        Assert.AreEqual(4096, outcome.BytesFreed);
    }
}
