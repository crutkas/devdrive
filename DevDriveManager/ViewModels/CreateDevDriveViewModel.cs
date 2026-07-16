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
/// <para><b>SAFETY.</b> Nothing destructive runs until the user clears the explicit
/// <see cref="ConfirmRequested"/> dialog. Even then:</para>
/// <list type="bullet">
///   <item><description><b>VHDX</b> — <see cref="ExecuteConfirmedAsync"/> calls the real
///   <see cref="IDevDriveCreationService.CreateVhdDevDriveAsync"/> (create + attach only; the ReFS
///   Dev Drive <i>format</i> is a separate admin step, reported as pending). This is never exercised by
///   automated tests or dev validation because the app is not launched.</description></item>
///   <item><description><b>Resize preview</b> — <see cref="IVolumeResizer.PreviewAsync"/> runs a real
///   elevated READ-ONLY feasibility check (the helper's <c>--whatif</c> mode: measures actual
///   reclaimable space, runs every guard, mutates nothing), falling back to the pure-arithmetic
///   <see cref="IDevDriveCreationService.SimulateResize"/> when the helper is unavailable or UAC is
///   declined.</description></item>
///   <item><description><b>Resize execute</b> — the destructive shrink/repartition/format
///   (<see cref="IVolumeResizer.ExecuteAsync"/>) is gated THREE ways: the default-off-by-default
///   <see cref="DevDriveManager.Services.ResizeFeatureGate"/> flag, a second honest confirmation, and
///   UAC elevation (the helper also re-runs the guards). Only an explicit self-hosting build enables
///   the flag.</description></item>
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
    private bool _pendingRealResize;
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

    public string GuardrailsMessage => ResizeFeatureGate.EnableRealResizeExecute
        ? "Nothing changes until you confirm. Resizing requires a second destructive confirmation and administrator approval."
        : "Nothing changes until you confirm. Creating a VHDX is real; resizing a volume is preview-only in this build.";

    /// <summary>
    /// Whether the gated "Apply resize" (real, destructive) button is offered. Only ever <c>true</c>
    /// after a successful real preview AND when the default-off <see cref="ResizeFeatureGate"/> is
    /// enabled by an explicit self-hosting build.
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
        _pendingRealResize = false;
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
        _pendingRealResize = false;
        ConfirmRequested?.Invoke(BuildConfirmRequest(_pendingPlan));
    }

    /// <summary>
    /// DESTRUCTIVE real resize. Gated three ways: it is only reachable when the default-off-by-default
    /// <see cref="ResizeFeatureGate.EnableRealResizeExecute"/> flag is on AND a real preview said the
    /// plan can proceed (<see cref="CanApplyRealResize"/>), it raises a second, explicitly honest
    /// confirmation, and the helper itself still requires UAC elevation and re-runs the guards. The
    /// normal build keeps the flag off; a deliberate self-hosting build may enable it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyRealResize))]
    private void ApplyRealResize()
    {
        if (!CanApplyRealResize || _pendingPlan is null)
        {
            return;
        }

        _pendingRealResize = true;
        ConfirmRequested?.Invoke(BuildRealResizeConfirm(_pendingPlan));
    }

    /// <summary>
    /// Runs the operation the user just confirmed. VHDX = real create+attach (gated, never auto-run);
    /// resize = a real elevated READ-ONLY feasibility preview (falling back to pure simulation when the
    /// helper is unavailable or UAC is declined). The destructive real resize only runs from the
    /// separately-gated <see cref="ApplyRealResizeCommand"/> path. Invoked by the page only when the
    /// confirm dialog's primary button is clicked.
    /// </summary>
    public async Task ExecuteConfirmedAsync()
    {
        if (_pendingPlan is null || IsBusy)
        {
            return;
        }

        // DESTRUCTIVE branch: only set by ApplyRealResizeCommand, which requires the explicit opt-in gate.
        if (_pendingRealResize)
        {
            _pendingRealResize = false;
            await ExecuteRealResizeAsync(_pendingPlan);
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
                // it is the real provisioner (create + attach the VHDX, no format). Under the
                // DDM_UITEST_SAFE_MUTATIONS seam it is a SafeFakeVhdProvisioner that simulates the result
                // with no real disk I/O, so the UI suite can exercise this path safely.
                DevDriveCreationResult result = await _creationService.CreateVhdDevDriveAsync(_pendingPlan);
                CompletionTitle = result.Success
                    ? $"{_pendingPlan.DriveLetter}: \u201C{_pendingPlan.Label}\u201D is almost ready"
                    : "Couldn't create the Dev Drive";
                CompletionMessage = result.FormatPending
                    ? $"{result.Summary} Until it's formatted it shows up as an unformatted disk."
                    : result.Summary;

                if (result.Success)
                {
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
    }

    // Real, elevated, READ-ONLY feasibility preview. Asks the helper (--whatif) for the actual
    // reclaimable space and a go/no-go; falls back to the pure-compute SimulateResize when the helper
    // is unavailable, missing, or the user declines elevation (PreviewAsync returns null).
    private async Task PreviewResizeAsync(DevDriveCreationPlan plan)
    {
        _lastFeasibility = null;
        CanApplyRealResize = false;

        ResizeFeasibility? feasibility = await _volumeResizer.PreviewAsync(BuildResizePlan(plan));
        if (feasibility is null)
        {
            // SAFETY: pure arithmetic fallback — no shrink/partition/format API is touched.
            DevDriveResizeSimulation sim = _creationService.SimulateResize(plan);
            CompletionTitle = $"Previewed {sim.DevDriveLetter}: \u201C{plan.Label}\u201D (estimated)";
            CompletionMessage =
                $"The elevated feasibility check was unavailable, so this is an estimate. Shrinking " +
                $"{sim.SourceVolumeLetter}: by {ByteSizeFormatter.Format(sim.ShrinkBytes)} would create a " +
                $"{ByteSizeFormatter.Format(sim.DevDriveBytes)} Dev Drive, leaving {sim.SourceVolumeLetter}: with " +
                $"{ByteSizeFormatter.Format(sim.NewSourceFreeBytes)} free. No changes were made.";
            CanReturnToForm = true;
            IsComplete = true;
            return;
        }

        _lastFeasibility = feasibility;
        // Gate the destructive button: only offered when the feature flag is ON and the real check passed.
        CanApplyRealResize = ResizeFeatureGate.EnableRealResizeExecute && feasibility.CanProceed;

        if (feasibility.CanProceed)
        {
            CompletionTitle = $"{feasibility.SourceVolumeLetter}: can be resized for {feasibility.NewDriveLetter}:";
            CompletionMessage =
                $"A real feasibility check found {ByteSizeFormatter.Format(feasibility.ReclaimableBytes)} reclaimable on " +
                $"{feasibility.SourceVolumeLetter}:. Shrinking it by {ByteSizeFormatter.Format(feasibility.AlignedShrinkBytes)} " +
                $"would create a {ByteSizeFormatter.Format(feasibility.AlignedShrinkBytes)} ReFS Dev Drive at " +
                $"{feasibility.NewDriveLetter}:. Nothing was changed — this was read-only.";
        }
        else
        {
            CompletionTitle = $"{feasibility.SourceVolumeLetter}: can't be resized right now";
            CompletionMessage =
                $"{feasibility.Reason} Nothing was changed — the feasibility check is read-only.";
        }

        CanReturnToForm = true;
        IsComplete = true;
    }

    // The destructive shrink → repartition → Format-Volume -DevDrive, via the elevated helper
    // (--execute). Unreachable unless the self-hosting feature flag is on; the helper re-runs the guards.
    private async Task ExecuteRealResizeAsync(DevDriveCreationPlan plan)
    {
        if (!ResizeFeatureGate.EnableRealResizeExecute)
        {
            // Hard backstop: never run the destructive path while the flag is off, even if invoked.
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        CanReturnToForm = false;
        try
        {
            ResizeExecuteOutcome outcome = await _volumeResizer.ExecuteAsync(BuildResizePlan(plan));
            CompletionTitle = outcome.Success
                ? $"{outcome.NewDriveLetter}: \u201C{plan.Label}\u201D Dev Drive created"
                : "The resize didn't complete";
            CompletionMessage = outcome.Message;
            CanApplyRealResize = false;

            if (outcome.Success)
            {
                HasUsableDevDrive = true;
                IsComplete = true;
            }
            else if (outcome.Executed)
            {
                HasUsableDevDrive = false;
                CompletionMessage += " Do not retry until you verify the disk layout in Disk Management.";
                IsComplete = true;
            }
            else
            {
                HasError = true;
                ErrorMessage = outcome.Message;
                IsComplete = false;
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Couldn't complete the resize: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
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
            MaximumSelectableBytes = source.FreeBytes;
            HasEligibleSource = source.FreeBytes >= DevDriveSizeMath.MinimumSizeBytesExact;
            NoSourceMessage = HasEligibleSource
                ? string.Empty
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
            SummaryDetail = $"{kind} VHDX. Format needs admin. Host keeps {remaining} free.";
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
    private static ResizePlan BuildResizePlan(DevDriveCreationPlan plan) => new()
    {
        SourceVolumeLetter = plan.SourceVolumeLetter is { } c ? char.ToUpperInvariant(c) : 'C',
        ShrinkBytes = plan.SizeBytes,
        NewDriveLetter = char.ToUpperInvariant(plan.DriveLetter),
        Label = string.IsNullOrWhiteSpace(plan.Label) ? "DevDrive" : plan.Label.Trim(),
    };

    private ConfirmRequest BuildConfirmRequest(DevDriveCreationPlan plan)
    {
        string size = ByteSizeFormatter.Format(plan.SizeBytes);

        if (plan.Source == DevDriveCreationSource.ResizeExistingVolume)
        {
            DevDriveResizeSimulation sim = _creationService.SimulateResize(plan);
            return new ConfirmRequest
            {
                Title = "Check resize feasibility?",
                Message =
                    $"This runs a READ-ONLY feasibility check on {sim.SourceVolumeLetter}: to measure the real " +
                    $"reclaimable space and report whether a {size} Dev Drive at {plan.DriveLetter}: is possible. " +
                    "Windows may prompt for administrator approval. Nothing is shrunk, partitioned, or formatted — " +
                    "the check only reads the disk layout.",
                Steps = sim.Steps,
                ConfirmText = "Check feasibility",
                IsDestructive = false,
            };
        }

        return new ConfirmRequest
        {
            Title = "Create Dev Drive?",
            Message =
                $"This creates and attaches a raw {size} VHDX at {plan.VhdFilePath}. " +
                "You then finish in Disk Management (admin): initialize the disk, create a partition, assign a letter, and format it as a ReFS Dev Drive.",
            Steps = new[]
            {
                $"Create a {(plan.VhdIsDynamic ? "dynamically expanding" : "fixed-size")} {size} virtual disk at {plan.VhdFilePath}.",
                "Attach the disk (Windows re-attaches it at every boot).",
                "Then in Disk Management (admin): initialize the disk, create a partition, assign a letter, and format it as a ReFS Dev Drive.",
            },
            ConfirmText = "Create",
            IsDestructive = false,
        };
    }

    // The HONEST second confirmation for the destructive real resize. Only ever raised by
    // ApplyRealResizeCommand, which requires the explicit self-hosting gate. Plain-language about irreversibility.
    private ConfirmRequest BuildRealResizeConfirm(DevDriveCreationPlan plan)
    {
        ResizeFeasibility? f = _lastFeasibility;
        char source = plan.SourceVolumeLetter is { } c ? char.ToUpperInvariant(c) : 'C';
        string size = ByteSizeFormatter.Format(f?.AlignedShrinkBytes is { } a && a > 0 ? a : plan.SizeBytes);

        return new ConfirmRequest
        {
            Title = $"Repartition {source}:?",
            Message =
                $"This will REALLY shrink {source}: by {size} and create a {size} ReFS Dev Drive at {plan.DriveLetter}:. " +
                "It repartitions your system drive and is NOT trivially reversible — back up important data first. " +
                "Windows will prompt for administrator approval before anything changes.",
            Steps = f?.Steps ?? Array.Empty<string>(),
            ConfirmText = "Shrink and create",
            IsDestructive = true,
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
