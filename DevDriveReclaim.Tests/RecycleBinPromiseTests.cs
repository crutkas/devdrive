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
        string root = @"C:\", bool has = true, bool disabled = false, long? max = null, long? used = null) =>
        new(root, has, disabled, max, used);

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
        ReclaimCandidate bin = new("recycle-bin", @"C:\", "Recycle Bin on C:", 300, ReclaimRisk.Check, "t", "t",
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

    /// <summary>
    /// An item within the bin's limit but bigger than what's left of it: the shell would recycle it and make room by deleting
    /// the bin's oldest items for good, so it can't be promised to the bin either.
    /// </summary>
    [TestMethod]
    public void AnItemBiggerThanTheRoomLeftWouldDeleteOthersPermanently()
    {
        RecycleBinCheck check = RecycleBinPreflight.Evaluate(300, Bin(max: 1000, used: 800));

        Assert.IsTrue(check.WillDeletePermanently);
        StringAssert.Contains(check.Reason, "no room left for it in the Recycle Bin on C:");
        Assert.IsFalse(RecycleBinPreflight.Evaluate(200, Bin(max: 1000, used: 800)).WillDeletePermanently);
        Assert.IsFalse(
            RecycleBinPreflight.Evaluate(300, Bin(max: 1000)).WillDeletePermanently,
            "what the bin holds unread: only the limit is checked");
    }

    [TestMethod]
    public void ARunsItemsShareTheRoom()
    {
        ReclaimCandidate[] items = [Candidate(@"C:\a", 400), Candidate(@"C:\b", 400), Candidate(@"C:\c", 400), Candidate(@"D:\d", 400)];
        Func<ReclaimCandidate, RecycleBinCheck> budget = RecycleBinPreflight.CreateBudget(items, root => Bin(root, max: 1000, used: 100));

        bool[] permanent = [.. items.Select(i => budget(i).WillDeletePermanently)];

        CollectionAssert.AreEqual(new[] { false, false, true, false }, permanent, "C: has room for two; D:'s bin is its own");
    }

    /// <summary>The reason is data too, so a confirmation can word it in its own language and say a shared one once.</summary>
    [TestMethod]
    public void AReasonIsAlsoData()
    {
        RecycleBinCheck tooLarge = RecycleBinPreflight.Evaluate(2000, Bin(max: 1000));
        Assert.AreEqual(RecycleBinRefusal.TooLarge, tooLarge.Refusal);
        Assert.AreEqual(@"C:\", tooLarge.VolumeRoot);
        Assert.AreEqual(1000, tooLarge.CapacityBytes);

        RecycleBinCheck noRoom = RecycleBinPreflight.Evaluate(300, Bin(max: 1000, used: 800));
        Assert.AreEqual((RecycleBinRefusal.NoRoom, 1000L), (noRoom.Refusal, noRoom.CapacityBytes!.Value));
        Assert.AreEqual(RecycleBinRefusal.TurnedOff, RecycleBinPreflight.Evaluate(1, Bin(disabled: true)).Refusal);
        Assert.AreEqual(RecycleBinRefusal.NoRecycleBin, RecycleBinPreflight.Evaluate(1, Bin(@"E:\", has: false)).Refusal);
        Assert.AreEqual(RecycleBinRefusal.Unknown, RecycleBinPreflight.Evaluate(1, null).Refusal);
        Assert.AreEqual(RecycleBinRefusal.None, RecycleBinPreflight.Evaluate(1, Bin()).Refusal);
    }

    [TestMethod]
    public void ARunNeverOutgrowsTheLimitEvenWhenTheBinsContentsCantBeRead()
    {
        ReclaimCandidate[] items = [Candidate(@"C:\a", 400), Candidate(@"C:\b", 400), Candidate(@"C:\c", 400)];
        Func<ReclaimCandidate, RecycleBinCheck> budget = RecycleBinPreflight.CreateBudget(items, root => Bin(root, max: 1000));

        bool[] permanent = [.. items.Select(i => budget(i).WillDeletePermanently)];

        CollectionAssert.AreEqual(new[] { false, false, true }, permanent, "the bin holds at least what the run put there");
    }

    [TestMethod]
    public void ABinTheRunEmptiesStartsEmpty()
    {
        ReclaimCandidate bin = new("recycle-bin", @"C:\", "Recycle Bin on C:", 900, ReclaimRisk.Check, "t", "t",
            supportsRecycleBin: false);
        ReclaimCandidate item = Candidate(@"C:\a", 500);

        Assert.IsTrue(RecycleBinPreflight.CreateBudget([item], root => Bin(root, max: 1000, used: 900))(item).WillDeletePermanently);
        Assert.IsFalse(RecycleBinPreflight.CreateBudget([bin, item], root => Bin(root, max: 1000, used: 900))(item).WillDeletePermanently);
        Assert.IsFalse(
            RecycleBinPreflight.CreateBudget([bin, item], root => Bin(root, max: 1000))(item).WillDeletePermanently,
            "emptied, so its contents are known even when they couldn't be read");
    }

    /// <summary>
    /// The room goes to what's hardest to get back: a Careful item before Safe ones, then the smallest first, so the most
    /// items stay restorable. What doesn't fit is named as permanent, and only that is released for permanent deletion.
    /// </summary>
    [TestMethod]
    public void ThePlanGivesTheRoomToCarefulItemsFirstAndNamesTheRest()
    {
        ReclaimCandidate bigSafe = Candidate(@"C:\src\a\node_modules", 600);
        ReclaimCandidate careful = new("worktree", @"C:\src\wt", "wt", 600, ReclaimRisk.Careful, "t", "t");
        ReclaimCandidate smallSafe = Candidate(@"C:\src\b\obj", 300);

        ReclaimPlan plan = ReclaimPlan.Create([bigSafe, careful, smallSafe], root => Bin(root, max: 1000, used: 0));

        Assert.AreEqual(900, plan.RecycledBytes);
        PlannedRemoval overflow = plan.Permanent.Single();
        Assert.AreEqual(bigSafe.Path, overflow.Candidate.Path);
        StringAssert.Contains(overflow.PermanentLabel, "no room left");
        Assert.AreEqual(RecycleBinRefusal.NoRoom, overflow.Why!.Refusal);
        CollectionAssert.AreEqual(
            ReclaimOverlapResolver.Roots([bigSafe, careful, smallSafe]).Select(c => c.Path).ToArray(), plan.Items.Select(i => i.Candidate.Path).ToArray(),
            "the plan keeps the overlap resolver's order");
        Assert.IsFalse(plan.ConsentedSelection([bigSafe, careful, smallSafe]).Single(c => c.Path == bigSafe.Path).SupportsRecycleBin);
    }

    /// <summary>#310's run, in small: a bin with room for one item and two that ask for it. The second is kept, not recycled
    /// on top of the first (which the shell would then delete for good). Nothing in this test reaches the shell.</summary>
    [TestMethod]
    public async Task TheExecutorKeepsAnItemTheBinHasNoRoomFor()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("no-room");
        fixture.File(@"no-room\payload.bin", 4096);

        ReclaimOutcome outcome = await new ReclaimExecutor(root => Bin(root, max: 1_000_000, used: 999_000))
            .ExecuteAsync([Candidate(folder, 4096)], null, CancellationToken.None);

        Assert.IsTrue(Directory.Exists(folder), "recycling it would delete the bin's oldest items for good");
        Assert.AreEqual(ReclaimItemStatus.Failed, outcome.Items.Single().Status);
        StringAssert.Contains(outcome.Items.Single().Message, "kept — no room left for it in the Recycle Bin");
    }

    [TestMethod]
    public void ARunCountsWhatTheConfirmationCounted()
    {
        ReclaimCandidate worktree = Candidate(@"C:\src\wt", 900);
        ReclaimCandidate inside = Candidate(@"C:\src\wt\bin", 100);
        ReclaimCandidate kept = Candidate(@"C:\src\other", 50);
        ReclaimCandidate keptInside = Candidate(@"C:\src\other\obj", 10);

        var outcome = new ReclaimOutcome(
        [
            new(worktree, ReclaimItemStatus.Recycled, 900, "recycled"),
            new(kept, ReclaimItemStatus.Failed, 0, "in use"),
            new(inside, ReclaimItemStatus.Absorbed, 0, "removed with it"),
            new(keptInside, ReclaimItemStatus.Failed, 0, "still here"),
        ], Cancelled: false);

        Assert.AreEqual(1, outcome.RemovedCount);
        Assert.AreEqual(1, outcome.FailedCount);
        Assert.AreEqual(2, outcome.Failures.Count(), "each row still says why it's there");
    }
}
