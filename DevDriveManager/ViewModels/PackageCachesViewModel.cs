using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Drives the "Package caches" section. Detection and sizing are REAL (resolved from environment
/// variables + a bounded directory scan). Moving is GATED: both "Move" (per row) and "Move all" open
/// a SAFE inline confirm and change nothing.
/// </summary>
/// <remarks>
/// Moving is GATED behind a SAFE inline confirm that changes nothing. Detection now runs off the UI
/// thread (npm cache-path resolution can spawn a short-lived process). Speed <em>testing</em> lives in
/// the unified Performance test suite, not here.
/// </remarks>
public partial class PackageCachesViewModel : ObservableObject
{
    private static readonly TimeSpan SizeBudget = TimeSpan.FromSeconds(1.5);

    private readonly IPackageCacheService _service;
    private readonly PackageCacheMoveCoordinator _moveCoordinator;
    private readonly Func<string, bool>? _folderExists;
    private char _devLetter = 'G';
    private char _systemLetter = 'C';

    // Moves are serialised (one at a time, cancellable).
    private PackageCacheRowViewModel? _movingRow;
    private CancellationTokenSource? _moveCts;

    // Bumped on each (re)load; the async size / move-back passes capture it and bail if a newer load
    // supersedes them, so a re-Initialize() can't fault a mid-flight pass (collection modified) or write
    // stale data onto freshly rebuilt rows.
    private int _loadGeneration;
    private int _moveAllConfirmationGeneration = -1;

    public PackageCachesViewModel(IPackageCacheService service, PackageCacheMoveCoordinator moveCoordinator, Func<string, bool>? folderExists = null)
    {
        _service = service;
        _moveCoordinator = moveCoordinator;
        _folderExists = folderExists;
    }

    /// <summary>Convenience factory wiring the real package-cache and move-coordinator services.</summary>
    public static PackageCachesViewModel CreateDefault() =>
        new(
            PackageCacheService.CreateDefault(),
            MutationComposition.CreatePackageCacheMoveCoordinator());

    /// <summary>One row per catalogued tool.</summary>
    public ObservableCollection<PackageCacheRowViewModel> Caches { get; } = new();

    /// <summary>Raised after <see cref="Caches"/> is (re)populated, so an aggregator can regroup the rows.</summary>
    public event EventHandler? CachesChanged;

    /// <summary>Raised synchronously when old rows are cleared so page projections cannot retain stale actions.</summary>
    public event EventHandler? InventoryReset;

    [ObservableProperty]
    public partial bool IsCalculating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveAllButton))]
    [NotifyPropertyChangedFor(nameof(ShowAllCachesOnDevDrive))]
    [NotifyPropertyChangedFor(nameof(ShowDashboardCacheList))]
    [NotifyCanExecuteChangedFor(nameof(MoveAllCommand))]
    public partial bool HasCachesOnSystemDrive { get; set; }

    /// <summary>True when package-cache move targets are available on this PC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveAllButton))]
    [NotifyPropertyChangedFor(nameof(ShowNoDevDriveNotice))]
    [NotifyPropertyChangedFor(nameof(ShowAllCachesOnDevDrive))]
    [NotifyPropertyChangedFor(nameof(ShowDashboardCacheList))]
    [NotifyCanExecuteChangedFor(nameof(MoveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmMoveAllCommand))]
    public partial bool HasDevDrive { get; set; }

    /// <summary>True when at least one catalogued cache exists on this PC, independent of move capability.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllCachesOnDevDrive))]
    [NotifyPropertyChangedFor(nameof(ShowDashboardCacheList))]
    public partial bool HasDetectedCaches { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDashboardCacheList))]
    public partial bool HasCachesOutsideDevDrive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllCachesOnDevDrive))]
    public partial bool AllDetectedCachesOnDevDrive { get; set; }

    [ObservableProperty]
    public partial string WarningMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevDriveNoticeMessage { get; set; } =
        "No Dev Drive found. Cache locations and sizes are still listed below; create a Dev Drive to enable move actions.";

    [ObservableProperty]
    public partial string MoveAllButtonText { get; set; } = "Move all";

    // ---- Inline "Move all" confirm (replaces the old preview ContentDialog) -----------------------

    /// <summary>True while the inline "Move all" confirm panel is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveAllButton))]
    [NotifyCanExecuteChangedFor(nameof(MoveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmMoveAllCommand))]
    public partial bool IsConfirmingMoveAll { get; set; }

    /// <summary>True once the user confirmed — shows the safe, preview-only "nothing changed" result.</summary>
    [ObservableProperty]
    public partial bool ShowMoveAllResult { get; set; }

    /// <summary>The inline-confirm body copy (built from the SAFE move plans).</summary>
    [ObservableProperty]
    public partial string MoveAllConfirmBodyText { get; set; } = string.Empty;

    /// <summary>The real combined "Move all" result line (continue-on-error aggregate).</summary>
    [ObservableProperty]
    public partial string MoveAllResultText { get; set; } = string.Empty;

    /// <summary>Show the "Move all" button when there is something to move and its confirm panel is closed.</summary>
    public bool ShowMoveAllButton => HasDevDrive && HasCachesOnSystemDrive && !IsConfirmingMoveAll;

    /// <summary>Shows neutral guidance instead of a false Dev Drive all-clear state.</summary>
    public bool ShowNoDevDriveNotice => !HasDevDrive;

    /// <summary>Shows the success state only when a Dev Drive and at least one detected cache both exist.</summary>
    public bool ShowAllCachesOnDevDrive =>
        HasDevDrive && HasDetectedCaches && AllDetectedCachesOnDevDrive;

    /// <summary>The Dashboard previews movable caches, or all detected caches when no Dev Drive exists.</summary>
    public bool ShowDashboardCacheList => HasDevDrive ? HasCachesOutsideDevDrive : HasDetectedCaches;

    /// <summary>Detects caches even when no Dev Drive exists, then sizes them asynchronously.</summary>
    public void Initialize(
        string systemRoot,
        string? devRoot,
        char? devLetter,
        char systemLetter,
        string? unavailableNoticeMessage = null)
    {
        ResetForDriveTransition();
        DisableCurrentRowActions();
        HasDevDrive = devLetter.HasValue;
        if (devLetter is char letter)
        {
            _devLetter = char.ToUpperInvariant(letter);
        }

        _systemLetter = char.ToUpperInvariant(systemLetter);
        DevDriveNoticeMessage = HasDevDrive
            ? string.Empty
            : unavailableNoticeMessage ??
              "No Dev Drive found. Cache locations and sizes are still listed below; create a Dev Drive to enable move actions.";

        Caches.Clear();
        InventoryReset?.Invoke(this, EventArgs.Empty);
        HasDetectedCaches = false;
        HasCachesOnSystemDrive = false;
        HasCachesOutsideDevDrive = false;
        AllDetectedCachesOnDevDrive = false;
        WarningMessage = "Scanning package caches\u2026";
        MoveAllButtonText = "Move all";
        _ = LoadCachesAsync(devLetter);
    }

    /// <summary>Preserves the read-only inventory while immediately disabling stale drive-dependent actions.</summary>
    public void SetDevDriveUnavailable(string noticeMessage)
    {
        ResetForDriveTransition();
        DisableCurrentRowActions();
        HasDevDrive = false;
        DevDriveNoticeMessage = noticeMessage;
        UpdateWarning();
        CachesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves the caches off the UI thread (npm cache-path detection can spawn a short-lived process)
    /// and then populates the rows, sizing and move-back state. Read-only: detection never moves anything.
    /// </summary>
    private async Task LoadCachesAsync(char? devDriveLetter)
    {
        int generation = ++_loadGeneration;
        char displayDevLetter = devDriveLetter is char letter ? char.ToUpperInvariant(letter) : _devLetter;
        char systemLetter = _systemLetter;
        bool hasDevDrive = devDriveLetter.HasValue;
        IReadOnlyList<PackageCacheInfo> infos =
            await Task.Run(() => _service.GetPackageCaches(devDriveLetter));

        if (generation != _loadGeneration)
        {
            return; // a newer Initialize() superseded this load while detection ran off-thread.
        }

        Caches.Clear();
        foreach (PackageCacheInfo info in infos)
        {
            Caches.Add(new PackageCacheRowViewModel(
                info, displayDevLetter, systemLetter,
                OnMoveRequested, OnConfirmMoveRequested, OnMoveBackRequested, OnCancelActiveMoveRequested,
                OnMapRequested, OnRemapRequested, _folderExists, hasDevDrive));
        }

        UpdateWarning();
        CachesChanged?.Invoke(this, EventArgs.Empty);
        _ = LoadSizesAsync(generation);
        _ = LoadMoveBackStateAsync(generation);
    }

    /// <summary>
    /// Off the UI thread, asks the coordinator (which reads the persistent reversibility store) which
    /// caches already have a recorded move, and lights up "Move back" on those rows. This is what makes
    /// a cache moved in a PRIOR launch keep its reversible "Move back" affordance after a restart. The
    /// availability is driven SOLELY by the persistent store (CanMoveBack), independent of the live
    /// OnDevDrive classification — which can differ across restarts and would otherwise hide a valid,
    /// recorded revert. A cache on the Dev Drive with NO recorded entry (the user set the env var by
    /// hand) is honestly left without a Move-back affordance.
    /// </summary>
    private async Task LoadMoveBackStateAsync(int generation)
    {
        // M3: evaluate every row that has an environment variable, NOT just rows currently classified as
        // OnDevDrive — the persistent reversibility store is the authoritative source for "Move back".
        List<PackageCacheRowViewModel> rows = Caches
            .Where(r => !string.IsNullOrEmpty(r.Info.EnvironmentVariable))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        Dictionary<string, bool> canMoveBack = await Task.Run(() =>
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (PackageCacheRowViewModel row in rows)
            {
                string envVar = row.Info.EnvironmentVariable;
                if (!map.ContainsKey(envVar))
                {
                    map[envVar] = _moveCoordinator.CanMoveBack(envVar);
                }
            }

            return map;
        });

        if (generation != _loadGeneration)
        {
            return; // superseded by a newer load — don't apply stale move-back state to rebuilt rows.
        }

        foreach (PackageCacheRowViewModel row in rows)
        {
            row.CanMoveBack = canMoveBack.TryGetValue(row.Info.EnvironmentVariable, out bool can) && can;
        }
    }

    private async Task LoadSizesAsync(int generation)
    {
        IsCalculating = true;
        try
        {
            // Iterate a SNAPSHOT so a concurrent re-Initialize (Caches.Clear) can't fault the enumeration;
            // bail on a newer generation so we don't write stale sizes onto freshly rebuilt rows.
            foreach (PackageCacheRowViewModel row in Caches.ToList())
            {
                if (generation != _loadGeneration)
                {
                    return;
                }

                if (!row.Info.Detected)
                {
                    continue;
                }

                ulong size = await _service.CalculateSizeAsync(row.Info, SizeBudget);
                if (generation != _loadGeneration)
                {
                    return;
                }

                row.SetSize(size);
                UpdateWarning();
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsCalculating = false;
            }
        }
    }

    private void UpdateWarning()
    {
        List<PackageCacheRowViewModel> detected = Caches
            .Where(c => c.Info.Detected || c.IsMapped || c.IsSet)
            .ToList();
        int detectedCount = detected.Count;
        HasDetectedCaches = detectedCount > 0;
        List<PackageCacheRowViewModel> outsideDev = detected
            .Where(c => !c.IsOnDevDrive)
            .ToList();
        HasCachesOutsideDevDrive = HasDevDrive && outsideDev.Count > 0;
        AllDetectedCachesOnDevDrive = HasDevDrive && detectedCount > 0 && outsideDev.Count == 0;

        if (!HasDevDrive)
        {
            HasCachesOnSystemDrive = false;
            WarningMessage = detectedCount switch
            {
                0 => "No package caches detected on this PC.",
                1 => "1 package cache detected on this PC.",
                _ => $"{detectedCount} package caches detected on this PC.",
            };
            MoveAllButtonText = "Move all";
            return;
        }

        // F2: filter on the LIVE per-row CanMove (detected && currently on the system drive). Unlike the
        // immutable Info.OnDevDrive snapshot, CanMove is flipped to false by ApplyMoveOutcome the moment a
        // cache moves, so a moved cache drops out of the warning, the sum, and the "Move all" count at once.
        List<PackageCacheRowViewModel> onSystem = Caches
            .Where(c => c.CanMove)
            .ToList();

        ulong sum = 0UL;
        foreach (PackageCacheRowViewModel row in onSystem)
        {
            sum += row.SizeBytes;
        }

        HasCachesOnSystemDrive = onSystem.Count > 0;
        string sumText = ByteSizeFormatter.Format(sum);
        WarningMessage = onSystem.Count > 0
            ? $"{sumText} of package caches are on {_systemLetter}:. Moving them to your Dev Drive " +
              $"frees space on {_systemLetter}: and speeds up installs and restores."
            : AllDetectedCachesOnDevDrive
                ? $"All {detectedCount} detected package caches are on {_devLetter}:."
                : outsideDev.Count == 1
                    ? $"1 detected package cache is outside {_devLetter}:."
                    : outsideDev.Count > 1
                        ? $"{outsideDev.Count} detected package caches are outside {_devLetter}:."
                        : "No package caches detected on this PC.";
        MoveAllButtonText = $"Move all ({sumText})";
    }

    private void ResetForDriveTransition()
    {
        _loadGeneration++;
        _moveCts?.Cancel();
        _moveAllConfirmationGeneration = -1;
        IsConfirmingMoveAll = false;
        ShowMoveAllResult = false;
        MoveAllConfirmBodyText = string.Empty;
        MoveAllResultText = string.Empty;
        IsCalculating = false;
        ConfirmMoveAllCommand.NotifyCanExecuteChanged();
    }

    private void DisableCurrentRowActions()
    {
        foreach (PackageCacheRowViewModel row in Caches)
        {
            row.DisableDevDriveActions();
        }
    }

    private void OnMoveRequested(PackageCacheRowViewModel row)
    {
        row.PendingAction = PendingCacheAction.Move;
        PackageCacheMovePlan plan = _service.BuildMovePlan(row.Info, _devLetter);
        string body =
            $"Copies {row.SizeText} to {plan.TargetPath} (hash-verified) and sets {plan.EnvironmentVariable} " +
            "for your user. The original is kept in place, so \u201CMove back\u201D fully reverses it. " +
            "Runs in the background and can be cancelled.";
        row.BeginMoveConfirm(body);
    }

    /// <summary>
    /// "Map path" request for an undetected tool: open the inline confirm describing a no-copy repoint of
    /// the per-user variable at the user-chosen <see cref="PackageCacheRowViewModel.MapPath"/>.
    /// </summary>
    private void OnMapRequested(PackageCacheRowViewModel row)
    {
        row.PendingAction = PendingCacheAction.Map;
        string body =
            $"Points {row.Info.EnvironmentVariable} at {row.MapPath} for your user \u2014 no files are copied. " +
            "New shells pick it up, and \u201CMove back\u201D restores the previous value and leaves your folder in place.";
        row.BeginMoveConfirm(body);
    }

    /// <summary>
    /// "Move &amp; remap" request for an undetected tool: open the inline confirm describing a real move of
    /// the user-chosen folder to the Dev Drive AND the repoint of the per-user variable (reuses the move engine).
    /// </summary>
    private void OnRemapRequested(PackageCacheRowViewModel row)
    {
        row.PendingAction = PendingCacheAction.Remap;
        PackageCacheMovePlan plan = _service.BuildMovePlan(row.Info, _devLetter);
        string body =
            $"Moves {row.MapPath} to {plan.TargetPath} (hash-verified) and sets {plan.EnvironmentVariable} for your user. " +
            "The original is kept in place, so \u201CMove back\u201D fully reverses it. Runs in the background and can be cancelled.";
        row.BeginMoveConfirm(body);
    }

    /// <summary>
    /// Performs the REAL, reversible move for one row: live per-row progress off the UI thread, default
    /// (source-kept) options, serialised so only one move runs at a time. On success the row flips to
    /// "On Dev Drive" + "Move back"; on a mid-copy failure the engine has already rolled back the partial
    /// copy and left the variable untouched, so the row stays "On C:" with the error shown inline.
    /// </summary>
    private async void OnConfirmMoveRequested(PackageCacheRowViewModel row)
    {
        // F1: this is an async-void event handler, so an unhandled throw — even before the first await —
        // would tear down the app on the UI thread. Defensively wrap the FULL body.
        try
        {
            if (_movingRow is not null)
            {
                return; // a move is already in flight (serialised)
            }

            _movingRow = row;
            row.BeginMove();

            // Created on the UI thread, so each progress report marshals back here and updates the row live.
            var progress = new Progress<CacheMoveProgress>(row.ReportMoveProgress);
            _moveCts = new CancellationTokenSource();
            try
            {
                switch (row.PendingAction)
                {
                    case PendingCacheAction.Map:
                    {
                        // No copy: just repoint the per-user variable at the user's folder (reversibly).
                        CacheMoveOutcome outcome = await _moveCoordinator.MapPathAsync(
                            row.Info.EnvironmentVariable, row.Header, row.MapPath, _moveCts.Token);
                        row.ApplyMapOutcome(outcome);
                        break;
                    }

                    case PendingCacheAction.Remap:
                    {
                        // Real move of the user's chosen folder to the Dev Drive + repoint (reuses the move engine).
                        PackageCacheMovePlan plan =
                            _service.BuildMovePlan(row.Info, _devLetter) with { SourcePath = row.MapPath };
                        CacheMoveOutcome outcome = await _moveCoordinator.MoveAsync(plan, progress, _moveCts.Token);
                        row.ApplyMoveOutcome(outcome);
                        break;
                    }

                    default:
                    {
                        PackageCacheMovePlan plan = _service.BuildMovePlan(row.Info, _devLetter);
                        CacheMoveOutcome outcome = await _moveCoordinator.MoveAsync(plan, progress, _moveCts.Token);
                        row.ApplyMoveOutcome(outcome);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (row.PendingAction == PendingCacheAction.Map)
                {
                    row.ApplyMapOutcome(CacheMoveOutcome.Failed(row.Header, ex.Message));
                }
                else
                {
                    row.ApplyMoveOutcome(CacheMoveOutcome.Failed(row.Header, ex.Message));
                }
            }
            finally
            {
                _moveCts?.Dispose();
                _moveCts = null;
                _movingRow = null;
                UpdateWarning();
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard: never let the async-void handler crash the app on a pre-await throw.
            row.ApplyMoveOutcome(CacheMoveOutcome.Failed(row.Header, ex.Message));
            _moveCts?.Dispose();
            _moveCts = null;
            _movingRow = null;
            UpdateWarning();
        }
    }

    /// <summary>
    /// Reverses a prior move for one row (restore the per-user env var + remove the Dev Drive copy),
    /// off the UI thread. The source was kept in place, so this is lossless. Returns the row to "On C:".
    /// </summary>
    private async void OnMoveBackRequested(PackageCacheRowViewModel row)
    {
        // F1: defensively wrap the FULL async-void body (see OnConfirmMoveRequested).
        try
        {
            if (_movingRow is not null)
            {
                return;
            }

            _movingRow = row;
            row.BeginMove();
            _moveCts = new CancellationTokenSource();
            try
            {
                CacheMoveOutcome outcome =
                    await _moveCoordinator.MoveBackAsync(row.Info.EnvironmentVariable, row.Header, _moveCts.Token);
                row.ApplyMoveBackOutcome(outcome);
            }
            catch (Exception ex)
            {
                row.ApplyMoveBackOutcome(CacheMoveOutcome.Failed(row.Header, ex.Message));
            }
            finally
            {
                _moveCts?.Dispose();
                _moveCts = null;
                _movingRow = null;
                UpdateWarning();
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard: never let the async-void handler crash the app on a pre-await throw.
            row.ApplyMoveBackOutcome(CacheMoveOutcome.Failed(row.Header, ex.Message));
            _moveCts?.Dispose();
            _moveCts = null;
            _movingRow = null;
            UpdateWarning();
        }
    }

    /// <summary>
    /// M7: cancels the in-flight move / move-back. Cancellation flows to the engine, which rolls back
    /// any partial copy and leaves the machine untouched. No-op when nothing is moving.
    /// </summary>
    private void OnCancelActiveMoveRequested(PackageCacheRowViewModel row) => _moveCts?.Cancel();

    private bool CanStartMoveAll() =>
        HasDevDrive && HasCachesOnSystemDrive && !IsConfirmingMoveAll;

    [RelayCommand(CanExecute = nameof(CanStartMoveAll))]
    private void MoveAll()
    {
        // F2: live CanMove predicate (same as UpdateWarning / ConfirmMoveAll) so the count matches the warning.
        List<PackageCacheRowViewModel> onSystem = Caches
            .Where(c => c.CanMove)
            .ToList();
        if (onSystem.Count == 0)
        {
            _moveAllConfirmationGeneration = -1;
            return;
        }

        ulong sum = 0UL;
        foreach (PackageCacheRowViewModel row in onSystem)
        {
            sum += row.SizeBytes;
        }

        string sumText = ByteSizeFormatter.Format(sum);
        MoveAllConfirmBodyText =
            $"Copies {onSystem.Count} package cache(s) ({sumText}) to {_devLetter}:\\ (hash-verified) and sets each " +
            "tool's per-user variable. Originals are kept in place, so every move is reversible. Runs one at a " +
            "time in the background; one failure won't stop the rest.";
        ShowMoveAllResult = false;
        _moveAllConfirmationGeneration = _loadGeneration;
        IsConfirmingMoveAll = true;
    }

    /// <summary>
    /// Confirms "Move all": sequentially moves every eligible on-system cache via the SAME real per-row
    /// engine path, with continue-on-error (one failure doesn't abort the rest) and a combined result
    /// that reports which tools failed.
    /// </summary>
    private bool CanConfirmMoveAll() =>
        HasDevDrive
        && IsConfirmingMoveAll
        && _moveAllConfirmationGeneration == _loadGeneration;

    [RelayCommand(CanExecute = nameof(CanConfirmMoveAll))]
    private async Task ConfirmMoveAll()
    {
        int confirmationGeneration = _moveAllConfirmationGeneration;
        IsConfirmingMoveAll = false;
        ShowMoveAllResult = false;

        if (!HasDevDrive || confirmationGeneration != _loadGeneration || _movingRow is not null)
        {
            _moveAllConfirmationGeneration = -1;
            return;
        }

        // F2: same live CanMove predicate as UpdateWarning / MoveAll.
        List<PackageCacheRowViewModel> onSystem = Caches
            .Where(c => c.CanMove)
            .ToList();
        if (onSystem.Count == 0)
        {
            return;
        }

        // F10: ONE shared CancellationTokenSource for the WHOLE sweep so the per-row Cancel affordance
        // (which cancels _moveCts) aborts the entire batch, not just the current cache. A Cancelled
        // outcome breaks the loop and leaves the remaining caches untouched.
        _moveCts = new CancellationTokenSource();
        var outcomes = new List<CacheMoveOutcome>(onSystem.Count);
        try
        {
            foreach (PackageCacheRowViewModel row in onSystem)
            {
                if (_moveCts.IsCancellationRequested)
                {
                    break;
                }

                _movingRow = row;
                row.BeginMove();
                var progress = new Progress<CacheMoveProgress>(row.ReportMoveProgress);

                CacheMoveOutcome outcome;
                try
                {
                    PackageCacheMovePlan plan = _service.BuildMovePlan(row.Info, _devLetter);
                    outcome = await _moveCoordinator.MoveAsync(plan, progress, _moveCts.Token);
                }
                catch (Exception ex)
                {
                    outcome = CacheMoveOutcome.Failed(row.Header, ex.Message);
                }
                finally
                {
                    _movingRow = null;
                }

                row.ApplyMoveOutcome(outcome);
                outcomes.Add(outcome);

                // F10: a per-row Cancel during Move-all stops the remaining caches.
                if (outcome.Status == CacheMoveStatus.Cancelled)
                {
                    break;
                }
            }
        }
        finally
        {
            _moveCts?.Dispose();
            _moveCts = null;
            _movingRow = null;
        }

        if (confirmationGeneration != _loadGeneration)
        {
            return;
        }

        CacheMoveAllOutcome combined = CacheMoveAllOutcome.From(outcomes);
        MoveAllResultText = combined.CombinedText;
        ShowMoveAllResult = true;
        _moveAllConfirmationGeneration = -1;
        UpdateWarning();
    }

    /// <summary>Cancel closes the prompt and changes nothing.</summary>
    [RelayCommand]
    private void CancelMoveAll()
    {
        _moveAllConfirmationGeneration = -1;
        IsConfirmingMoveAll = false;
    }
}
