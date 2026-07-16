using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Which action the inline confirm will perform when accepted. Defaults to <see cref="Move"/> so the
/// existing confirm path (and tests that execute <c>ConfirmMoveCommand</c> directly) keep moving.
/// </summary>
public enum PendingCacheAction
{
    /// <summary>Copy a detected C: cache to the Dev Drive and repoint the variable (the original flow).</summary>
    Move,

    /// <summary>Point the variable at a user-chosen folder without copying (for an undetected tool).</summary>
    Map,

    /// <summary>Move a user-chosen folder to the Dev Drive AND repoint the variable (for an undetected tool).</summary>
    Remap,
}

/// <summary>
/// Row VM for one developer tool's package cache. Detection data is immutable; only the size loads
/// asynchronously (so the row shows "calculating…" then the real size). The Move command is GATED —
/// it opens an inline, preview-only confirm (owned by this row) and changes nothing.
/// </summary>
/// <remarks>
/// Speed <em>testing</em> ("what does moving this cache get me?") now lives in the unified Performance
/// test suite, not on the row, so this VM only owns detection, sizing, status and the safe move flow.
/// </remarks>
public partial class PackageCacheRowViewModel : ObservableObject
{
    private readonly Action<PackageCacheRowViewModel> _onMove;
    private readonly Action<PackageCacheRowViewModel> _onConfirmMove;
    private readonly Action<PackageCacheRowViewModel> _onMoveBack;
    private readonly Action<PackageCacheRowViewModel> _onCancelActiveMove;
    private readonly Action<PackageCacheRowViewModel> _onMapRequested;
    private readonly Action<PackageCacheRowViewModel> _onRemapRequested;
    private readonly Func<string, bool> _folderExists;
    private readonly char _devLetter;
    private readonly char _systemWhere;

    public PackageCacheRowViewModel(
        PackageCacheInfo info,
        char devLetter,
        char systemLetter,
        Action<PackageCacheRowViewModel> onMove,
        Action<PackageCacheRowViewModel> onConfirmMove,
        Action<PackageCacheRowViewModel> onMoveBack,
        Action<PackageCacheRowViewModel> onCancelActiveMove,
        Action<PackageCacheRowViewModel> onMapRequested,
        Action<PackageCacheRowViewModel> onRemapRequested,
        Func<string, bool>? folderExists = null)
    {
        Info = info;
        _onMove = onMove;
        _onConfirmMove = onConfirmMove;
        _onMoveBack = onMoveBack;
        _onCancelActiveMove = onCancelActiveMove;
        _onMapRequested = onMapRequested;
        _onRemapRequested = onRemapRequested;
        _folderExists = folderExists ?? Directory.Exists;
        _devLetter = devLetter;
        _systemWhere = info.DriveLetter ?? systemLetter;

        Header = info.Name;
        Description = $"{info.PathTemplate} \u2192 sets {info.EnvironmentVariable}";
        ResolvedPath = info.ResolvedPath;
        MapPath = info.ResolvedPath;

        if (!info.Detected)
        {
            StatusKind = "notfound";
            StatusText = "Not found";
            SizeText = string.Empty;
        }
        else if (info.OnDevDrive)
        {
            StatusKind = "dev";
            StatusText = $"On {devLetter}:";
            SizeText = "calculating\u2026";
        }
        else
        {
            StatusKind = "system";
            char where = info.DriveLetter ?? systemLetter;
            StatusText = $"On {where}:";
            SizeText = "calculating\u2026";
        }

        CanMove = info.Detected && !info.OnDevDrive;
        IsSet = info.Detected && info.OnDevDrive;
        IsNotFound = !info.Detected;

        string token = FolderToken(info.Name);
        RowAutomationId = $"PackageCacheCard_{token}";
        MoveButtonAutomationId = $"MoveCache_{token}";
        MoveConfirmPanelAutomationId = $"MoveConfirm_{token}";
        ConfirmMoveButtonAutomationId = $"ConfirmMove_{token}";
        CancelMoveButtonAutomationId = $"CancelMove_{token}";
        CancelActiveMoveButtonAutomationId = $"CancelActiveMove_{token}";
        MoveResultAutomationId = $"MoveResult_{token}";
        MoveBackButtonAutomationId = $"MoveBack_{token}";
        MoveProgressAutomationId = $"MoveProgress_{token}";
        MapPathInputAutomationId = $"MapPathInput_{token}";
        BrowseMapPathButtonAutomationId = $"BrowseMapPath_{token}";
        MapPathButtonAutomationId = $"MapPath_{token}";
        MoveAndRemapButtonAutomationId = $"MoveAndRemap_{token}";
        MapBlockAutomationId = $"MapBlock_{token}";
        AutomationName = $"{Header}, {StatusText}";
    }

    /// <summary>The underlying detection snapshot (used to build the safe move plan).</summary>
    public PackageCacheInfo Info { get; }

    public string Header { get; }

    public string Description { get; }

    public string ResolvedPath { get; }

    /// <summary>"dev" | "system" | "notfound" — drives the status-pill colour.</summary>
    [ObservableProperty]
    public partial string StatusKind { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    /// <summary>True when the cache exists and is NOT on the Dev Drive (so it can be moved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveButton))]
    public partial bool CanMove { get; set; }

    /// <summary>True when the cache is already on the Dev Drive (show the "set" check).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapBlock))]
    public partial bool IsSet { get; set; }

    /// <summary>True when the cache directory does not exist.</summary>
    public bool IsNotFound { get; }

    /// <summary>Dim "Not found" rows so the actionable rows stand out.</summary>
    public double RowOpacity => IsNotFound ? 0.5 : 1.0;

    /// <summary>
    /// True for alternate ("banded") rows within a status group so the eye has a stripe to follow. Set by
    /// the Package caches page each time the rows are (re)grouped — moving a cache changes its group and
    /// hence the banding, so this is observable.
    /// </summary>
    [ObservableProperty]
    public partial bool BandAlt { get; set; }

    public string RowAutomationId { get; }

    public string MoveButtonAutomationId { get; }

    public string MoveConfirmPanelAutomationId { get; }

    public string ConfirmMoveButtonAutomationId { get; }

    public string CancelMoveButtonAutomationId { get; }

    public string CancelActiveMoveButtonAutomationId { get; }

    public string MoveResultAutomationId { get; }

    public string MoveBackButtonAutomationId { get; }

    public string MoveProgressAutomationId { get; }

    public string MapPathInputAutomationId { get; }

    public string BrowseMapPathButtonAutomationId { get; }

    public string MapPathButtonAutomationId { get; }

    public string MoveAndRemapButtonAutomationId { get; }

    public string MapBlockAutomationId { get; }

    [ObservableProperty]
    public partial string AutomationName { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; }

    [ObservableProperty]
    public partial ulong SizeBytes { get; set; }

    /// <summary>Called by the parent once the async size probe completes.</summary>
    public void SetSize(ulong bytes)
    {
        SizeBytes = bytes;
        SizeText = bytes == 0UL ? "\u2014" : ByteSizeFormatter.Format(bytes);
    }

    // ---- Inline "Move" confirm + REAL reversible move (M4) ---------------------------------------

    /// <summary>True while this row's inline move-confirm panel is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveButton))]
    [NotifyPropertyChangedFor(nameof(ShowMoveBackButton))]
    [NotifyPropertyChangedFor(nameof(ShowMapBlock))]
    public partial bool IsConfirmingMove { get; set; }

    /// <summary>True while the REAL move (or move-back) is running — drives the live progress UI.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveButton))]
    [NotifyPropertyChangedFor(nameof(ShowMoveBackButton))]
    [NotifyPropertyChangedFor(nameof(ShowMapBlock))]
    public partial bool IsMoving { get; set; }

    /// <summary>True once a move/move-back result is available to show inline.</summary>
    [ObservableProperty]
    public partial bool ShowMoveResult { get; set; }

    /// <summary>The inline-confirm body copy (built by the parent from the SAFE move plan).</summary>
    [ObservableProperty]
    public partial string MoveConfirmBodyText { get; set; } = string.Empty;

    /// <summary>The real result line shown after a move / move-back (e.g. "Moved 24 MB to G:\packages\npm; …").</summary>
    [ObservableProperty]
    public partial string MoveResultText { get; set; } = string.Empty;

    /// <summary>Live "Copying 3/8 files · 9 MB / 24 MB" status while the move runs.</summary>
    [ObservableProperty]
    public partial string MoveProgressText { get; set; } = string.Empty;

    /// <summary>Determinate move progress in [0, 100].</summary>
    [ObservableProperty]
    public partial double MoveProgressPercent { get; set; }

    /// <summary>True when this row offers "Move back" (a reversible move was recorded for this cache).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoveBackButton))]
    public partial bool CanMoveBack { get; set; }

    /// <summary>Show the "Move" button when this cache can move and no confirm/move is in flight.</summary>
    public bool ShowMoveButton => CanMove && !IsConfirmingMove && !IsMoving;

    /// <summary>Show "Move back" when a reversible move exists and nothing is in flight.</summary>
    public bool ShowMoveBackButton => CanMoveBack && !IsConfirmingMove && !IsMoving;

    // ---- "Map path" / "Move & remap" for an UNDETECTED tool --------------------------------------

    /// <summary>Which action the inline confirm will run when accepted. Defaults to <see cref="PendingCacheAction.Move"/>.</summary>
    public PendingCacheAction PendingAction { get; set; } = PendingCacheAction.Move;

    /// <summary>User-editable folder this tool's cache lives at (for Map / Move &amp; remap). Two-way bound.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveAndRemap))]
    [NotifyCanExecuteChangedFor(nameof(MoveAndRemapCommand))]
    public partial string MapPath { get; set; } = string.Empty;

    /// <summary>True once the variable has been pointed at a user-chosen folder (no copy) for this tool.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapBlock))]
    public partial bool IsMapped { get; set; }

    /// <summary>
    /// Show the "tell us where it lives" block (Browse / Map path / Move &amp; remap) for an undetected
    /// tool that is not yet mapped or moved and has nothing in flight — instead of a dead "Not found".
    /// </summary>
    public bool ShowMapBlock => IsNotFound && !IsSet && !IsMapped && !IsConfirmingMove && !IsMoving;

    /// <summary>
    /// "Move &amp; remap" is only valid when the chosen folder actually EXISTS — you cannot move a cache
    /// that isn't there. (Map path only repoints the variable, so it stays valid for a not-yet-created
    /// folder.) Re-evaluated live as <see cref="MapPath"/> changes (typed or browsed).
    /// </summary>
    public bool CanMoveAndRemap => !string.IsNullOrWhiteSpace(MapPath) && _folderExists(MapPath);

    /// <summary>Parent call: open the inline confirm with the supplied (real, reversible) body copy.</summary>
    public void BeginMoveConfirm(string body)
    {
        MoveConfirmBodyText = body;
        ShowMoveResult = false;
        IsConfirmingMove = true;
    }

    /// <summary>Parent call: leave the confirm and enter the live "moving" state (clears any prior result).</summary>
    public void BeginMove()
    {
        IsConfirmingMove = false;
        IsMoving = true;
        ShowMoveResult = false;
        MoveResultText = string.Empty;
        MoveProgressText = "Preparing\u2026";
        MoveProgressPercent = 0d;
    }

    /// <summary>Parent call (on the UI thread): update the live move progress from a report.</summary>
    public void ReportMoveProgress(CacheMoveProgress progress)
    {
        MoveProgressText = CacheMoveProgressFormatter.Describe(progress);
        MoveProgressPercent = CacheMoveProgressFormatter.Percent(progress);
    }

    /// <summary>Parent call: render a finished move outcome and flip the row's state accordingly.</summary>
    public void ApplyMoveOutcome(CacheMoveOutcome outcome)
    {
        IsMoving = false;
        MoveResultText = outcome.ResultText;
        ShowMoveResult = true;

        if (outcome.IsOnDevDrive)
        {
            // Success: the row is now on the Dev Drive and offers a reversible "Move back".
            StatusKind = "dev";
            StatusText = $"On {_devLetter}:";
            CanMove = false;
            IsSet = true;
            CanMoveBack = outcome.CanMoveBack;
            AutomationName = $"{Header}, {StatusText}";
        }
        // On Failed/Cancelled the engine already rolled back: the row stays "On C:" and can retry.
    }

    /// <summary>Parent call: render a finished move-back outcome and return the row to its "On C:" state.</summary>
    public void ApplyMoveBackOutcome(CacheMoveOutcome outcome)
    {
        IsMoving = false;
        MoveResultText = outcome.ResultText;
        ShowMoveResult = true;

        if (outcome.Status == CacheMoveStatus.MovedBack)
        {
            StatusKind = "system";
            StatusText = $"On {_systemWhere}:";
            CanMove = true;
            IsSet = false;
            CanMoveBack = false;
            AutomationName = $"{Header}, {StatusText}";
        }
    }

    /// <summary>Parent call: render a finished "Map path" outcome — the variable now points at the user's folder.</summary>
    public void ApplyMapOutcome(CacheMoveOutcome outcome)
    {
        IsMoving = false;
        MoveResultText = outcome.ResultText;
        ShowMoveResult = true;

        if (outcome.Status == CacheMoveStatus.Mapped)
        {
            IsMapped = true;
            // The chosen folder may live on C: or the Dev Drive — colour the pill honestly by where it is,
            // never grey for an active/tracked state.
            bool onDev = outcome.TargetPath.Length > 0
                && char.ToUpperInvariant(outcome.TargetPath[0]) == char.ToUpperInvariant(_devLetter);
            StatusKind = onDev ? "dev" : "system";
            StatusText = onDev ? $"On {_devLetter}:" : "Mapped";
            CanMoveBack = outcome.CanMoveBack;
            AutomationName = $"{Header}, {StatusText}";
        }
    }

    [RelayCommand]
    private void Move() => _onMove(this);

    /// <summary>Confirm now performs the REAL reversible move (owned/serialised by the parent).</summary>
    [RelayCommand]
    private void ConfirmMove() => _onConfirmMove(this);

    /// <summary>Cancel closes the prompt and changes nothing.</summary>
    [RelayCommand]
    private void CancelMove() => IsConfirmingMove = false;

    /// <summary>Cancels the in-flight move/move-back (owned/serialised by the parent, which holds the CTS).</summary>
    [RelayCommand]
    private void CancelActiveMove() => _onCancelActiveMove(this);

    /// <summary>Reverses a prior move: restores the env var + removes the Dev Drive copy (owned by the parent).</summary>
    [RelayCommand]
    private void MoveBack() => _onMoveBack(this);

    /// <summary>Map path: point the variable at <see cref="MapPath"/> without copying (owned by the parent).</summary>
    [RelayCommand]
    private void MapPathAction() => _onMapRequested(this);

    /// <summary>Move &amp; remap: move <see cref="MapPath"/> to the Dev Drive AND repoint the variable (owned by the parent).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveAndRemap))]
    private void MoveAndRemap() => _onRemapRequested(this);

    private static string FolderToken(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "cache" : builder.ToString();
    }
}
