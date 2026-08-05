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
    private bool _hasDevDrive;

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
        Func<string, bool>? folderExists = null,
        bool hasDevDrive = true)
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
        _hasDevDrive = hasDevDrive;

        Header = info.Name;
        Description = $"{info.PathTemplate} \u2192 sets {info.EnvironmentVariable}";
        ResolvedPath = info.ResolvedPath;
        MapPath = info.ResolvedPath;
        LocationText = info.ResolvedPath;
        SetRedirectedBy(info.EnvironmentVariableSet);

        if (!info.Detected)
        {
            StatusKind = "notfound";
            StatusText = "Not found";

            // An em dash, not a blank. Under a SIZE column header an empty cell reads as data we
            // failed to load; the truth is there is no folder to measure.
            SizeText = "\u2014";
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

        CanMove = hasDevDrive && info.Detected && !info.OnDevDrive;
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

    /// <summary>
    /// Which swatch colour this ecosystem gets, assigned in inventory order by the parent so a tool
    /// keeps one colour everywhere it appears.
    /// </summary>
    public int CategoryIndex { get; set; }

    /// <summary>
    /// Where the cache lives right now.
    /// <para>
    /// Separate from <see cref="ResolvedPath"/>, which is the detection-time path and never changes.
    /// A row is updated in place when a move finishes rather than being rebuilt, so anything the table
    /// shows as a column has to be observable or it goes stale the moment the user acts.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string LocationText { get; set; }

    /// <summary>The environment variable pointing the tool at its cache, or a dash when none is set.</summary>
    [ObservableProperty]
    public partial string RedirectedByText { get; set; }

    /// <summary>False when nothing redirects this tool, so the cell can render more quietly.</summary>
    [ObservableProperty]
    public partial bool IsRedirected { get; set; }

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
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    public partial bool CanMove { get; set; }

    /// <summary>True when the cache is already on the Dev Drive (show the "set" check).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapBlock))]
    [NotifyPropertyChangedFor(nameof(IsOnDevDrive))]
    public partial bool IsSet { get; set; }

    /// <summary>True when the cache directory does not exist.</summary>
    public bool IsNotFound { get; }

    /// <summary>Dim "Not found" rows so the actionable rows stand out.</summary>
    public double RowOpacity => IsNotFound ? 0.5 : 1.0;

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
    public bool ShowMoveBackButton => _hasDevDrive && CanMoveBack && !IsConfirmingMove && !IsMoving;

    // ---- "Map path" / "Move & remap" for an UNDETECTED tool --------------------------------------

    /// <summary>Which action the inline confirm will run when accepted. Defaults to <see cref="PendingCacheAction.Move"/>.</summary>
    public PendingCacheAction PendingAction { get; set; } = PendingCacheAction.Move;

    /// <summary>User-editable folder this tool's cache lives at (for Map / Move &amp; remap). Two-way bound.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveAndRemap))]
    [NotifyPropertyChangedFor(nameof(IsMappedToDevDrive))]
    [NotifyPropertyChangedFor(nameof(IsOnDevDrive))]
    [NotifyCanExecuteChangedFor(nameof(MoveAndRemapCommand))]
    public partial string MapPath { get; set; } = string.Empty;

    /// <summary>True once the variable has been pointed at a user-chosen folder (no copy) for this tool.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapBlock))]
    [NotifyPropertyChangedFor(nameof(IsMappedToDevDrive))]
    [NotifyPropertyChangedFor(nameof(IsOnDevDrive))]
    public partial bool IsMapped { get; set; }

    public bool IsMappedToDevDrive
    {
        get
        {
            string? root = Path.GetPathRoot(MapPath);
            return IsMapped
                && !string.IsNullOrEmpty(root)
                && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(_devLetter);
        }
    }

    /// <summary>Current live location, including user-mapped paths rather than only the detection snapshot.</summary>
    public bool IsOnDevDrive => IsSet || IsMappedToDevDrive;

    /// <summary>
    /// Show the "tell us where it lives" block (Browse / Map path / Move &amp; remap) for an undetected
    /// tool that is not yet mapped or moved and has nothing in flight — instead of a dead "Not found".
    /// </summary>
    public bool ShowMapBlock => _hasDevDrive && IsNotFound && !IsSet && !IsMapped && !IsConfirmingMove && !IsMoving;

    /// <summary>
    /// "Move &amp; remap" is only valid when the chosen folder actually EXISTS — you cannot move a cache
    /// that isn't there. (Map path only repoints the variable, so it stays valid for a not-yet-created
    /// folder.) Re-evaluated live as <see cref="MapPath"/> changes (typed or browsed).
    /// </summary>
    public bool CanMoveAndRemap => _hasDevDrive && !string.IsNullOrWhiteSpace(MapPath) && _folderExists(MapPath);

    private bool HasDevDriveActions => _hasDevDrive;

    /// <summary>Immediately invalidates every mutation affordance when drive availability changes.</summary>
    public void DisableDevDriveActions()
    {
        _hasDevDrive = false;
        CanMove = false;
        CanMoveBack = false;
        IsConfirmingMove = false;
        OnPropertyChanged(nameof(ShowMoveBackButton));
        OnPropertyChanged(nameof(ShowMapBlock));
        OnPropertyChanged(nameof(CanMoveAndRemap));
        ConfirmMoveCommand.NotifyCanExecuteChanged();
        MoveBackCommand.NotifyCanExecuteChanged();
        MapPathActionCommand.NotifyCanExecuteChanged();
        MoveAndRemapCommand.NotifyCanExecuteChanged();
    }

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

            // A move is a copy plus an environment variable, so both visible columns move with it.
            if (outcome.TargetPath.Length > 0)
            {
                LocationText = outcome.TargetPath;
            }

            SetRedirectedBy(true);
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
            CanMove = _hasDevDrive;
            IsSet = false;
            CanMoveBack = false;
            AutomationName = $"{Header}, {StatusText}";

            // Moving back restores the detection-time location and clears the variable again.
            LocationText = ResolvedPath;
            SetRedirectedBy(false);
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
            // The chosen folder may live on C: or the Dev Drive — colour the pill honestly by where it is,
            // never grey for an active/tracked state.
            bool onDev = outcome.TargetPath.Length > 0
                && char.ToUpperInvariant(outcome.TargetPath[0]) == char.ToUpperInvariant(_devLetter);
            IsMapped = true;
            StatusKind = onDev ? "dev" : "system";
            StatusText = onDev ? $"On {_devLetter}:" : "Mapped";
            CanMoveBack = outcome.CanMoveBack;
            AutomationName = $"{Header}, {StatusText}";

            // Mapping is the variable half of a move without the copy: the folder is the user's choice.
            if (outcome.TargetPath.Length > 0)
            {
                LocationText = outcome.TargetPath;
            }

            SetRedirectedBy(true);
        }
    }

    /// <summary>
    /// Renders the "Redirected by" cell. The variable's name is fixed per tool; what changes is whether
    /// anything is pointing at it, so the dash and the name share one code path.
    /// </summary>
    private void SetRedirectedBy(bool redirected)
    {
        IsRedirected = redirected && Info.EnvironmentVariable.Length > 0;
        RedirectedByText = IsRedirected ? Info.EnvironmentVariable : "\u2014 not set \u2014";
    }

    [RelayCommand(CanExecute = nameof(CanMove))]
    private void Move() => _onMove(this);

    /// <summary>Confirm now performs the REAL reversible move (owned/serialised by the parent).</summary>
    [RelayCommand(CanExecute = nameof(HasDevDriveActions))]
    private void ConfirmMove() => _onConfirmMove(this);

    /// <summary>Cancel closes the prompt and changes nothing.</summary>
    [RelayCommand]
    private void CancelMove() => IsConfirmingMove = false;

    /// <summary>Cancels the in-flight move/move-back (owned/serialised by the parent, which holds the CTS).</summary>
    [RelayCommand]
    private void CancelActiveMove() => _onCancelActiveMove(this);

    /// <summary>Reverses a prior move: restores the env var + removes the Dev Drive copy (owned by the parent).</summary>
    [RelayCommand(CanExecute = nameof(HasDevDriveActions))]
    private void MoveBack() => _onMoveBack(this);

    /// <summary>Map path: point the variable at <see cref="MapPath"/> without copying (owned by the parent).</summary>
    [RelayCommand(CanExecute = nameof(HasDevDriveActions))]
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
