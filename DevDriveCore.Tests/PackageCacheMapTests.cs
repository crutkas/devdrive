using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the new "Map path" engine path — the no-copy, reversible repoint of a per-user environment
/// variable used when a tool's cache wasn't auto-detected but the user points us at it. It reuses the
/// existing mover env-writer + reversibility plumbing, so the SAFETY contract is the load-bearing thing
/// to pin: Map writes NO ownership marker, so a later "Move back" restores ONLY the variable and PRESERVES
/// the user's folder (never recursively deletes it). All I/O flows through in-memory fakes — no real cache,
/// variable, or disk is touched.
/// </summary>
[TestClass]
public sealed class PackageCacheMapTests
{
    private const string EnvVar = "GOMODCACHE";
    private const string Target = @"G:\packages\go";

    private static (PackageCacheMoveCoordinator Coordinator, PackageCacheMover Mover, InMemoryFileSystem Fs, FakeEnvironmentWriter Env, InMemoryReversibilityStore Store)
        Build()
    {
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        var mover = new PackageCacheMover(fs, env, store);
        return (new PackageCacheMoveCoordinator(mover, store), mover, fs, env, store);
    }

    [TestMethod]
    public async Task MapPathAsync_PointsVariableAtChosenFolder_WithoutCopying_AndIsReversible()
    {
        (PackageCacheMoveCoordinator coordinator, _, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        // The user's chosen folder already exists with files; Map must NOT copy or disturb them.
        fs.AddFile(Target + @"\mod\x.txt", "x");

        CacheMoveOutcome outcome = await coordinator.MapPathAsync(EnvVar, "Go modules", Target);

        Assert.AreEqual(CacheMoveStatus.Mapped, outcome.Status);
        Assert.IsTrue(outcome.Succeeded);
        Assert.IsFalse(outcome.IsOnDevDrive, "A map is not a move — the cache wasn't relocated to the Dev Drive.");
        Assert.IsTrue(outcome.CanMoveBack, "A recorded map must offer a reversible 'Move back'.");
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
        Assert.AreEqual(0, fs.CopyCount, "Map copies nothing.");
        Assert.IsTrue(coordinator.CanMoveBack(EnvVar));
        StringAssert.Contains(outcome.ResultText, Target);
    }

    [TestMethod]
    public async Task MapPathAsync_ThenMoveBack_RestoresPriorValue_AndPreservesTheUsersFolder()
    {
        (PackageCacheMoveCoordinator coordinator, _, InMemoryFileSystem fs, FakeEnvironmentWriter env, _) = Build();
        env.Seed(EnvVar, @"C:\old\go"); // a prior value that move-back must restore
        fs.AddFile(Target + @"\mod\x.txt", "x");

        await coordinator.MapPathAsync(EnvVar, "Go modules", Target);
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));

        CacheMoveOutcome back = await coordinator.MoveBackAsync(EnvVar, "Go modules");

        Assert.AreEqual(CacheMoveStatus.MovedBack, back.Status);
        Assert.AreEqual(@"C:\old\go", env.GetUserVariable(EnvVar), "Move-back restores the prior environment value.");
        Assert.IsTrue(fs.FileExists(Target + @"\mod\x.txt"), "Map's revert must PRESERVE the user's folder (no ownership marker was written).");
        Assert.IsFalse(coordinator.CanMoveBack(EnvVar), "The reversibility entry is consumed by the move-back.");
    }

    [TestMethod]
    public async Task MapPathAsync_IsIdempotent_WhenVariableAlreadyPointsThere_AndWritesNothing()
    {
        (PackageCacheMoveCoordinator coordinator, _, _, FakeEnvironmentWriter env, _) = Build();
        env.Seed(EnvVar, Target);

        CacheMoveOutcome outcome = await coordinator.MapPathAsync(EnvVar, "Go modules", Target);

        Assert.AreEqual(CacheMoveStatus.Mapped, outcome.Status);
        Assert.IsEmpty(env.SetCalls, "An idempotent map performs no environment write.");
        Assert.AreEqual(Target, env.GetUserVariable(EnvVar));
    }

    [TestMethod]
    public async Task MapAsync_WritesNoOwnershipMarker_SoRevertNeverDeletesTheUsersFolder()
    {
        (_, PackageCacheMover mover, InMemoryFileSystem fs, _, _) = Build();
        fs.AddFile(Target + @"\keep.txt", "keep");

        PackageCacheMoveReceipt receipt = await mover.MapAsync(EnvVar, "Go modules", Target);

        Assert.IsTrue(receipt.Success);
        Assert.AreEqual(0, receipt.FilesCopied);
        Assert.IsFalse(
            fs.FileExists(Target + "\\" + PackageCacheMover.OwnershipMarkerFileName),
            "Map must NOT claim ownership of the user's folder.");

        bool reverted = await mover.RevertAsync(PackageCacheMover.ReversibilityId(EnvVar));
        Assert.IsTrue(reverted);
        Assert.IsTrue(fs.FileExists(Target + @"\keep.txt"), "Revert preserves the unowned folder.");
    }

    [TestMethod]
    public async Task MapPathAsync_RejectsBlankEnvironmentVariableOrTarget()
    {
        (PackageCacheMoveCoordinator coordinator, _, _, _, _) = Build();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await coordinator.MapPathAsync("", "Go", Target));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await coordinator.MapPathAsync(EnvVar, "Go", "  "));
    }
}
