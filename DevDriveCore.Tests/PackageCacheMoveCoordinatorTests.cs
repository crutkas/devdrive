using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the M4 <see cref="PackageCacheMoveCoordinator"/> — the UI-facing seam the app wires up.
/// It runs over the REAL <see cref="PackageCacheMover"/> with an in-memory filesystem + fake
/// environment writer + in-memory store (shared between mover and coordinator), so NO real cache,
/// file, or environment variable is touched. Asserts the projected <see cref="CacheMoveOutcome"/>, the
/// move-back path, idempotency, and that an expected failure becomes a non-throwing Failed outcome so
/// a "Move all" sweep can continue.
/// </summary>
[TestClass]
public sealed class PackageCacheMoveCoordinatorTests
{
    private const string Source = @"C:\Users\dev\AppData\Local\npm-cache";
    private const string Target = @"G:\packages\npm";
    private const string EnvVar = "npm_config_cache";

    private static (PackageCacheMoveCoordinator Coordinator, InMemoryFileSystem Fs, FakeEnvironmentWriter Env, InMemoryReversibilityStore Store)
        Build()
    {
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        var mover = new PackageCacheMover(fs, env, store);
        return (new PackageCacheMoveCoordinator(mover, store), fs, env, store);
    }

    private static PackageCacheMovePlan Plan(string source = Source, string target = Target, string envVar = EnvVar) =>
        new() { ToolName = "npm", SourcePath = source, TargetPath = target, EnvironmentVariable = envVar };

    private static void SeedSource(InMemoryFileSystem fs)
    {
        fs.AddFile(Source + @"\a.txt", "alpha");
        fs.AddFile(Source + @"\b.txt", "bravo-bravo");
    }

    // ---- Move (happy path) ---------------------------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_HappyPath_ReturnsMovedOutcomeAndEnablesMoveBack()
    {
        (PackageCacheMoveCoordinator coordinator, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);

        CacheMoveOutcome outcome = await coordinator.MoveAsync(Plan());

        Assert.AreEqual(CacheMoveStatus.Moved, outcome.Status);
        Assert.IsTrue(outcome.Succeeded);
        Assert.IsTrue(outcome.IsOnDevDrive);
        Assert.IsTrue(outcome.CanMoveBack);
        Assert.AreEqual(2, outcome.FilesCopied);
        Assert.IsGreaterThan(0L, outcome.BytesCopied);
        StringAssert.Contains(outcome.ResultText, "Moved");
        StringAssert.Contains(outcome.ResultText, Target);
        StringAssert.Contains(outcome.ResultText, EnvVar);
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
        Assert.IsTrue(coordinator.CanMoveBack(EnvVar), "A recorded move must be move-back-able.");
    }

    // ---- Idempotency: already on the Dev Drive -------------------------------------------------

    [TestMethod]
    public async Task MoveAsync_AlreadyPointingAtTarget_ReportsAlreadyOnDevDrive()
    {
        (PackageCacheMoveCoordinator coordinator, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        env.Seed(EnvVar, Target); // prior launch already moved it

        CacheMoveOutcome outcome = await coordinator.MoveAsync(Plan());

        Assert.AreEqual(CacheMoveStatus.AlreadyOnDevDrive, outcome.Status);
        Assert.IsTrue(outcome.IsOnDevDrive);
        Assert.IsTrue(outcome.CanMoveBack);
        Assert.AreEqual(0, outcome.FilesCopied);
        StringAssert.Contains(outcome.ResultText, "Dev Drive");
    }

    // ---- Move back -----------------------------------------------------------------------------

    [TestMethod]
    public async Task MoveBackAsync_AfterMove_RestoresEnvAndDisablesMoveBack()
    {
        (PackageCacheMoveCoordinator coordinator, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        await coordinator.MoveAsync(Plan());

        CacheMoveOutcome back = await coordinator.MoveBackAsync(EnvVar, "npm");

        Assert.AreEqual(CacheMoveStatus.MovedBack, back.Status);
        Assert.IsTrue(back.Succeeded);
        Assert.IsNull(env.GetUserVariable(EnvVar), "Env var restored to its prior (unset) value.");
        Assert.IsTrue(fs.DirectoryExists(Source), "Source was kept, so it still exists after move-back.");
        Assert.IsFalse(fs.DirectoryExists(Target), "C1: the Dev Drive copy must not be orphaned on move-back.");
        Assert.IsFalse(coordinator.CanMoveBack(EnvVar), "Reverted move is no longer move-back-able.");
    }

    [TestMethod]
    public async Task MoveBackAsync_NothingRecorded_ReportsNothingToRevert()
    {
        (PackageCacheMoveCoordinator coordinator, _, _, _) = Build();

        CacheMoveOutcome back = await coordinator.MoveBackAsync(EnvVar, "npm");

        Assert.AreEqual(CacheMoveStatus.NothingToRevert, back.Status);
        Assert.IsTrue(back.Succeeded, "Nothing-to-revert is a benign success, not a failure.");
    }

    // ---- Failure is projected, not thrown (so Move-all can continue) ---------------------------

    [TestMethod]
    public async Task MoveAsync_CopyFailure_ReturnsFailedOutcomeAndLeavesEnvUntouched()
    {
        (PackageCacheMoveCoordinator coordinator, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        SeedSource(fs);
        fs.FailCopyToContaining = "packages"; // every copy into the target fails

        CacheMoveOutcome outcome = await coordinator.MoveAsync(Plan());

        Assert.AreEqual(CacheMoveStatus.Failed, outcome.Status);
        Assert.IsFalse(outcome.Succeeded);
        Assert.IsFalse(outcome.IsOnDevDrive);
        Assert.IsNull(env.GetUserVariable(EnvVar), "A failed move must leave the env var untouched.");
        Assert.IsFalse(coordinator.CanMoveBack(EnvVar));
        Assert.IsNotNull(outcome.ErrorMessage);
    }

    [TestMethod]
    public async Task MoveAsync_Cancelled_ReturnsCancelledOutcome()
    {
        (PackageCacheMoveCoordinator coordinator, InMemoryFileSystem fs, _, _) = Build();
        SeedSource(fs);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        CacheMoveOutcome outcome = await coordinator.MoveAsync(Plan(), progress: null, cts.Token);

        Assert.AreEqual(CacheMoveStatus.Cancelled, outcome.Status);
    }

    // ---- Cross-launch: a new coordinator over the same persisted store sees move-back ----------

    [TestMethod]
    public async Task CanMoveBack_PersistsAcrossCoordinatorInstances_SharingStore()
    {
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore(); // stands in for the persistent JSON store
        SeedSource(fs);

        var firstLaunch = new PackageCacheMoveCoordinator(new PackageCacheMover(fs, env, store), store);
        await firstLaunch.MoveAsync(Plan());

        // Simulate a fresh app launch: a brand-new coordinator over the SAME store.
        var secondLaunch = new PackageCacheMoveCoordinator(new PackageCacheMover(fs, env, store), store);
        Assert.IsTrue(secondLaunch.CanMoveBack(EnvVar), "Move-back must survive a relaunch via the shared store.");
    }

    [TestMethod]
    public void CanMoveBack_UnknownVariable_IsFalse()
    {
        (PackageCacheMoveCoordinator coordinator, _, _, _) = Build();
        Assert.IsFalse(coordinator.CanMoveBack("nonexistent_cache"));
        Assert.IsFalse(coordinator.CanMoveBack(""));
    }
}
