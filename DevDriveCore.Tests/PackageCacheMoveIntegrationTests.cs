using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Guarded integration test that exercises the REAL <see cref="SystemFileSystem"/> code path of the M4
/// package-cache move + revert — proving the move works against an actual disk, not just the in-memory
/// fake. It operates ENTIRELY inside a throwaway <c>%TEMP%</c> directory (created in setup, deleted in
/// <c>finally</c>) and uses a FAKE environment writer + in-memory store, so it NEVER moves the machine's
/// real npm cache and NEVER touches a real environment variable. Default options keep the source in
/// place, so the move is losslessly reversible.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class PackageCacheMoveIntegrationTests
{
    [TestMethod]
    public async Task RealFileSystem_MoveThenRevert_CopiesVerifiesAndRestores()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-movetest-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "npm-cache");
        string target = Path.Combine(root, "devdrive", "packages", "npm");
        const string envVar = "DDM_FAKE_CACHE_VAR_DO_NOT_USE"; // only ever read/written via the FAKE writer below

        try
        {
            // Arrange: real files on a real disk (inside %TEMP%).
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "index.json"), "{ \"name\": \"cache-root\" }");
            File.WriteAllBytes(Path.Combine(source, "blob.bin"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            File.WriteAllText(Path.Combine(source, "sub", "nested.txt"), "nested contents");

            var fs = new SystemFileSystem();                    // REAL filesystem under test
            var env = new FakeEnvironmentWriter();              // NEVER touches a real env var
            var store = new InMemoryReversibilityStore();
            var coordinator = new PackageCacheMoveCoordinator(new PackageCacheMover(fs, env, store), store);

            var plan = new PackageCacheMovePlan
            {
                ToolName = "npm",
                SourcePath = source,
                TargetPath = target,
                EnvironmentVariable = envVar,
            };

            // Act 1: move.
            CacheMoveOutcome moved = await coordinator.MoveAsync(plan);

            // Assert: real files copied + verified, env (fake) set, source kept in place.
            Assert.AreEqual(CacheMoveStatus.Moved, moved.Status, moved.ResultText);
            Assert.AreEqual(3, moved.FilesCopied);
            Assert.IsTrue(File.Exists(Path.Combine(target, "index.json")));
            Assert.IsTrue(File.Exists(Path.Combine(target, "blob.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(target, "sub", "nested.txt")), "Nested files keep their relative path on disk.");
            CollectionAssert.AreEqual(
                File.ReadAllBytes(Path.Combine(source, "blob.bin")),
                File.ReadAllBytes(Path.Combine(target, "blob.bin")),
                "Copied bytes must match the source exactly (hash-verified copy).");
            Assert.AreEqual(target, env.GetUserVariable(envVar));
            Assert.IsTrue(Directory.Exists(source), "Default options keep the source in place for a lossless revert.");
            Assert.IsTrue(coordinator.CanMoveBack(envVar));

            // Act 2: move back (source still exists → restore the env var and remove the Dev Drive copy).
            CacheMoveOutcome back = await coordinator.MoveBackAsync(envVar, "npm");

            // Assert: env (fake) restored to its prior (unset) value; the copy is removed (never orphaned).
            Assert.AreEqual(CacheMoveStatus.MovedBack, back.Status, back.ResultText);
            Assert.IsNull(env.GetUserVariable(envVar), "Env var restored to its prior unset state.");
            Assert.IsTrue(Directory.Exists(source), "Source is intact after move-back.");
            Assert.IsFalse(Directory.Exists(target), "C1: the Dev Drive copy is removed on move-back, not orphaned.");
            Assert.IsFalse(coordinator.CanMoveBack(envVar), "Reverted move is no longer move-back-able.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
