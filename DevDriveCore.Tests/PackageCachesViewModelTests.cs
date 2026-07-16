using System.Diagnostics;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;

namespace DevDriveCore.Tests;

/// <summary>
/// Headless tests for <see cref="PackageCachesViewModel"/> (the WinUI VM is linked in and exercised
/// with no UI). They pin the F2 fix: the warning sum, <c>HasCachesOnSystemDrive</c>, and the "Move all"
/// count all filter on the LIVE per-row <see cref="PackageCacheRowViewModel.CanMove"/> — which a
/// successful move flips to false — rather than the immutable <c>Info.OnDevDrive</c> detection snapshot
/// (which never flips). The move itself runs through the REAL <see cref="PackageCacheMoveCoordinator"/>
/// over an in-memory filesystem / environment / store, so NO real cache, variable, or disk is touched.
/// </summary>
[TestClass]
public sealed class PackageCachesViewModelTests
{
    private const string NpmSource = @"C:\Users\dev\AppData\Local\npm-cache";
    private const string NuGetSource = @"C:\Users\dev\.nuget\packages";
    private const string NpmEnvVar = "npm_config_cache";
    private const string NuGetEnvVar = "NUGET_PACKAGES";

    [TestMethod]
    public async Task PerRowMove_DropsMovedCacheFromLiveCount_NotFromImmutableSnapshot()
    {
        (PackageCachesViewModel vm, FakeEnvironmentWriter env) = BuildViewModel();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 2 && vm.Caches.All(c => c.CanMove && c.SizeBytes > 0));

        Assert.IsTrue(vm.HasCachesOnSystemDrive, "Both detected caches start on the system drive.");

        // Move exactly one cache through the real (in-memory) engine.
        PackageCacheRowViewModel first = vm.Caches[0];
        first.ConfirmMoveCommand.Execute(null);
        await WaitUntilAsync(() => first.IsSet && !first.IsMoving && first.ShowMoveResult);

        // The detection snapshot is immutable and stays false — the OLD `!Info.OnDevDrive` predicate
        // would therefore STILL count this row. The fix counts the LIVE CanMove, which the move flipped.
        Assert.IsFalse(first.Info.OnDevDrive, "Detection snapshot must stay false (it is never mutated).");
        Assert.IsFalse(first.CanMove, "A successful move flips the live CanMove to false.");
        Assert.IsTrue(vm.Caches[1].CanMove, "The un-moved cache is still eligible.");

        // One eligible cache remains, so the warning still shows — and the live "Move all" count is 1,
        // not 2 (the moved row is excluded). Under the stale snapshot predicate this would still say 2.
        Assert.IsTrue(vm.HasCachesOnSystemDrive);
        vm.MoveAllCommand.Execute(null);
        StringAssert.Contains(vm.MoveAllConfirmBodyText, "1 package cache");
        vm.CancelMoveAllCommand.Execute(null);

        Assert.AreEqual(@"G:\packages\npm", env.GetUserVariable(NpmEnvVar), "Only the moved cache's variable is set.");
        Assert.IsNull(env.GetUserVariable(NuGetEnvVar), "The un-moved cache's variable is untouched.");
    }

    [TestMethod]
    public async Task ConfirmMoveAll_MovesEveryLiveEligibleCache_ThenClearsTheWarning()
    {
        (PackageCachesViewModel vm, FakeEnvironmentWriter env) = BuildViewModel();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 2 && vm.Caches.All(c => c.CanMove && c.SizeBytes > 0));

        // The confirm body counts the LIVE eligible rows (2 to start).
        vm.MoveAllCommand.Execute(null);
        Assert.IsTrue(vm.IsConfirmingMoveAll);
        StringAssert.Contains(vm.MoveAllConfirmBodyText, "2 package cache");

        await vm.ConfirmMoveAllCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.ShowMoveAllResult);
        Assert.IsTrue(vm.Caches.All(c => c.IsSet && !c.CanMove), "Every cache moved to the Dev Drive.");
        // Neither immutable snapshot ever flips, so the stale predicate would leave this TRUE; the live
        // CanMove predicate correctly reports nothing left on the system drive.
        Assert.IsFalse(vm.Caches.Any(c => c.Info.OnDevDrive), "Detection snapshots are immutable.");
        Assert.IsFalse(vm.HasCachesOnSystemDrive, "No eligible caches remain — the warning is cleared.");
        Assert.AreEqual(@"G:\packages\npm", env.GetUserVariable(NpmEnvVar));
        Assert.AreEqual(@"G:\packages\nuget", env.GetUserVariable(NuGetEnvVar));
    }

    [TestMethod]
    public async Task NoDevDrive_KeepsDetectedCachesVisibleAndDisablesMoveActions()
    {
        (PackageCachesViewModel vm, _) = BuildViewModel();

        vm.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 2 && vm.Caches.All(c => c.SizeBytes > 0));

        Assert.IsFalse(vm.HasDevDrive);
        Assert.IsTrue(vm.HasDetectedCaches);
        Assert.IsTrue(vm.ShowNoDevDriveNotice);
        Assert.IsTrue(vm.ShowDashboardCacheList);
        Assert.IsFalse(vm.ShowAllCachesOnDevDrive);
        Assert.IsFalse(vm.ShowMoveAllButton);
        Assert.IsTrue(vm.Caches.All(c => !c.CanMove && !c.ShowMoveButton && !c.ShowMoveBackButton));
        Assert.AreEqual("2 package caches detected on this PC.", vm.WarningMessage);
        Assert.AreEqual(NpmSource, vm.Caches.Single(c => c.Header == "npm").ResolvedPath);
    }

    [TestMethod]
    public async Task LosingDevDrive_ImmediatelyInvalidatesExistingMoveCommands()
    {
        (PackageCachesViewModel vm, _) = BuildViewModel();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 2 && vm.Caches.All(c => c.CanMove));
        PackageCacheRowViewModel oldRow = vm.Caches[0];
        int resetCount = 0;
        vm.InventoryReset += (_, _) => resetCount++;

        vm.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');

        Assert.IsFalse(oldRow.CanMove);
        Assert.IsFalse(oldRow.MoveCommand.CanExecute(null));
        Assert.IsFalse(oldRow.ConfirmMoveCommand.CanExecute(null));
        Assert.AreEqual(1, resetCount);
    }

    [TestMethod]
    public async Task DriveTransition_CancelsStaleMoveAllConfirmation()
    {
        (PackageCachesViewModel vm, _) = BuildViewModel();
        vm.Initialize(@"C:\", @"G:\", 'G', 'C');
        await WaitUntilAsync(() => vm.Caches.Count == 2 && vm.Caches.All(c => c.CanMove));

        vm.MoveAllCommand.Execute(null);
        Assert.IsTrue(vm.IsConfirmingMoveAll);
        Assert.IsTrue(vm.ConfirmMoveAllCommand.CanExecute(null));

        vm.SetDevDriveUnavailable("Checking drive status.");

        Assert.IsFalse(vm.IsConfirmingMoveAll);
        Assert.IsFalse(vm.ConfirmMoveAllCommand.CanExecute(null));
        Assert.AreEqual(string.Empty, vm.MoveAllConfirmBodyText);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static (PackageCachesViewModel Vm, FakeEnvironmentWriter Env) BuildViewModel()
    {
        var fs = new InMemoryFileSystem();
        // Seed both sources so the real in-memory mover can copy + hash-verify them.
        fs.AddFile(NpmSource + @"\a.txt", "alpha");
        fs.AddFile(NpmSource + @"\b.txt", "bravo");
        fs.AddFile(NuGetSource + @"\p.txt", "papa");
        fs.AddFile(NuGetSource + @"\sub\q.txt", "quebec");

        var env = new FakeEnvironmentWriter();
        var store = new InMemoryReversibilityStore();
        var coordinator = new PackageCacheMoveCoordinator(new PackageCacheMover(fs, env, store), store);

        var caches = new[]
        {
            new PackageCacheInfo
            {
                Name = "npm",
                EnvironmentVariable = NpmEnvVar,
                ResolvedPath = NpmSource,
                Detected = true,
                DriveLetter = 'C',
                OnDevDrive = false,
            },
            new PackageCacheInfo
            {
                Name = "nuget",
                EnvironmentVariable = NuGetEnvVar,
                ResolvedPath = NuGetSource,
                Detected = true,
                DriveLetter = 'C',
                OnDevDrive = false,
            },
        };

        var service = new FakePackageCacheService(caches, sizePerCache: 24UL * 1024 * 1024);
        return (new PackageCachesViewModel(service, coordinator), env);
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

    /// <summary>
    /// Minimal in-memory <see cref="IPackageCacheService"/>: returns a fixed set of detected, on-system
    /// caches, a deterministic size, and a Dev-Drive-targeted move plan. Detection/sizing are pure data;
    /// the actual (in-memory) move is performed by the real coordinator the VM is given.
    /// </summary>
    private sealed class FakePackageCacheService : IPackageCacheService
    {
        private readonly IReadOnlyList<PackageCacheInfo> _caches;
        private readonly ulong _sizePerCache;

        public FakePackageCacheService(IReadOnlyList<PackageCacheInfo> caches, ulong sizePerCache)
        {
            _caches = caches;
            _sizePerCache = sizePerCache;
        }

        public IReadOnlyList<PackageCacheInfo> GetPackageCaches(char? devDriveLetter) => _caches;

        public Task<ulong> CalculateSizeAsync(PackageCacheInfo cache, TimeSpan timeBudget, CancellationToken cancellationToken = default) =>
            Task.FromResult(cache.Detected ? _sizePerCache : 0UL);

        public PackageCacheMovePlan BuildMovePlan(PackageCacheInfo cache, char devDriveLetter) => new()
        {
            ToolName = cache.Name,
            SourcePath = cache.ResolvedPath,
            TargetPath = $@"{char.ToUpperInvariant(devDriveLetter)}:\packages\{cache.Name.ToLowerInvariant()}",
            EnvironmentVariable = cache.EnvironmentVariable,
        };
    }
}
