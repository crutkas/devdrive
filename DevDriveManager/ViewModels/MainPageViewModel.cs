using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Services;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.ViewModels;

/// <summary>
/// ViewModel for the Dev Drive settings page. Calls <see cref="IDevDriveService"/> on a background
/// thread (so the UI thread never blocks) and projects the results into observable state. It also
/// composes the per-section sub-ViewModels (Performance, Package caches, Drive health), wiring each
/// to the detected Dev Drive after load.
/// </summary>
/// <remarks>
/// SAFETY: every mutating affordance is wired to a SAFE inline confirm (preview-only) or to app-local
/// settings only. The page changes nothing on the user's machine.
/// </remarks>
public partial class MainPageViewModel : ObservableObject
{
    private readonly IDevDriveService _service;

    public MainPageViewModel()
        : this(DevDriveService.CreateDefault())
    {
    }

    /// <summary>Test/DI-friendly constructor.</summary>
    public MainPageViewModel(IDevDriveService service)
    {
        _service = service;

        Ecosystems = EcosystemsViewModel.CreateDefault(DispatchToUi);
        DriveHealth = new DriveHealthViewModel();
        Trust = new TrustFiltersViewModel(new ElevatedFilterProbe(), _service.GetDefenderPerformanceMode);
    }

    private static bool DispatchToUi(Action action)
    {
        if (App.DispatcherQueue.HasThreadAccess)
        {
            action();
            return true;
        }

        return App.DispatcherQueue.TryEnqueue(() => action());
    }

    /// <summary>Raised when the user invokes "Create Dev Drive" (the page navigates to the stub).</summary>
    public event Action? NavigateToCreateRequested;

    /// <summary>
    /// Raised when the user asks to turn on Defender performance mode from the Trust &amp; filters deep
    /// link. The page owns the confirm + elevation flow (the ViewModel never changes machine config
    /// itself) and calls <see cref="OnPerformanceModeEnabled"/> back on success.
    /// </summary>
    public event Action? PerformanceModeEnableRequested;

    /// <summary>Raised to launch a URI (e.g. <c>ms-settings:storage</c> or Windows Security) via the shell.</summary>
    public event Action<string>? LaunchUriRequested;

    // ---- Section sub-ViewModels ----------------------------------------------------------------

    /// <summary>
    /// The unified per-ecosystem experience (Concept A): one card per language stack, composing the
    /// reused package-cache move engine + the workload benchmark. Replaces the old separate Performance
    /// suite, Package caches, and Suggestions sections.
    /// </summary>
    public EcosystemsViewModel Ecosystems { get; }

    /// <summary>The reused Performance test suite — delegated to <see cref="Ecosystems"/> so existing
    /// page code-behind (e.g. perf-mode dismissal) keeps working unchanged.</summary>
    public PerformanceSuiteViewModel Suite => Ecosystems.Suite;

    /// <summary>The reused package-cache view-model — delegated to <see cref="Ecosystems"/>.</summary>
    public PackageCachesViewModel PackageCaches => Ecosystems.Caches;

    public DriveHealthViewModel DriveHealth { get; }

    /// <summary>
    /// Drive-health "Trust and filters" detail + the unelevated "See Filters" affordance (reads
    /// trust/filters live via a short-lived UAC-elevated READ-ONLY helper, no full-app restart).
    /// </summary>
    public TrustFiltersViewModel Trust { get; }

    /// <summary>
    /// True when the app is running under the UI-test safe-mutation seam (DDM_UITEST_SAFE_MUTATIONS=1).
    /// Surfaced via a collapsed indicator so the automated suite can confirm real moves are faked.
    /// </summary>
    public bool IsSafeMutationMode => MutationComposition.IsSafeMutationMode;

    /// <summary>All fixed volumes on the system.</summary>
    public ObservableCollection<VolumeRowViewModel> Volumes { get; } = new();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasLoaded { get; set; }

    // ---- Status banner -------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool HasDevDrive { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Checking for Dev Drives\u2026";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>Loads (or reloads) volumes and Dev Drive detail off the UI thread.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        Ecosystems.SuspendDriveDependentActions(
            "Checking drive status. Cache locations remain visible; move actions will return after the refresh completes.");
        try
        {
            // All platform work happens on a background thread; the await resumes on the UI thread.
            (IReadOnlyList<VolumeInfo> volumes, DevDriveTrustInfo? trust, bool? globalPerf, char? devLetter, bool devTrusted) result =
                await Task.Run(() =>
                {
                    IReadOnlyList<VolumeInfo> vols = _service.GetVolumes();
                    VolumeInfo? dev = vols.FirstOrDefault(v => v.IsDevDrive && v.DriveLetter.HasValue);
                    DevDriveTrustInfo? trustInfo = dev?.DriveLetter is char dl
                        ? _service.GetDevDriveTrustInfo(dl)
                        : null;
                    // The global Defender preference is reliable (1 = on/async, 0 = off/sync; it agrees
                    // with Windows Security) and readable unelevated. Read it whenever a Dev Drive exists
                    // so the effective verdict can report On/Off even before elevation; the evaluator
                    // decides how to combine it with the (elevation-only) trust/policy detail.
                    bool? globalPerf = dev is not null ? _service.GetDefenderPerformanceMode() : null;
                    // M4: capture the UNELEVATED trusted bit so an untrusted detected Dev Drive is never
                    // reported "On" when we can't read the authoritative elevated trust detail.
                    return (vols, trustInfo, globalPerf, dev?.DriveLetter, dev?.IsTrusted ?? false);
                });

            Volumes.Clear();
            int volumeIndex = 0;
            foreach (VolumeInfo volume in result.volumes)
            {
                Volumes.Add(new VolumeRowViewModel(volume) { BandAlt = (volumeIndex++ % 2) == 1 });
            }

            // Authoritative per-volume effective performance mode: from the (reliable, unelevated-readable)
            // global Defender preference combined with the elevation-dependent trust/policy detail. When
            // unelevated, a detected Dev Drive still reports On/Off from the global preference — unless its
            // unelevated trusted bit is clear (M4), in which case performance mode is reported Off.
            EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(
                result.trust, result.globalPerf,
                isDevDrive: result.devLetter is not null,
                unelevatedTrusted: result.devTrusted);

            UpdateStatus(result.volumes, result.devLetter);
            UpdateTrust(result.trust, effective, result.devLetter);
            InitializeSections(result.volumes, result.devLetter, result.trust, effective);
        }
        catch (Exception ex)
        {
            // M5: a WMI/native volume-query failure must degrade gracefully, not fault the async-void
            // OnLoaded caller. Surface a visible error banner and still mark HasLoaded so the UI renders.
            HasDevDrive = false;
            StatusSeverity = InfoBarSeverity.Error;
            StatusTitle = "Couldn't read your drives";
            StatusMessage =
                "Something went wrong querying this PC's volumes. Reload to try again. " +
                $"Details: {ex.Message}";
            Ecosystems.SetDevDriveUnavailable(
                "Drive status is unavailable. Cache locations remain visible; reload to enable move actions.");
        }
        finally
        {
            IsLoading = false;
            HasLoaded = true;
        }
    }

    [RelayCommand]
    private void CreateDevDrive() => NavigateToCreateRequested?.Invoke();

    [RelayCommand]
    private void ManageInStorage() => LaunchUriRequested?.Invoke("ms-settings:storage");

    /// <summary>Trust &amp; filters deep link: asks the page to run the confirmed, elevated enable flow.</summary>
    [RelayCommand]
    private void EnablePerformanceMode() => PerformanceModeEnableRequested?.Invoke();

    /// <summary>Policy-controlled case: open Windows Security to the Dev Drive protection settings.</summary>
    [RelayCommand]
    private void OpenWindowsSecurity() =>
        LaunchUriRequested?.Invoke(PerformanceModeAdvisor.WindowsSecurityDevDriveProtectionUri);

    /// <summary>
    /// Page callback after performance mode is successfully turned on: reflect the new state in the
    /// Trust &amp; filters card (text flips to "On", the deep link is withdrawn).
    /// </summary>
    public void OnPerformanceModeEnabled() => Trust.OnPerformanceModeEnabled();

    /// <summary>Initializes read-only cache discovery on every PC and Dev Drive-dependent sections when available.</summary>
    private void InitializeSections(
        IReadOnlyList<VolumeInfo> volumes, char? devLetter, DevDriveTrustInfo? trust, EffectivePerformanceMode effective)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        char systemLetter = systemRoot.Length > 0 ? char.ToUpperInvariant(systemRoot[0]) : 'C';
        string? devRoot = devLetter is char dl ? $"{dl}:\\" : null;

        Ecosystems.Initialize(systemRoot, devRoot, devLetter, systemLetter);
        Ecosystems.ApplyPerformanceMode(effective);

        VolumeInfo? devVolume = devLetter is char driveLetter
            ? volumes.FirstOrDefault(v => v.DriveLetter == driveLetter)
            : null;
        if (devVolume is not null)
        {
            DriveHealth.Initialize(devVolume, trust, effective);
        }
    }

    private void UpdateStatus(IReadOnlyList<VolumeInfo> volumes, char? devLetter)
    {
        VolumeInfo? dev = devLetter is char dl
            ? volumes.FirstOrDefault(v => v.DriveLetter == dl)
            : null;

        if (dev is not null)
        {
            HasDevDrive = true;

            // CHANGE 1: no green "active" banner — the Drive health card already conveys active/healthy,
            // so the banner is redundant here. Reserve the banner for the no-Dev-Drive-yet and error
            // states only (kept Informational; it is collapsed in the active state via the XAML binding).
            StatusSeverity = InfoBarSeverity.Informational;
            StatusTitle = string.Empty;
            StatusMessage = string.Empty;
        }
        else
        {
            HasDevDrive = false;
            StatusSeverity = InfoBarSeverity.Informational;
            StatusTitle = "No Dev Drive yet";
            StatusMessage =
                "A Dev Drive is a ReFS volume tuned for developer workloads (repos, package caches, " +
                "build output). Create one to get faster file I/O and Defender performance mode.";
        }
    }

    private void UpdateTrust(DevDriveTrustInfo? trust, EffectivePerformanceMode effective, char? devLetter) =>
        Trust.UpdateTrust(trust, effective, devLetter);
}
