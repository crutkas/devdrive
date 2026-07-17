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
/// Drives the "Create a Dev Drive" flow (Treatment&#160;A). Owns the single source-of-truth size value
/// and keeps the disk-bar handle, the Slider and the NumberBox in sync through one
/// <see cref="ApplySize"/> funnel built on the pure <see cref="DevDriveSizeMath"/>.
/// </summary>
/// <remarks>
/// <para><b>SAFETY.</b> Nothing destructive runs until the user explicitly confirms it. Even then:</para>
/// <list type="bullet">
///   <item><description><b>VHDX</b> — <see cref="ExecuteConfirmedAsync"/> creates, attaches, initializes,
///   partitions, formats, and verifies the new VHDX through one UAC-elevated helper invocation.</description></item>
///   <item><description><b>Resize preview</b> — <see cref="IVolumeResizer.PreviewAsync"/> runs a real
///   elevated READ-ONLY feasibility check (the helper's <c>--whatif</c> mode: measures actual
///   reclaimable space, runs every guard, and mutates nothing). Execution remains unavailable when
///   the helper cannot produce a live preview.</description></item>
///   <item><description><b>Resize execute</b> — the destructive shrink/repartition/format
///   (<see cref="IVolumeResizer.ExecuteAsync"/>) requires a successful live preview, an explicit
///   Create action, and UAC elevation; the helper then re-runs every guard immediately before mutation.</description></item>
/// </list>
/// </remarks>
public partial class CreateDevDriveViewModel : ObservableObject
{
    /// <summary>Default Dev Drive size we pre-select (clamped to the source's free space): 256 GiB.</summary>
    private const double DefaultPreferredBytes = 256d * DevDriveSizeMath.BytesPerGigabyte;

    private readonly IDevDriveService _volumeService;
    private readonly IDevDriveCreationService _creationService;
    private readonly IVolumeResizer _volumeResizer;

    private IReadOnlyList<VolumeInfo> _volumes = Array.Empty<VolumeInfo>();
    private char _systemLetter = 'C';
    private SourceVolumeOption? _hostVolume;
    private DevDriveCreationPlan? _pendingPlan;
    private ResizeFeasibility? _lastFeasibility;
    private bool _isSyncing;
    private bool _initialized;

    public CreateDevDriveViewModel(
        IDevDriveService volumeService,
        IDevDriveCreationService creationService,
        IVolumeResizer volumeResizer)
    {
        _volumeService = volumeService;
        _creationService = creationService;
        _volumeResizer = volumeResizer;
    }

    /// <summary>Convenience factory wiring the real volume query and the (seam-aware) creation + resize services.</summary>
    public static CreateDevDriveViewModel CreateDefault() =>
        new(
            DevDriveService.CreateDefault(),
            MutationComposition.CreateDevDriveCreationService(),
            MutationComposition.CreateVolumeResizer());

    /// <summary>Raised by <see cref="CreateCommand"/>; the page shows a blocking confirmation dialog.</summary>
    public event Action<ConfirmRequest>? ConfirmRequested;

    /// <summary>Raised when the user asks to pick the VHDX file path (the page owns the file picker).</summary>
    public event Action? BrowseVhdPathRequested;

    /// <summary>Raised when the flow should return to Dev Drive management (Cancel / Back / onboarding).</summary>
    public event Action? NavigateBackRequested;

    /// <summary>Raised after a verified creation so the page can refresh shared app state.</summary>
    public event Func<Task>? DevDriveCreated;

    /// <summary>Available drive letters (e.g. "D:"), excluding ones already in use.</summary>
    public ObservableCollection<string> AvailableDriveLetters { get; } = new();

    /// <summary>NTFS/ReFS volumes with at least 50 GB free that could be shrunk for the resize path.</summary>
    public ObservableCollection<SourceVolumeOption> AvailableSourceVolumes { get; } = new();

    // ---- Source choice --------------------------------------------------------------------------

    /// <summary>0 = new VHDX, 1 = resize an existing volume.</summary>
    [ObservableProperty]
    public partial int SourceIndex { get; set; }

    public bool IsVhdx => SourceIndex == 0;

    public bool IsResize => SourceIndex == 1;

    // ---- Identity & placement -------------------------------------------------------------------

    [ObservableProperty]
    public partial string Label { get; set; } = "DevDrive";

    [ObservableProperty]
    public partial string SelectedDriveLetter { get; set; } = "D:";

    [ObservableProperty]
    public partial int SelectedSourceVolumeIndex { get; set; } = -1;

    // ---- VHDX-only ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string VhdFilePath { get; set; } = string.Empty;

    /// <summary>0 = dynamically expanding, 1 = fixed size.</summary>
    [ObservableProperty]
    public partial int VhdTypeIndex { get; set; }

    // ---- Size control (SelectedBytes is the single source of truth) -----------------------------

    [ObservableProperty]
    public partial double SelectedBytes { get; set; }

    [ObservableProperty]
    public partial double SliderValueGb { get; set; }

    [ObservableProperty]
    public partial double SliderMaximumGb { get; set; } = 50d;

    [ObservableProperty]
    public partial double SizeNumberValue { get; set; }

    /// <summary>0 = GB, 1 = MB.</summary>
    [ObservableProperty]
    public partial int SizeUnitIndex { get; set; }

    // ---- Disk-bar inputs ------------------------------------------------------------------------

    [ObservableProperty]
    public partial double TotalBytes { get; set; }

    [ObservableProperty]
    public partial double UsedBytes { get; set; }

    [ObservableProperty]
    public partial double MaximumSelectableBytes { get; set; }

    [ObservableProperty]
    public partial string UsedBarLabel { get; set; } = "Used + protected";

    [ObservableProperty]
    public partial string RemainingBarLabel { get; set; } = "Remaining";

    [ObservableProperty]
    public partial string DevBarLabel { get; set; } = "Dev Drive";

    // ---- Live readouts --------------------------------------------------------------------------

    [ObservableProperty]
    public partial string SpaceAvailableLabel { get; set; } = "Space available";

    [ObservableProperty]
    public partial string SpaceAvailableText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevDriveSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RemainingAfterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SizeErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSizeError { get; set; }

    // ---- Summary / primary action ---------------------------------------------------------------

    [ObservableProperty]
    public partial string SummaryTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrimaryActionText { get; set; } = "Create";

    // ---- Source availability --------------------------------------------------------------------

    [ObservableProperty]
    public partial bool HasEligibleSource { get; set; } = true;

    [ObservableProperty]
    public partial string NoSourceMessage { get; set; } = string.Empty;

    // ---- Transient state ------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsComplete { get; set; }

    [ObservableProperty]
    public partial bool HasUsableDevDrive { get; set; }

    [ObservableProperty]
    public partial bool CanReturnToForm { get; set; }

    [ObservableProperty]
    public partial bool CanCreate { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    // ---- Completion / onboarding ----------------------------------------------------------------

    [ObservableProperty]
    public partial string CompletionTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompletionMessage { get; set; } = string.Empty;

    public string GuardrailsMessage =>
        "VHDX creation requires administrator approval. Drive resizing is checked before any changes are made.";

    /// <summary>
    /// Whether the Create button is offered after a successful live resize preview.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRealResizeCommand))]
    public partial bool CanApplyRealResize { get; set; }

    /// <summary>Chosen drive letter without the colon (e.g. <c>'D'</c>).</summary>
    public char DriveLetterChar =>
        SelectedDriveLetter is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) : 'D';

    /// <summary>Currently selected display unit.</summary>
    public DevDriveSizeUnit CurrentUnit =>
        SizeUnitIndex == 1 ? DevDriveSizeUnit.Megabytes : DevDriveSizeUnit.Gigabytes;

    /// <summary>The resize source the user picked, or <c>null</c> when none is eligible/selected.</summary>
    public SourceVolumeOption? SelectedSourceVolume =>
        SelectedSourceVolumeIndex >= 0 && SelectedSourceVolumeIndex < AvailableSourceVolumes.Count
            ? AvailableSourceVolumes[SelectedSourceVolumeIndex]
            : null;

    /// <summary>Reads the host volumes (read-only/safe) and seeds the form. Call from the page's Loaded.</summary>
    public async Task LoadAsync()
    {
        if (_initialized)
        {
            return;
        }

        IsLoading = true;
        try
        {
            _volumes = await Task.Run(() => _volumeService.GetVolumes());
            _systemLetter = ResolveSystemLetter();
            BuildDriveLetters();
            BuildSourceVolumes();
            VhdFilePath = $@"{_systemLetter}:\DevDrives\DevDrive.vhdx";
            ResolveHostVolume();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Couldn't read the machine's volumes: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        _initialized = true;
        Reconfigure();
    }

    // ---- Commands -------------------------------------------------------------------------------

    [RelayCommand]
    private void SelectVhdxSource() => SourceIndex = 0;

    [RelayCommand]
    private void SelectResizeSource() => SourceIndex = 1;

    [RelayCommand]
    private void BrowseVhdPath() => BrowseVhdPathRequested?.Invoke();

    [RelayCommand]
    private void Cancel() => NavigateBackRequested?.Invoke();

    /// <summary>Used by the onboarding card's suggestions and its "Done" button to return to management.</summary>
    [RelayCommand]
    private void BackToManagement() => NavigateBackRequested?.Invoke();

    [RelayCommand]
    private void ReturnToForm()
    {
        if (IsBusy)
        {
            return;
        }

        _pendingPlan = null;
        _lastFeasibility = null;
        CanApplyRealResize = false;
        CanReturnToForm = false;
        HasUsableDevDrive = false;
        IsComplete = false;
    }

    /// <summary>Builds the plan and asks the page to show the gating confirmation dialog. Executes nothing.</summary>
    [RelayCommand]
    private void Create()
    {
        if (!CanCreate)
        {
            return;
        }

        _pendingPlan = BuildPlan();
        ConfirmRequested?.Invoke(BuildConfirmRequest(_pendingPlan));
    }

    /// <summary>
    /// DESTRUCTIVE real resize. The post-preview Create action is the explicit confirmation; the elevated
    /// helper still re-runs every guard immediately before mutation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyRealResize))]
    private async Task ApplyRealResizeAsync()
    {
        if (IsBusy ||
            !CanApplyRealResize ||
            _pendingPlan is not { Source: DevDriveCreationSource.ResizeExistingVolume } plan ||
            _lastFeasibility is not { CanProceed: true })
        {
            return;
        }

        CanApplyRealResize = false;
        await ExecuteRealResizeAsync(plan);
        if (HasUsableDevDrive)
        {
            await NotifyDevDriveCreatedAsync();
        }
    }

    /// <summary>
    /// Runs the operation the user just confirmed. VHDX = complete elevated creation (never auto-run);
    /// resize = a real elevated READ-ONLY feasibility preview. The destructive real resize runs only
    /// from the separately gated <see cref="ApplyRealResizeAsync"/> path. Invoked by the page only when
    /// the initial confirmation dialog's primary button is clicked.
    /// </summary>
    public async Task ExecuteConfirmedAsync()
    {
        if (_pendingPlan is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        HasUsableDevDrive = false;
        CanReturnToForm = false;
        try
        {
            if (_pendingPlan.Source == DevDriveCreationSource.Vhdx)
            {
                // SAFETY: the creation service runs only here, after explicit confirmation. In production
                // it runs the complete transaction through the elevated helper. Under the
                // DDM_UITEST_SAFE_MUTATIONS seam it is a SafeFakeVhdProvisioner that simulates the result
                // with no real disk I/O, so the UI suite can exercise this path safely.
                DevDriveCreationResult result = await _creationService.CreateVhdDevDriveAsync(_pendingPlan);
                CompletionTitle = result.Success
                    ? $"{result.VhdResult?.DriveLetter ?? _pendingPlan.DriveLetter}: created"
                    : result.StateUnknown
                        ? "Creation needs verification"
                        : "Couldn't create the Dev Drive";
                ulong createdBytes = result.VhdResult is { SizeBytes: > 0 } vhdResult
                    ? vhdResult.SizeBytes
                    : _pendingPlan.SizeBytes;
                CompletionMessage = result.Success
                    ? $"{ByteSizeFormatter.Format(createdBytes)} ReFS Dev Drive"
                    : result.Summary;

                if (result.Success)
                {
                    HasUsableDevDrive = true;
                    IsComplete = true;
                }
                else if (result.StateUnknown)
                {
                    HasUsableDevDrive = false;
                    CompletionMessage += " Do not retry until you verify the VHDX and disk layout in Disk Management.";
                    IsComplete = true;
                }
                else
                {
                    HasError = true;
                    ErrorMessage = result.Summary;
                    IsComplete = false;
                }
            }
            else
            {
                await PreviewResizeAsync(_pendingPlan);
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Couldn't complete the operation: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        if (HasUsableDevDrive)
        {
            await NotifyDevDriveCreatedAsync();
        }
    }

    // Real, elevated, READ-ONLY feasibility preview. Asks the helper (--whatif) for the actual
    // reclaimable space and a go/no-go. Execution stays unavailable without this live preview.
    private async Task PreviewResizeAsync(DevDriveCreationPlan plan)
    {
        _lastFeasibility = null;
        CanApplyRealResize = false;

        ResizeFeasibility? feasibility = await _volumeResizer.PreviewAsync(BuildResizePlan(plan));
        if (feasibility is null)
        {
            CompletionTitle = "Resize check unavailable";
            CompletionMessage = "No changes were made. Try again.";
            CanReturnToForm = true;
            IsComplete = true;
            return;
        }

        _lastFeasibility = feasibility;
        CanApplyRealResize = feasibility.CanProceed;

        if (feasibility.CanProceed)
        {
            CompletionTitle =
                $"Resizing {feasibility.SourceVolumeLetter}: to {ByteSizeFormatter.Format(feasibility.SourceSizeBytesAfter)}";
            CompletionMessage =
                $"Creating {feasibility.NewDriveLetter}: at {ByteSizeFormatter.Format(feasibility.AlignedShrinkBytes)}";
        }
        else
        {
            CompletionTitle = $"Can't resize {feasibility.SourceVolumeLetter}:";
            CompletionMessage = feasibility.Reason;
        }

        CanReturnToForm = true;
        IsComplete = true;
    }

    // The destructive shrink → repartition → Format-Volume -DevDrive, via the elevated helper
    // (--execute). Reachable only from Create after a passing live preview.
    private async Task ExecuteRealResizeAsync(DevDriveCreationPlan plan)
    {
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            ResizeExecuteOutcome outcome =
                await _volumeResizer.ExecuteAsync(BuildResizePlan(plan, _lastFeasibility));
            CompletionTitle = outcome.Success
                ? $"{outcome.NewDriveLetter}: created"
                : "The resize didn't complete";
            CompletionMessage = outcome.Success
                ? $"{ByteSizeFormatter.Format(outcome.DevDriveBytes)} ReFS Dev Drive"
                : outcome.Message;
            CanApplyRealResize = false;

            if (outcome.Success)
            {
                HasUsableDevDrive = true;
                CanReturnToForm = false;
                IsComplete = true;
            }
            else if (outcome.Executed)
            {
                HasUsableDevDrive = false;
                CanReturnToForm = false;
                CompletionMessage += " Do not retry until you verify the disk layout in Disk Management.";
                IsComplete = true;
            }
            else
            {
                HasError = true;
                ErrorMessage = outcome.Message;
                CanReturnToForm = false;
                IsComplete = false;
            }
        }

        catch (Exception ex)
        {
            CanApplyRealResize = false;
            CanReturnToForm = false;
            HasError = true;
            ErrorMessage = $"Couldn't complete the resize: {ex.Message}";
            IsComplete = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task NotifyDevDriveCreatedAsync()
    {
        if (DevDriveCreated is not { } handlers)
        {
            return;
        }

        foreach (Func<Task> handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            await handler();
        }
    }

    // ---- Property change reactions --------------------------------------------------------------

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsVhdx));
        OnPropertyChanged(nameof(IsResize));
        if (!_initialized)
        {
            return;
        }

        Reconfigure();
    }

    partial void OnSelectedSourceVolumeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedSourceVolume));
        if (_initialized && IsResize)
        {
            Reconfigure();
        }
    }

    partial void OnVhdFilePathChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        ResolveHostVolume();
        if (IsVhdx)
        {
            Reconfigure();
        }
        else
        {
            UpdateSummary();
            RefreshCanCreate();
        }
    }

    partial void OnLabelChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateSummary();
        RefreshCanCreate();
    }

    partial void OnSelectedDriveLetterChanged(string value)
    {
        OnPropertyChanged(nameof(DriveLetterChar));
        if (_initialized)
        {
            UpdateSummary();
            RefreshCanCreate();
        }
    }

    partial void OnVhdTypeIndexChanged(int value)
    {
        if (_initialized)
        {
            UpdateSummary();
        }
    }

    partial void OnSelectedBytesChanged(double value) => ApplySize(value);

    partial void OnSliderValueGbChanged(double value) => ApplySize(DevDriveSizeMath.GigabytesToBytes(value));

    partial void OnSizeNumberValueChanged(double value) => ApplySize(DevDriveSizeMath.UnitToBytes(value, CurrentUnit));

    partial void OnSizeUnitIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentUnit));
        ApplySize(SelectedBytes); // re-express the same size in the new unit
    }

    partial void OnIsBusyChanged(bool value) => RefreshCanCreate();

    partial void OnIsCompleteChanged(bool value) => RefreshCanCreate();

    // ---- Sync funnel & derivations --------------------------------------------------------------

    /// <summary>
    /// The single place every size input (bar handle, slider, number box, unit switch) flows through.
    /// Clamps to <c>[50&#160;GiB, max]</c>, then writes the clamped value back to all three controls and
    /// recomputes the readouts/summary. Re-entrancy is blocked with <see cref="_isSyncing"/>.
    /// </summary>
    private void ApplySize(double requestedBytes)
    {
        if (_isSyncing || !_initialized)
        {
            return;
        }

        if (double.IsNaN(requestedBytes) || double.IsInfinity(requestedBytes))
        {
            requestedBytes = 0d; // e.g. an emptied NumberBox — treat as below-minimum
        }

        _isSyncing = true;
        try
        {
            double max = MaximumSelectableBytes;
            double clamped = DevDriveSizeMath.ClampSizeBytes(requestedBytes, max);

            SelectedBytes = clamped;
            SliderValueGb = Math.Round(DevDriveSizeMath.BytesToGigabytes(clamped));
            SizeNumberValue = CurrentUnit == DevDriveSizeUnit.Megabytes
                ? Math.Round(DevDriveSizeMath.BytesToUnit(clamped, CurrentUnit))
                : Math.Round(DevDriveSizeMath.BytesToUnit(clamped, CurrentUnit), 2);

            SpaceAvailableText = ByteSizeFormatter.Format(ToUlong(max));
            DevDriveSizeText = ByteSizeFormatter.Format(ToUlong(clamped));
            RemainingAfterText = ByteSizeFormatter.Format(ToUlong(DevDriveSizeMath.RemainingBytes(max, clamped)));
            DevBarLabel = $"Dev Drive \u00B7 {DevDriveSizeText}";
            RemainingBarLabel = $"Free \u00B7 {RemainingAfterText}";

            // Error is driven by the RAW request so typing 40 GB still shows "Minimum 50 GB".
            if (DevDriveSizeMath.IsBelowMinimum(requestedBytes))
            {
                SizeErrorText = "Minimum 50 GB";
            }
            else if (DevDriveSizeMath.ExceedsMaximum(requestedBytes, max))
            {
                SizeErrorText = "Not enough space";
            }
            else
            {
                SizeErrorText = string.Empty;
            }

            HasSizeError = SizeErrorText.Length > 0;
        }
        finally
        {
            _isSyncing = false;
        }

        UpdateSummary();
        RefreshCanCreate();
    }

    /// <summary>Re-seeds the disk-bar, slider bounds, default size and labels for the current source.</summary>
    private void Reconfigure()
    {
        SourceVolumeOption? source = IsResize ? SelectedSourceVolume : _hostVolume;

        if (source is null)
        {
            TotalBytes = 0d;
            UsedBytes = 0d;
            MaximumSelectableBytes = 0d;
            HasEligibleSource = false;
            NoSourceMessage = IsResize
                ? "No NTFS or ReFS volume has at least 50 GB free to shrink. Create a new VHDX instead."
                : "Couldn't find a host volume with enough free space for a Dev Drive.";
        }
        else
        {
            TotalBytes = source.SizeBytes;
            UsedBytes = source.UsedBytes;
            MaximumSelectableBytes = IsVhdx
                ? Math.Max(0d, source.FreeBytes - DevDriveSizeMath.VhdContainerHeadroomBytes)
                : source.FreeBytes;
            HasEligibleSource = MaximumSelectableBytes >= DevDriveSizeMath.MinimumSizeBytes;
            NoSourceMessage = HasEligibleSource
                ? string.Empty
                : IsVhdx
                    ? $"{source.Letter}: has only {ByteSizeFormatter.Format(source.FreeBytes)} free — a 50 GB VHDX Dev Drive also needs 128 MB for partition metadata."
                    : $"{source.Letter}: has only {ByteSizeFormatter.Format(source.FreeBytes)} free — a Dev Drive needs at least 50 GB.";
        }

        string usedSize = ByteSizeFormatter.Format(ToUlong(UsedBytes));
        if (IsResize)
        {
            UsedBarLabel = $"Used \u00B7 {usedSize}";
            SpaceAvailableLabel = "Shrinkable space available";
            PrimaryActionText = "Preview resize";
        }
        else
        {
            UsedBarLabel = $"Used \u00B7 {usedSize}";
            SpaceAvailableLabel = "Host volume free space";
            PrimaryActionText = "Create";
        }

        SliderMaximumGb = Math.Max(
            DevDriveSizeMath.BytesToGigabytes(DevDriveSizeMath.MinimumSizeBytes),
            Math.Floor(DevDriveSizeMath.BytesToGigabytes(MaximumSelectableBytes)));

        double preferred = Math.Min(MaximumSelectableBytes, DefaultPreferredBytes);
        ApplySize(DevDriveSizeMath.ClampSizeBytes(preferred, MaximumSelectableBytes));

        UpdateSummary();
        RefreshCanCreate();
    }

    private void UpdateSummary()
    {
        char letter = DriveLetterChar;
        string size = ByteSizeFormatter.Format(ToUlong(SelectedBytes));
        string remaining = ByteSizeFormatter.Format(ToUlong(DevDriveSizeMath.RemainingBytes(MaximumSelectableBytes, SelectedBytes)));

        if (IsResize)
        {
            char src = SelectedSourceVolume?.Letter ?? _systemLetter;
            SummaryTitle = $"{letter}: \u2014 {size} ReFS Dev Drive";
            SummaryDetail = $"Shrinks {src}:, keeps {remaining} free. Preview only \u2014 nothing changes.";
        }
        else
        {
            string kind = VhdTypeIndex == 0 ? "dynamically expanding" : "fixed-size";
            SummaryTitle = $"{letter}: \u2014 {size} ReFS Dev Drive";
            SummaryDetail = $"{kind} VHDX. Creates and formats with admin approval. Host keeps {remaining} free.";
        }
    }

    private void RefreshCanCreate()
    {
        CanCreate =
            _initialized &&
            !IsBusy &&
            !IsComplete &&
            HasEligibleSource &&
            !HasSizeError &&
            TotalBytes > 0d &&
            MaximumSelectableBytes >= DevDriveSizeMath.MinimumSizeBytes &&
            !string.IsNullOrWhiteSpace(Label) &&
            !string.IsNullOrWhiteSpace(SelectedDriveLetter) &&
            AvailableDriveLetters.Contains(SelectedDriveLetter) &&
            SelectedBytes >= DevDriveSizeMath.MinimumSizeBytes &&
            (IsResize ? SelectedSourceVolume is not null : !string.IsNullOrWhiteSpace(VhdFilePath));
    }

    private DevDriveCreationPlan BuildPlan()
    {
        ulong size = (ulong)Math.Round(Math.Max(DevDriveSizeMath.MinimumSizeBytes, SelectedBytes));
        var plan = new DevDriveCreationPlan
        {
            Source = IsResize ? DevDriveCreationSource.ResizeExistingVolume : DevDriveCreationSource.Vhdx,
            Label = string.IsNullOrWhiteSpace(Label) ? "DevDrive" : Label.Trim(),
            DriveLetter = DriveLetterChar,
            SizeBytes = size,
            VhdFilePath = VhdFilePath,
            VhdIsDynamic = VhdTypeIndex == 0,
        };

        if (IsResize && SelectedSourceVolume is { } v)
        {
            plan = plan with
            {
                SourceVolumeLetter = v.Letter,
                SourceVolumeSizeBytes = v.SizeBytes,
                SourceVolumeUsedBytes = v.UsedBytes,
                SourceVolumeFreeBytes = v.FreeBytes,
            };
        }

        return plan;
    }

    // Projects the creation plan onto the resize-broker's ResizePlan (the JSON payload the elevated
    // helper consumes). Building one changes nothing.
    private static ResizePlan BuildResizePlan(
        DevDriveCreationPlan plan,
        ResizeFeasibility? approvedPreview = null) => new()
    {
        SourceVolumeLetter = plan.SourceVolumeLetter is { } c ? char.ToUpperInvariant(c) : 'C',
        ShrinkBytes = plan.SizeBytes,
        NewDriveLetter = char.ToUpperInvariant(plan.DriveLetter),
        Label = string.IsNullOrWhiteSpace(plan.Label) ? "DevDrive" : plan.Label.Trim(),
        ExpectedDiskNumber = approvedPreview?.DiskNumber,
        ExpectedDiskUniqueId = approvedPreview?.DiskUniqueId ?? string.Empty,
        ExpectedPartitionNumber = approvedPreview?.PartitionNumber,
        ExpectedPartitionOffsetBytes = approvedPreview?.PartitionOffsetBytes,
        ExpectedPartitionGuid = approvedPreview?.PartitionGuid ?? string.Empty,
        ExpectedAlignedShrinkBytes = approvedPreview?.AlignedShrinkBytes,
    };

    private ConfirmRequest BuildConfirmRequest(DevDriveCreationPlan plan)
    {
        string size = ByteSizeFormatter.Format(plan.SizeBytes);

        if (plan.Source == DevDriveCreationSource.ResizeExistingVolume)
        {
            char source = plan.SourceVolumeLetter is { } c ? char.ToUpperInvariant(c) : 'C';
            return new ConfirmRequest
            {
                Title = "Check resize?",
                Message = $"Check whether {source}: can create {plan.DriveLetter}: at {size}. No changes will be made.",
                Steps = Array.Empty<string>(),
                ConfirmText = "Check",
                IsDestructive = false,
            };
        }

        return new ConfirmRequest
        {
            Title = $"Create {plan.DriveLetter}:?",
            Message = $"Create a {size} Dev Drive at {plan.VhdFilePath}. Windows will request administrator approval.",
            Steps = Array.Empty<string>(),
            ConfirmText = "Create",
            IsDestructive = false,
        };
    }

    private void BuildDriveLetters()
    {
        var used = new HashSet<char>(
            _volumes.Where(v => v.DriveLetter.HasValue).Select(v => char.ToUpperInvariant(v.DriveLetter!.Value)));

        AvailableDriveLetters.Clear();
        foreach (char c in "DEFGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(c))
            {
                AvailableDriveLetters.Add($"{c}:");
            }
        }

        // Re-notify even if the value is unchanged: the ComboBox may clear its selection while
        // AvailableDriveLetters is still being populated, so force it to reflect the default (next free) letter.
        SelectedDriveLetter = AvailableDriveLetters.Count > 0 ? AvailableDriveLetters[0] : string.Empty;
        OnPropertyChanged(nameof(SelectedDriveLetter));
    }

    private void BuildSourceVolumes()
    {
        AvailableSourceVolumes.Clear();
        foreach (VolumeInfo v in _volumes)
        {
            if (v.DriveLetter is not char letter)
            {
                continue;
            }

            if (v.IsDevDrive)
            {
                continue; // don't offer to shrink an existing Dev Drive
            }

            bool recognised =
                v.FileSystemType.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                v.FileSystemType.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
            if (!recognised)
            {
                continue; // skip RAW/recovery/EFI and other system partitions
            }

            if (v.FreeBytes < DevDriveSizeMath.MinimumSizeBytesExact)
            {
                continue; // can't carve out a 50 GB minimum
            }

            AvailableSourceVolumes.Add(new SourceVolumeOption(
                char.ToUpperInvariant(letter), DescribeVolume(v), v.SizeBytes, v.UsedBytes, v.FreeBytes));
        }

        int index = -1;
        for (int i = 0; i < AvailableSourceVolumes.Count; i++)
        {
            if (AvailableSourceVolumes[i].Letter == _systemLetter)
            {
                index = i;
                break;
            }
        }

        if (index < 0 && AvailableSourceVolumes.Count > 0)
        {
            index = 0;
        }

        SelectedSourceVolumeIndex = index;
    }

    private void ResolveHostVolume()
    {
        char root = _systemLetter;
        if (VhdFilePath is { Length: >= 2 } p && p[1] == ':' && char.IsLetter(p[0]))
        {
            root = char.ToUpperInvariant(p[0]);
        }

        VolumeInfo? match =
            _volumes.FirstOrDefault(v => v.DriveLetter is char l && char.ToUpperInvariant(l) == root)
            ?? _volumes.FirstOrDefault(v => v.DriveLetter is char l && char.ToUpperInvariant(l) == _systemLetter);

        _hostVolume = match?.DriveLetter is char ml
            ? new SourceVolumeOption(char.ToUpperInvariant(ml), DescribeVolume(match), match.SizeBytes, match.UsedBytes, match.FreeBytes)
            : null;
    }

    private static string DescribeVolume(VolumeInfo v)
    {
        string label = string.IsNullOrWhiteSpace(v.Label) ? "Local Disk" : v.Label;
        char letter = v.DriveLetter is char l ? char.ToUpperInvariant(l) : '?';
        return $"{letter}:  {label}  \u00B7  {ByteSizeFormatter.Format(v.FreeBytes)} free of {ByteSizeFormatter.Format(v.SizeBytes)}";
    }

    private static char ResolveSystemLetter()
    {
        string? root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return root is { Length: > 0 } && char.IsLetter(root[0]) ? char.ToUpperInvariant(root[0]) : 'C';
    }

    private static ulong ToUlong(double value) => value <= 0d ? 0UL : (ulong)Math.Round(value);
}

/// <summary>A volume the user can pick as a resize source, or the resolved VHDX host volume.</summary>
public sealed record SourceVolumeOption(char Letter, string Display, ulong SizeBytes, ulong UsedBytes, ulong FreeBytes)
{
    /// <summary>ComboBox shows this when no item template is supplied.</summary>
    public override string ToString() => Display;
}

/// <summary>What the page needs to render the gating confirmation dialog for a creation/resize.</summary>
public sealed record ConfirmRequest
{
    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    public string ConfirmText { get; init; } = "Confirm";

    public bool IsDestructive { get; init; }
}
