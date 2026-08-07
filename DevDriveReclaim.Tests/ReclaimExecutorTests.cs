namespace DevDriveReclaim.Tests;

/// <summary>
/// The executor, against real temp trees that really get deleted.
/// </summary>
/// <remarks>
/// Deliberately not mocked. A delete engine verified against a stub filesystem proves that the code
/// calls the methods it calls; the questions worth asking here — does the folder actually go, does a
/// nested selection delete twice, does one locked file abort the batch — are all questions about
/// what the real filesystem does in response.
/// <para>
/// Everything lives under a unique temp root that <see cref="ReclaimFixture"/> removes on dispose,
/// so a failing test cannot leave the developer's disk in a worse state than it found it.
/// </para>
/// </remarks>
[TestClass]
public sealed class ReclaimExecutorTests
{
    private static ReclaimCandidate Candidate(
        string path,
        long size = 1024,
        ReclaimRisk risk = ReclaimRisk.Safe,
        bool supportsRecycleBin = false,
        string categoryId = "build-output") =>
        new(categoryId, path, DisplayNameOf(path), size, risk, "test", "test",
            supportsRecycleBin: supportsRecycleBin);

    // GetFileName is empty for a volume root, and the model rejects an empty display name.
    private static string DisplayNameOf(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    /// <summary>
    /// Permanent deletion rather than recycling, throughout. Filling the developer's Recycle Bin
    /// with test fixtures would be a rude thing for a test suite to do, and the recycle path's own
    /// behaviour is asserted separately by what it reports, not by where the bytes end up.
    /// </summary>
    private static async Task<ReclaimOutcome> Execute(params ReclaimCandidate[] candidates) =>
        await new ReclaimExecutor().ExecuteAsync(candidates, null, CancellationToken.None);

    [TestMethod]
    public async Task AFolderIsRemovedFromDisk()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("project", "obj");
        fixture.File(@"project\obj\built.dll", 2048);

        ReclaimOutcome outcome = await Execute(Candidate(folder, size: 2048));

        Assert.IsFalse(Directory.Exists(folder));
        Assert.AreEqual(1, outcome.RemovedCount);
        Assert.AreEqual(0, outcome.FailedCount);
        Assert.AreEqual(2048, outcome.BytesFreed);
    }

    [TestMethod]
    public async Task AFileIsRemovedFromDisk()
    {
        using var fixture = new ReclaimFixture();
        string file = fixture.File(@"dupes\copy.bin", 512);

        ReclaimOutcome outcome = await Execute(Candidate(file, size: 512));

        Assert.IsFalse(File.Exists(file));
        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status);
    }

    /// <summary>
    /// The reason the executor resolves nesting rather than iterating the selection: the child is
    /// gone the moment the parent is, so deleting it separately would fail and report a failure for
    /// work that succeeded.
    /// </summary>
    [TestMethod]
    public async Task ANestedSelectionIsRemovedOnceAndCountedOnce()
    {
        using var fixture = new ReclaimFixture();
        string worktree = fixture.Dir("feature-branch");
        string inner = fixture.Dir("feature-branch", "obj");
        fixture.File(@"feature-branch\obj\built.dll", 1024);

        ReclaimOutcome outcome = await Execute(
            Candidate(worktree, size: 5000),
            Candidate(inner, size: 1024));

        Assert.IsFalse(Directory.Exists(worktree));
        Assert.AreEqual(5000, outcome.BytesFreed, "the child's bytes were counted a second time");

        ReclaimItemOutcome child = outcome.Items.Single(i => i.Candidate.Path == inner);
        Assert.AreEqual(ReclaimItemStatus.Absorbed, child.Status);
        StringAssert.Contains(child.Message, "feature-branch");
    }

    /// <summary>
    /// One locked file is the normal case on a developer machine. A batch that aborts on it leaves
    /// the user with a partial delete and no account of what happened.
    /// </summary>
    [TestMethod]
    public async Task ALockedItemFailsAloneAndTheRestOfTheBatchStillRuns()
    {
        using var fixture = new ReclaimFixture();
        string locked = fixture.Dir("locked");
        string free = fixture.Dir("free");
        fixture.File(@"free\junk.bin", 128);

        string lockedFile = Path.Combine(locked, "held-open.bin");
        using (var handle = new FileStream(lockedFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            ReclaimOutcome outcome = await Execute(
                Candidate(locked, size: 64),
                Candidate(free, size: 128));

            Assert.IsFalse(Directory.Exists(free), "the healthy item was not removed");
            Assert.IsTrue(Directory.Exists(locked));

            Assert.AreEqual(1, outcome.FailedCount);
            Assert.AreEqual(128, outcome.BytesFreed, "the failed item's bytes were counted as freed");
            Assert.AreEqual(locked, outcome.Failures.Single().Candidate.Path);
        }
    }

    /// <summary>
    /// The guard's refusals have to reach the user through the executor, or the safest code in the
    /// app is a silent no-op that reports success.
    /// </summary>
    [TestMethod]
    public async Task ARefusedPathIsReportedAndNothingIsTouched()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        ReclaimOutcome outcome = await Execute(Candidate(windows, size: 999));

        Assert.IsTrue(Directory.Exists(windows));
        Assert.AreEqual(ReclaimItemStatus.Refused, outcome.Items.Single().Status);
        Assert.AreEqual(0, outcome.BytesFreed);
        Assert.AreEqual(1, outcome.FailedCount);
    }

    /// <summary>
    /// The single most dangerous thing this app could do. A provider bug that emits a volume root
    /// must not reach a delete call, and the count that matters is zero bytes freed.
    /// </summary>
    [TestMethod]
    public async Task AVolumeRootIsNeverDeleted()
    {
        ReclaimOutcome outcome = await Execute(
            Candidate(Path.GetTempPath()[..3], size: long.MaxValue / 2));

        Assert.AreEqual(ReclaimItemStatus.Refused, outcome.Items.Single().Status);
        Assert.AreEqual(0, outcome.BytesFreed);
    }

    [TestMethod]
    public async Task SomethingAlreadyGoneIsNotAFailure()
    {
        using var fixture = new ReclaimFixture();

        ReclaimOutcome outcome = await Execute(
            Candidate(Path.Combine(fixture.Root, "removed-by-a-build"), size: 4096));

        Assert.AreEqual(ReclaimItemStatus.AlreadyGone, outcome.Items.Single().Status);
        Assert.AreEqual(0, outcome.FailedCount);
        Assert.AreEqual(0, outcome.BytesFreed, "nothing was there, so nothing came back");
    }

    [TestMethod]
    public async Task AnEmptySelectionDoesNothing()
    {
        ReclaimOutcome outcome = await Execute();

        Assert.IsEmpty(outcome.Items);
        Assert.AreEqual(0, outcome.BytesFreed);
        Assert.IsFalse(outcome.Cancelled);
    }

    /// <summary>
    /// Cancelling before anything runs must leave the disk untouched, and every item must say it
    /// was not reached rather than silently vanishing from the report.
    /// </summary>
    [TestMethod]
    public async Task CancellingBeforeTheRunLeavesEverythingInPlace()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("keep-me");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        ReclaimOutcome outcome = await new ReclaimExecutor()
            .ExecuteAsync([Candidate(folder)], null, cts.Token);

        Assert.IsTrue(Directory.Exists(folder));
        Assert.IsTrue(outcome.Cancelled);
        Assert.AreEqual(ReclaimItemStatus.Cancelled, outcome.Items.Single().Status);
    }

    /// <summary>Reports on the calling thread, so a cancel raised from it lands before the run advances.</summary>
    private sealed class ImmediateProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>
    /// The state the Stop button actually creates, which cancelling before the run does not reach:
    /// some items already gone, the rest untouched. The disk and the report have to agree about
    /// which is which, because the ViewModel deletes exactly the rows the report calls removed —
    /// so an item wrongly listed as removed disappears from the table while still occupying the
    /// space it claimed to give back.
    /// </summary>
    [TestMethod]
    public async Task StoppingPartwayKeepsWhatWentAndLeavesTheRestOnDisk()
    {
        using var fixture = new ReclaimFixture();
        string first = fixture.Dir("deleted-before-the-stop");
        string second = fixture.Dir("unreached");
        fixture.File(@"deleted-before-the-stop\a.bin", 4096);
        fixture.File(@"unreached\b.bin", 4096);

        using var cts = new CancellationTokenSource();

        // Progress is reported after each removal, so cancelling from the first report stops the
        // run between the two items rather than at a moment the test has to guess at.
        var stopAfterTheFirst = new ImmediateProgress<ReclaimExecutionProgress>(_ => cts.Cancel());

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync(
            [Candidate(first, size: 4096), Candidate(second, size: 4096)],
            stopAfterTheFirst,
            cts.Token);

        Assert.IsTrue(outcome.Cancelled);
        Assert.AreEqual(1, outcome.RemovedCount);
        Assert.AreEqual(4096, outcome.BytesFreed, "Only what really went may be counted as freed.");

        // Deepest-first orders by path length, so the longer name is the one that went.
        Assert.IsFalse(Directory.Exists(first));
        Assert.IsTrue(Directory.Exists(second), "The unreached item must still be on disk.");

        ReclaimItemOutcome stopped = outcome.Items.Single(i => i.Candidate.Path == second);
        Assert.AreEqual(ReclaimItemStatus.Cancelled, stopped.Status);
        Assert.IsFalse(stopped.Removed, "A folder still on disk must never be reported as removed.");
    }

    [TestMethod]
    public async Task ProgressCountsRootsRatherThanEverythingTicked()
    {
        using var fixture = new ReclaimFixture();
        string parent = fixture.Dir("parent");
        string child = fixture.Dir("parent", "child");

        var reports = new List<ReclaimExecutionProgress>();

        await new ReclaimExecutor().ExecuteAsync(
            [Candidate(parent), Candidate(child)],
            new Progress<ReclaimExecutionProgress>(reports.Add),
            CancellationToken.None);

        // Progress is awaited indirectly: Progress<T> posts to the captured context, and this test
        // has none, so the callbacks land on the thread pool and may trail the returned task.
        await WaitFor(() => reports.Count > 0);

        Assert.AreEqual(1, reports[^1].Total, "the absorbed child was counted as work to do");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }
    /// <summary>
    /// A child of a container the guard refused must not be reported as removed. Reporting
    /// "absorbed" unconditionally converted a refusal into a success, and the ViewModel deletes every
    /// row it is told was removed — so the row left the table while the folder stayed on disk.
    /// </summary>
    [TestMethod]
    public async Task AChildOfARefusedContainerIsNotReportedAsRemoved()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        ReclaimOutcome outcome = await Execute(
            Candidate(windows), Candidate(Path.Combine(windows, "Temp")));

        Assert.AreEqual(0, outcome.RemovedCount);
        Assert.AreEqual(2, outcome.FailedCount);
        Assert.IsTrue(Directory.Exists(windows));
    }

    /// <summary>
    /// A cancelled run must not claim the children of containers it never reached.
    /// </summary>
    [TestMethod]
    public async Task ChildrenOfAnUnreachedContainerAreNotReportedAsRemoved()
    {
        using ReclaimFixture fixture = new();
        string outer = fixture.Dir("repo");
        string inner = fixture.Dir("repo", "bin");
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync(
            [Candidate(outer), Candidate(inner)], null, cts.Token);

        Assert.AreEqual(0, outcome.RemovedCount);
        Assert.IsTrue(outcome.Cancelled);
        Assert.IsTrue(Directory.Exists(outer), "Nothing should have been touched.");
    }
}