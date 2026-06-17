using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="PackageCacheMover"/> (the app-composed, confirmation-gated cache mover). All
/// operations run against an in-memory filesystem + fake environment writer + in-memory store, so NO
/// real cache, file, or environment variable is ever touched.
/// </summary>
[TestClass]
public sealed class PackageCacheMoverTests
{
    private const string Source = @"C:\Users\dev\AppData\Local\npm-cache";
    private const string Target = @"G:\packages\npm";
    private const string EnvVar = "npm_config_cache";

    private static (PackageCacheMover Mover, InMemoryFileSystem Fs, FakeEnvironmentWriter Env, InMemoryReversibilityStore Store) Build()
    {
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        return (new PackageCacheMover(fs, env, store), fs, env, store);
    }

    private static PackageCacheMovePlan Plan(string source = Source, string target = Target, string envVar = EnvVar) =>
        new() { ToolName = "npm", SourcePath = source, TargetPath = target, EnvironmentVariable = envVar };

    private static void SeedSource(InMemoryFileSystem fs)
    {
        fs.AddFile(Source + @"\a.txt", "alpha");
        fs.AddFile(Source + @"\b.txt", "bravo");
        fs.AddFile(Source + @"\sub\c.txt", "charlie");
    }

    // ---- happy path ----------------------------------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_HappyPath_CopiesVerifiesAndSetsEnv()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());

        Assert.IsTrue(receipt.Success);
        Assert.IsFalse(receipt.AlreadyOnDevDrive);
        Assert.AreEqual(3, receipt.FilesCopied);
        Assert.IsTrue(fs.FileExists(Target + @"\a.txt"));
        Assert.IsTrue(fs.FileExists(Target + @"\b.txt"));
        Assert.IsTrue(fs.FileExists(Target + @"\sub\c.txt"), "Nested files keep their relative path.");
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
        // Default options keep the source in place.
        Assert.IsTrue(fs.DirectoryExists(Source));
        Assert.IsFalse(receipt.SourceDeleted);

        ReversibilityEntry? entry = store.TryGet(receipt.ReversibilityId);
        Assert.IsNotNull(entry);
        Assert.AreEqual(ReversibilityKinds.PackageCacheMove, entry!.Kind);
        Assert.IsFalse(entry.EnvironmentValueWasSet, "Variable was previously unset.");
    }

    [TestMethod]
    public async Task MoveAsync_ReportsProgress()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, _, _) = Build();
        SeedSource(fs);
        // Synchronous IProgress so reports are captured in order, before the awaited task ends.
        var progress = new RecordingProgress<CacheMoveProgress>();

        await mover.MoveAsync(Plan(), progress);

        IReadOnlyList<CacheMoveProgress> reports = progress.Reports;
        Assert.HasCount(4, reports); // 1 initial snapshot + 3 files
        CacheMoveProgress final = reports[^1];
        Assert.AreEqual(3, final.FilesCompleted);
        Assert.AreEqual(3, final.TotalFiles);
        Assert.AreEqual(1.0d, final.Fraction, 1e-9);
        // FilesCompleted is monotonically non-decreasing across reports.
        for (int i = 1; i < reports.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(reports[i - 1].FilesCompleted, reports[i].FilesCompleted);
        }
    }

    [TestMethod]
    public async Task MoveAsync_EmptySource_StillCreatesTargetAndSetsEnv()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        // Source directory does not exist (cache never created).

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());

        Assert.IsTrue(receipt.Success);
        Assert.AreEqual(0, receipt.FilesCopied);
        Assert.IsTrue(fs.DirectoryExists(Target));
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
        Assert.IsNotNull(store.TryGet(receipt.ReversibilityId));
    }

    // ---- env capture ---------------------------------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_CapturesPriorEnvValue()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);
        env.Seed(EnvVar, @"D:\old-cache");

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());

        Assert.IsTrue(receipt.PriorEnvironmentValueWasSet);
        Assert.AreEqual(@"D:\old-cache", receipt.PriorEnvironmentValue);
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));

        ReversibilityEntry entry = store.TryGet(receipt.ReversibilityId)!;
        Assert.IsTrue(entry.EnvironmentValueWasSet);
        Assert.AreEqual(@"D:\old-cache", entry.PriorEnvironmentValue);
    }

    // ---- idempotency ---------------------------------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_AlreadyOnDevDrive_IsNoOp()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);
        env.Seed(EnvVar, Target);

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());

        Assert.IsTrue(receipt.Success);
        Assert.IsTrue(receipt.AlreadyOnDevDrive);
        Assert.AreEqual(0, fs.CopyCount, "Nothing should be copied.");
        Assert.IsEmpty(env.SetCalls, "Env var should not be touched.");
        Assert.AreEqual(string.Empty, receipt.ReversibilityId);
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    [DataRow(@"G:\packages\npm\")]   // trailing slash
    [DataRow("G:/packages/npm")]     // forward slashes
    public async Task MoveAsync_IdempotencyNormalizesPaths(string priorEnv)
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        env.Seed(EnvVar, priorEnv);

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());

        Assert.IsTrue(receipt.AlreadyOnDevDrive);
        Assert.AreEqual(0, fs.CopyCount);
    }

    [TestMethod]
    public async Task MoveAsync_RunTwice_SecondIsNoOp()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, _, _) = Build();
        SeedSource(fs);

        await mover.MoveAsync(Plan());
        PackageCacheMoveReceipt second = await mover.MoveAsync(Plan());

        Assert.IsTrue(second.AlreadyOnDevDrive);
    }

    // ---- delete-source option ------------------------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_DeleteSourceAfterVerify_RemovesSource()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, _, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(
            Plan(), options: new CacheMoveOptions { DeleteSourceAfterVerify = true });

        Assert.IsTrue(receipt.SourceDeleted);
        Assert.IsFalse(fs.DirectoryExists(Source));
        Assert.IsTrue(fs.FileExists(Target + @"\a.txt"));
        Assert.IsTrue(store.TryGet(receipt.ReversibilityId)!.SourceDeleted);
    }

    // ---- partial-failure rollback --------------------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_CopyFailure_RollsBackAndLeavesEnvUntouched()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);
        fs.FailCopyToContaining = "b.txt"; // second file (sorted: a, b, sub\c)

        await Assert.ThrowsExactlyAsync<IOException>(async () => await mover.MoveAsync(Plan()));

        // Rollback: target removed (didn't pre-exist), nothing left behind.
        Assert.IsFalse(fs.DirectoryExists(Target), "Target dir created by the move must be rolled back.");
        Assert.IsFalse(fs.FileExists(Target + @"\a.txt"), "Already-copied file must be rolled back.");
        // Env var never set; no reversibility entry recorded.
        Assert.IsEmpty(env.SetCalls);
        Assert.IsEmpty(store.GetAll());
        // Source is untouched.
        Assert.IsTrue(fs.FileExists(Source + @"\a.txt"));
    }

    [TestMethod]
    public async Task MoveAsync_HashMismatch_RollsBack()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        fs.CorruptCopyToContaining = "a.txt"; // copied bytes differ -> SHA-256 mismatch

        await Assert.ThrowsExactlyAsync<IOException>(async () => await mover.MoveAsync(Plan()));

        Assert.IsFalse(fs.DirectoryExists(Target));
        Assert.IsEmpty(env.SetCalls);
    }

    [TestMethod]
    public async Task MoveAsync_VerifyHashesDisabled_DoesNotFailOnCorruptCopy()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        fs.CorruptCopyToContaining = "a.txt";

        PackageCacheMoveReceipt receipt = await mover.MoveAsync(
            Plan(), options: new CacheMoveOptions { VerifyHashes = false });

        Assert.IsTrue(receipt.Success);
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
    }

    [TestMethod]
    public async Task MoveAsync_PreexistingTarget_NotDeletedOnRollback()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, _, _) = Build();
        SeedSource(fs);
        fs.AddFile(Target + @"\keep.txt", "preexisting"); // target pre-exists with content
        fs.AddFile(Target + @"\" + PackageCacheMover.OwnershipMarkerFileName, "owned"); // …and is app-owned
        fs.FailCopyToContaining = "b.txt";

        await Assert.ThrowsExactlyAsync<IOException>(async () => await mover.MoveAsync(Plan()));

        Assert.IsTrue(fs.DirectoryExists(Target), "Pre-existing app-owned target must NOT be deleted on rollback.");
        Assert.IsTrue(fs.FileExists(Target + @"\keep.txt"), "Pre-existing content must be preserved.");
        Assert.IsFalse(fs.FileExists(Target + @"\a.txt"), "Only copied-by-us files are rolled back.");
    }

    // ---- F4: ownership marker (don't clobber/delete pre-existing user data) ---------------------

    [TestMethod]
    public async Task MoveAsync_NonEmptyUnownedTarget_IsRefused()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);
        fs.AddFile(Target + @"\user-data.txt", "important"); // pre-existing, non-empty, NO ownership marker

        await Assert.ThrowsExactlyAsync<IOException>(async () => await mover.MoveAsync(Plan()));

        Assert.IsTrue(fs.DirectoryExists(Target), "A refused move must leave the user's target intact.");
        Assert.IsTrue(fs.FileExists(Target + @"\user-data.txt"), "Pre-existing user data must be preserved.");
        Assert.IsFalse(fs.FileExists(Target + @"\a.txt"), "Nothing should be copied into an unowned target.");
        Assert.IsFalse(
            fs.FileExists(Target + @"\" + PackageCacheMover.OwnershipMarkerFileName),
            "No ownership marker should be written into a refused target.");
        Assert.IsEmpty(env.SetCalls, "Env var must not be touched when the move is refused.");
        Assert.IsEmpty(store.GetAll(), "No reversibility entry should be recorded for a refused move.");
    }

    [TestMethod]
    public async Task RevertAsync_PreexistingUnownedTarget_IsPreserved()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();

        // A recorded move whose target points at a folder the app does NOT own (no marker) holding user
        // data — revert must restore the env var and remove the entry, but NEVER recursively delete the
        // unowned target.
        fs.AddFile(Target + @"\user-data.txt", "important");
        env.Seed(EnvVar, Target);
        var entry = new ReversibilityEntry
        {
            Id = PackageCacheMover.ReversibilityId(EnvVar),
            Kind = ReversibilityKinds.PackageCacheMove,
            ToolName = "npm",
            EnvironmentVariable = EnvVar,
            EnvironmentValueWasSet = false,
            PriorEnvironmentValue = null,
            SourcePath = Source,
            TargetPath = Target,
            SourceDeleted = false,
        };
        store.Save(entry);

        bool reverted = await mover.RevertAsync(entry.Id);

        Assert.IsTrue(reverted);
        Assert.IsTrue(fs.DirectoryExists(Target), "An unowned target (no marker) must be preserved on revert.");
        Assert.IsTrue(fs.FileExists(Target + @"\user-data.txt"), "User data in an unowned target must survive revert.");
        Assert.IsNull(env.GetUserVariable(EnvVar), "Env var restored to its prior (unset) state.");
        Assert.IsNull(store.TryGet(entry.Id), "Reverted entry should be removed.");
    }

    // ---- revert --------------------------------------------------------------------------------

    [TestMethod]
    public async Task RevertAsync_PriorEnvUnset_RemovesVariable()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);
        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));

        bool reverted = await mover.RevertAsync(receipt.ReversibilityId);

        Assert.IsTrue(reverted);
        Assert.IsNull(env.GetUserVariable(EnvVar), "Variable should be removed (it was unset before).");
        Assert.IsNull(store.TryGet(receipt.ReversibilityId), "Entry should be removed.");
        // C1: the Dev Drive copy is ALWAYS removed on revert (never orphaned). The source was kept, so
        // no copy-back is needed and no data is lost.
        Assert.IsFalse(fs.DirectoryExists(Target), "Dev Drive copy must be removed on revert.");
        Assert.IsFalse(fs.FileExists(Target + @"\a.txt"), "Dev Drive copy must be removed on revert.");
        Assert.IsTrue(fs.FileExists(Source + @"\a.txt"), "Kept source must remain intact.");
    }

    [TestMethod]
    public async Task RevertAsync_PriorEnvSet_RestoresValue()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        env.Seed(EnvVar, @"D:\old-cache");
        PackageCacheMoveReceipt receipt = await mover.MoveAsync(Plan());

        await mover.RevertAsync(receipt.ReversibilityId);

        Assert.AreEqual(@"D:\old-cache", env.GetUserVariable(EnvVar));
    }

    [TestMethod]
    public async Task RevertAsync_SourceWasDeleted_RestoresSourceAndRemovesCopy()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, _, _) = Build();
        SeedSource(fs);
        PackageCacheMoveReceipt receipt = await mover.MoveAsync(
            Plan(), options: new CacheMoveOptions { DeleteSourceAfterVerify = true });
        Assert.IsFalse(fs.DirectoryExists(Source));

        // C1: copy-back is driven by the recorded SourceDeleted state, NOT the caller flag — so it must
        // restore the source even though moveFilesBack defaults to false.
        bool reverted = await mover.RevertAsync(receipt.ReversibilityId);

        Assert.IsTrue(reverted);
        Assert.IsTrue(fs.FileExists(Source + @"\a.txt"), "Files should be copied back to the deleted source.");
        Assert.IsTrue(fs.FileExists(Source + @"\sub\c.txt"));
        Assert.IsFalse(fs.DirectoryExists(Target), "Dev Drive copy should be removed after copying files back.");
    }

    [TestMethod]
    public async Task RevertAsync_UnknownId_ReturnsFalse()
    {
        (PackageCacheMover mover, _, _, _) = Build();
        Assert.IsFalse(await mover.RevertAsync("package-cache:does-not-exist"));
    }

    [TestMethod]
    public async Task MoveAsync_DeleteSource_SaveOfDeleteIntentFails_KeepsSource_NoDataLoss()
    {
        // F12: the SourceDeleted=true receipt is persisted BEFORE the source is deleted. If that save
        // fails (≈ a crash in that window), the source — the only verified copy at that moment — must
        // remain intact, and the durable receipt must NOT claim the source was deleted, so a later
        // revert can never delete the sole remaining copy.
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var inner = new InMemoryReversibilityStore();
        var store = new ThrowOnSourceDeletedSaveStore(inner);
        var mover = new PackageCacheMover(fs, env, store);
        SeedSource(fs);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await mover.MoveAsync(Plan(), options: new CacheMoveOptions { DeleteSourceAfterVerify = true }));

        Assert.IsTrue(fs.DirectoryExists(Source), "Source must survive when recording the delete intent fails.");
        Assert.IsTrue(fs.FileExists(Source + @"\a.txt"));

        IReadOnlyList<ReversibilityEntry> all = inner.GetAll();
        Assert.HasCount(1, all);
        Assert.IsFalse(all[0].SourceDeleted, "Persisted receipt must not claim the source was deleted.");
    }

    /// <summary>
    /// Test double that simulates a failure persisting the delete-intent receipt: the first
    /// (SourceDeleted=false) save succeeds, but the SourceDeleted=true save throws — modelling a crash
    /// in the persist-before-delete window that F12 guards against.
    /// </summary>
    private sealed class ThrowOnSourceDeletedSaveStore : IReversibilityStore
    {
        private readonly InMemoryReversibilityStore _inner;

        public ThrowOnSourceDeletedSaveStore(InMemoryReversibilityStore inner) => _inner = inner;

        public void Save(ReversibilityEntry entry)
        {
            if (entry.SourceDeleted)
            {
                throw new IOException("Simulated failure persisting the delete-intent receipt.");
            }

            _inner.Save(entry);
        }

        public ReversibilityEntry? TryGet(string id) => _inner.TryGet(id);

        public IReadOnlyList<ReversibilityEntry> GetAll() => _inner.GetAll();

        public bool Remove(string id) => _inner.Remove(id);
    }

    [TestMethod]
    public async Task RevertAsync_WrongKind_ReturnsFalse()
    {
        (PackageCacheMover mover, _, _, InMemoryReversibilityStore store) = Build();
        store.Save(new ReversibilityEntry { Id = "x", Kind = ReversibilityKinds.VhdProvision });

        Assert.IsFalse(await mover.RevertAsync("x"));
        Assert.IsNotNull(store.TryGet("x"), "A wrong-kind entry must be left untouched.");
    }

    // ---- seam + guards -------------------------------------------------------------------------

    [TestMethod]
    public async Task ApplyAsync_RunsFullMoveWithDefaults()
    {
        (PackageCacheMover mover, InMemoryFileSystem fs, FakeEnvironmentWriter env, InMemoryReversibilityStore store) = Build();
        SeedSource(fs);

        await mover.ApplyAsync(Plan());

        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
        Assert.HasCount(1, store.GetAll());
    }

    [TestMethod]
    public void Ctor_NullArgs_Throw()
    {
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        Assert.ThrowsExactly<ArgumentNullException>(() => new PackageCacheMover(null!, env, store));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PackageCacheMover(fs, null!, store));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PackageCacheMover(fs, env, null!));
    }

    [TestMethod]
    public async Task MoveAsync_NullPlan_Throws()
    {
        (PackageCacheMover mover, _, _, _) = Build();
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await mover.MoveAsync(null!));
    }

    [TestMethod]
    public async Task MoveAsync_BlankTargetOrEnv_Throws()
    {
        (PackageCacheMover mover, _, _, _) = Build();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await mover.MoveAsync(Plan(target: "  ")));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await mover.MoveAsync(Plan(envVar: "  ")));
    }

    [TestMethod]
    public void ReversibilityId_HasStableFormat()
    {
        Assert.AreEqual("package-cache:npm_config_cache", PackageCacheMover.ReversibilityId("npm_config_cache"));
    }

    [TestMethod]
    public void CreateDefault_ReturnsInstance()
    {
        Assert.IsNotNull(PackageCacheMover.CreateDefault());
    }
}
