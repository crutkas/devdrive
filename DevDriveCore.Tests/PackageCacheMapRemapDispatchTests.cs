using System.Diagnostics;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;

namespace DevDriveCore.Tests;

/// <summary>
/// Headless tests for the new "Map path" / "Move &amp; remap" affordances in <see cref="PackageCachesViewModel"/>
/// — what the per-ecosystem card offers for a tool whose cache wasn't auto-detected, instead of a dead
/// "Not found". Both reuse the existing env-writer + reversible move plumbing; these tests pin that the
/// row's confirm dispatches to the RIGHT engine path by <see cref="PackageCacheRowViewModel.PendingAction"/>:
/// Map repoints the variable with NO copy (and preserves the user's folder), while Move &amp; remap relocates
/// the chosen folder to the Dev Drive AND repoints. Everything runs over in-memory seams — no real cache,
/// variable, or disk is touched.
/// </summary>
[TestClass]
public sealed class PackageCacheMapRemapDispatchTests
{
    private const string EnvVar = "GOMODCACHE";

    [TestMethod]
    public async Task MapPath_ForUndetectedTool_PointsVariable_WithoutCopying_AndFlipsRowToMapped()
    {
        (PackageCachesViewModel vm, FakeEnvironmentWriter env, InMemoryFileSystem fs) = BuildUndetected();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 1);

        PackageCacheRowViewModel row = vm.Caches[0];
        Assert.IsTrue(row.ShowMapBlock, "An undetected tool offers the map block, not a dead 'Not found'.");

        row.MapPath = @"C:\dev\gomodcache";
        fs.AddFile(@"C:\dev\gomodcache\keep.txt", "keep"); // the user's existing folder

        row.MapPathActionCommand.Execute(null);
        Assert.IsTrue(row.IsConfirmingMove);
        Assert.AreEqual(PendingCacheAction.Map, row.PendingAction);

        row.ConfirmMoveCommand.Execute(null);
        await WaitUntilAsync(() => row.IsMapped && row.ShowMoveResult);

        Assert.AreEqual(@"C:\dev\gomodcache", env.GetUserVariable(EnvVar), "Map repoints the per-user variable.");
        Assert.AreEqual(0, fs.CopyCount, "Map copies nothing.");
        Assert.IsTrue(fs.FileExists(@"C:\dev\gomodcache\keep.txt"), "The user's folder is preserved.");
        Assert.IsFalse(row.ShowMapBlock, "Once mapped, the map block is replaced by the result.");
        Assert.IsTrue(row.CanMoveBack, "A mapped tool offers a reversible 'Move back'.");
    }

    [TestMethod]
    public async Task MoveAndRemap_ForUndetectedTool_MovesChosenFolderToDevDrive_AndSetsTheVariable()
    {
        (PackageCachesViewModel vm, FakeEnvironmentWriter env, InMemoryFileSystem fs) = BuildUndetected();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 1);

        PackageCacheRowViewModel row = vm.Caches[0];
        row.MapPath = @"C:\dev\gomodcache";
        fs.AddFile(@"C:\dev\gomodcache\a.txt", "alpha");
        fs.AddFile(@"C:\dev\gomodcache\b.txt", "bravo-bravo");

        row.MoveAndRemapCommand.Execute(null);
        Assert.IsTrue(row.IsConfirmingMove);
        Assert.AreEqual(PendingCacheAction.Remap, row.PendingAction);

        row.ConfirmMoveCommand.Execute(null);
        await WaitUntilAsync(() => row.IsSet && !row.IsMoving && row.ShowMoveResult);

        Assert.AreEqual(@"G:\packages\go", env.GetUserVariable(EnvVar), "Remap repoints the variable at the Dev Drive copy.");
        Assert.IsTrue(fs.FileExists(@"G:\packages\go\a.txt"), "The chosen folder's files were copied to the Dev Drive.");
        Assert.IsTrue(fs.FileExists(@"G:\packages\go\b.txt"));
        Assert.IsTrue(fs.FileExists(@"C:\dev\gomodcache\a.txt"), "The default move keeps the source in place (so it's reversible).");
        Assert.IsTrue(row.CanMoveBack);
        Assert.IsFalse(row.CanMove);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    [TestMethod]
    public async Task MoveAndRemap_IsUnavailable_UntilTheChosenFolderExists()
    {
        (PackageCachesViewModel vm, _, InMemoryFileSystem fs) = BuildUndetected();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 1);

        PackageCacheRowViewModel row = vm.Caches[0];

        // A folder that isn't there cannot be moved — only Map path (repoint the variable) applies.
        row.MapPath = @"C:\dev\does-not-exist";
        Assert.IsFalse(row.CanMoveAndRemap, "You cannot move a cache folder that isn't present.");
        Assert.IsFalse(row.MoveAndRemapCommand.CanExecute(null), "So the Move & remap button is hidden/disabled.");

        // Point it at a folder that DOES exist → Move & remap becomes available.
        fs.AddFile(@"C:\dev\real-cache\a.txt", "alpha");
        row.MapPath = @"C:\dev\real-cache";
        Assert.IsTrue(row.CanMoveAndRemap, "Once the folder exists, there is something to move.");
        Assert.IsTrue(row.MoveAndRemapCommand.CanExecute(null));
    }

    private static (PackageCachesViewModel Vm, FakeEnvironmentWriter Env, InMemoryFileSystem Fs) BuildUndetected()
    {
        var fs = new InMemoryFileSystem();
        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        var coordinator = new PackageCacheMoveCoordinator(new PackageCacheMover(fs, env, store), store);

        var info = new PackageCacheInfo
        {
            Name = "Go", // a clean token so the in-memory target is G:\packages\go
            EnvironmentVariable = EnvVar,
            ResolvedPath = string.Empty,
            Detected = false,
            DriveLetter = null,
            OnDevDrive = false,
        };

        var service = new SingleUndetectedCacheService(info);
        return (new PackageCachesViewModel(service, coordinator, fs.DirectoryExists), env, fs);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("The expected ViewModel state was not reached within the timeout.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>Returns a single UNDETECTED cache and a Dev-Drive-targeted plan (so map/remap have something to act on).</summary>
    private sealed class SingleUndetectedCacheService : IPackageCacheService
    {
        private readonly PackageCacheInfo _info;

        public SingleUndetectedCacheService(PackageCacheInfo info) => _info = info;

        public IReadOnlyList<PackageCacheInfo> GetPackageCaches(char devDriveLetter) => new[] { _info };

        public Task<ulong> CalculateSizeAsync(PackageCacheInfo cache, TimeSpan timeBudget, CancellationToken cancellationToken = default) =>
            Task.FromResult(0UL);

        public PackageCacheMovePlan BuildMovePlan(PackageCacheInfo cache, char devDriveLetter) => new()
        {
            ToolName = cache.Name,
            SourcePath = cache.ResolvedPath,
            TargetPath = $@"{char.ToUpperInvariant(devDriveLetter)}:\packages\{cache.Name.ToLowerInvariant()}",
            EnvironmentVariable = cache.EnvironmentVariable,
        };
    }
}
