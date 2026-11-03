using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Drives the Drive-health "Trust and filters" detail: the elevated trust/filter projection (trust
/// state, Defender performance mode, attached/allowed filters) AND the unelevated "See Filters"
/// affordance that reads them live via a short-lived, UAC-elevated, READ-ONLY helper — without
/// restarting the whole app as administrator.
/// </summary>
/// <remarks>
/// <para>
/// UI-agnostic (CommunityToolkit.Mvvm + DevDriveCore only — no WinUI), so the See-Filters flow and the
/// trust projection are unit-tested headlessly with a fake <see cref="IElevatedFilterProbe"/>. On a
/// non-null probe result it populates the same observable state the elevated-at-launch path does; on a
/// null result it keeps the affordance and shows a brief "couldn't read filters" note.
/// </para>
/// <para>
/// SAFETY: read-only. The only privileged action is the user-initiated, UAC-gated filter query behind
/// <see cref="IElevatedFilterProbe"/>; this VM never mutates machine state.
/// </para>
/// </remarks>
public partial class TrustFiltersViewModel : ObservableObject
{
    private readonly IElevatedFilterProbe _probe;
    private readonly Func<bool?> _defenderPerformanceModeReader;
    private char? _devLetter;

    /// <summary>
    /// Creates the VM over an elevated filter probe (a fake in tests, the real helper in the app) and an
    /// optional reader for the global <c>Get-MpPreference PerformanceModeStatus</c>. The reader supplies
    /// the reliable On/Off signal used after a successful See-Filters probe. It defaults to a reader that
    /// returns <c>null</c> (unknown) so headless tests don't need to supply one.
    /// </summary>
    public TrustFiltersViewModel(IElevatedFilterProbe probe, Func<bool?>? defenderPerformanceModeReader = null)
    {
        _probe = probe;
        _defenderPerformanceModeReader = defenderPerformanceModeReader ?? (static () => null);
    }

    // ---- Trust detail (populated when elevated, or after a successful See-Filters probe) -----------

    /// <summary>True when trust/filter detail is available — the SettingsExpander is shown and expanded.</summary>
    [ObservableProperty]
    public partial bool HasTrustDetail { get; set; }

    /// <summary>
    /// True when trust/filter detail needs elevation and hasn't been read yet — show the compact
    /// "See Filters" affordance (with the de-emphasized Restart-as-administrator fallback).
    /// </summary>
    [ObservableProperty]
    public partial bool ShowSeeFilters { get; set; }

    /// <summary>True after a See-Filters probe couldn't read the filters (UAC declined / unavailable / failed).</summary>
    [ObservableProperty]
    public partial bool CouldNotReadFilters { get; set; }

    /// <summary>True while a See-Filters probe is in flight (disables the button).</summary>
    [ObservableProperty]
    public partial bool IsInspecting { get; set; }

    [ObservableProperty]
    public partial string TrustStateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PerformanceModeText { get; set; } = string.Empty;

    /// <summary>
    /// One honest line stating the volume's Defender Dev Drive protection status, e.g. "Trusted \u00B7
    /// Defender Dev Drive protection managed by your organization" or "Trusted \u00B7 Defender Dev Drive
    /// protection scanning asynchronously". The authoritative per-volume source is Windows Security \u203A
    /// Dev Drive protection \u203A "See volumes".
    /// </summary>
    [ObservableProperty]
    public partial string ProtectionStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AttachedFiltersText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AllowedFiltersText { get; set; } = string.Empty;

    /// <summary>
    /// True when performance mode is off AND not controlled by group policy on this PC — the local,
    /// one-click "Turn on performance mode" deep link applies.
    /// </summary>
    [ObservableProperty]
    public partial bool CanTurnOnPerformanceMode { get; set; }

    /// <summary>
    /// True when performance mode is managed by group policy on this PC (the <see cref="EffectivePerformanceMode"/>
    /// state is <see cref="PerformanceModeEffectiveness.Managed"/>) — the deep link opens Windows Security
    /// instead of claiming to flip a setting the user can't change.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPerformanceModePolicyControlled { get; set; }

    /// <summary>Honest caption shown for the policy-controlled case.</summary>
    public string PerformanceModePolicyNote => PerformanceModeAdvisor.PolicyControlledNote;

    /// <summary>Inline note shown when a See-Filters probe couldn't read the filters.</summary>
    public string CouldNotReadFiltersNote =>
        "Couldn't read filters \u2014 the prompt was declined or the query failed. Try again, or use Restart as administrator.";

    /// <summary>
    /// The filters in the Dev Drive's I/O path, highest altitude first — the order a write passes
    /// through them.
    /// </summary>
    /// <remarks>
    /// Never reassigned, so the Drives table's binding survives a See-Filters probe: the collection is
    /// cleared and refilled in place.
    /// </remarks>
    public ObservableCollection<FilterRowViewModel> Filters { get; } = [];

    /// <summary>True once we have a filter list to show. Drives the table-versus-empty-state choice.</summary>
    [ObservableProperty]
    public partial bool HasFilters { get; set; }

    /// <summary>
    /// How many of the listed filters are actually attached, as "3 of 6 run on G:". The single fact
    /// the filters tab exists to deliver.
    /// </summary>
    [ObservableProperty]
    public partial string FilterSummaryText { get; set; } = string.Empty;

    /// <summary>
    /// Projects the (optional, elevation-dependent) trust/filter detail and the derived effective perf
    /// mode into observable state. Mirrors the elevated-at-launch projection. When <paramref name="trust"/>
    /// is null and a Dev Drive exists, the unelevated "See Filters" affordance is shown instead.
    /// </summary>
    public void UpdateTrust(DevDriveTrustInfo? trust, EffectivePerformanceMode effective, char? devLetter)
    {
        _devLetter = devLetter;

        if (devLetter is null)
        {
            HasTrustDetail = false;
            ShowSeeFilters = false;
            CouldNotReadFilters = false;
            CanTurnOnPerformanceMode = false;
            IsPerformanceModePolicyControlled = false;
            ProtectionStatusText = string.Empty;
            SetFilters(null, null);
            return;
        }

        if (trust is null)
        {
            // fsutil needs elevation; trust/filter detail couldn't be read at launch. Offer "See Filters".
            HasTrustDetail = false;
            ShowSeeFilters = true;
            CouldNotReadFilters = false;
            CanTurnOnPerformanceMode = false;
            IsPerformanceModePolicyControlled = false;
            ProtectionStatusText = string.Empty;
            SetFilters(null, devLetter);
            return;
        }

        HasTrustDetail = true;
        ShowSeeFilters = false;
        CouldNotReadFilters = false;

        TrustStateText = trust.TrustState switch
        {
            DevDriveTrustState.Trusted => "Trusted",
            DevDriveTrustState.Untrusted => "Untrusted",
            _ => "Unknown",
        };

        // Honest, specific perf-mode line from the effective mode (trust state + policy + global pref),
        // plus a one-line protection status. Attached antivirus filters are deliberately NOT used as a
        // scan-mode signal (they attach in both async and sync modes).
        PerformanceModeText = effective.Display;
        ProtectionStatusText = BuildProtectionStatus(trust, effective);

        AttachedFiltersText = trust.AttachedFilters.Count > 0
            ? string.Join(", ", trust.AttachedFilters)
            : "None";
        AllowedFiltersText = trust.AllowedFilters.Count > 0
            ? string.Join(", ", trust.AllowedFilters)
            : "Not specified";

        // Honest deep-link state, driven by the EFFECTIVE mode: offer the local one-click enable ONLY when
        // perf mode is genuinely off on a trusted volume AND it isn't policy-managed (the user can't flip a
        // policy-managed setting locally); when policy manages it, point to Windows Security instead.
        CanTurnOnPerformanceMode =
            effective.State == PerformanceModeEffectiveness.Off
            && trust.TrustState == DevDriveTrustState.Trusted
            && !effective.PolicyEnforced;
        IsPerformanceModePolicyControlled =
            effective.State == PerformanceModeEffectiveness.Managed
            || (effective.PolicyEnforced && effective.State != PerformanceModeEffectiveness.On);

        SetFilters(trust, devLetter);
    }

    /// <summary>
    /// Rebuilds the filter table from a trust reading, in place so the Drives table's binding survives.
    /// </summary>
    /// <remarks>
    /// The altitude lookup is a per-filter registry read, which any user can do — it is only the filter
    /// <i>names</i> that need elevation. That is why the altitude column is populated the moment the
    /// list arrives rather than needing a second, deeper probe.
    /// </remarks>
    private void SetFilters(DevDriveTrustInfo? trust, char? devLetter)
    {
        Filters.Clear();

        if (trust is null || devLetter is not char letter)
        {
            HasFilters = false;
            FilterSummaryText = string.Empty;
            return;
        }

        foreach (FilterDriverInfo info in FilterDriverProjection.Project(trust.AttachedFilters, trust.AllowedFilters))
        {
            Filters.Add(new FilterRowViewModel(info, letter));
        }

        HasFilters = Filters.Count > 0;

        int attached = Filters.Count(row => row.IsAttached);
        FilterSummaryText = Filters.Count == 0
            ? "no filters reported"
            : $"{attached} of {Filters.Count} run on {letter}:";
    }

    /// <summary>Builds the one-line Defender Dev Drive protection status for the Trust &amp; filters expander.</summary>
    private static string BuildProtectionStatus(DevDriveTrustInfo trust, EffectivePerformanceMode effective)
    {
        if (trust.TrustState == DevDriveTrustState.Untrusted)
        {
            return "Untrusted \u00B7 not protected as a Dev Drive (scanned synchronously)";
        }

        if (trust.TrustState != DevDriveTrustState.Trusted)
        {
            return "Trust state unknown \u00B7 run as admin to confirm";
        }

        string detail = effective.State switch
        {
            PerformanceModeEffectiveness.Managed =>
                "Defender Dev Drive protection managed by your organization",
            PerformanceModeEffectiveness.On =>
                "Defender Dev Drive protection on \u2014 scanning asynchronously",
            PerformanceModeEffectiveness.Off =>
                "Defender Dev Drive protection on \u2014 scanning synchronously (performance mode off)",
            _ => "Defender Dev Drive protection \u2014 status unavailable",
        };

        // When the On/Off verdict came from a policy-managed PC, note the org control without hiding the fact.
        if (effective.PolicyEnforced && effective.State != PerformanceModeEffectiveness.Managed)
        {
            detail += " \u00B7 managed by your organization";
        }

        return $"Trusted \u00B7 {detail}";
    }

    /// <summary>
    /// Reflects a successful performance-mode enable: text flips to "On" and the deep link is withdrawn.
    /// </summary>
    public void OnPerformanceModeEnabled()
    {
        PerformanceModeText = "On \u2014 scanning asynchronously";
        ProtectionStatusText = "Trusted \u00B7 Defender Dev Drive protection on \u2014 scanning asynchronously";
        CanTurnOnPerformanceMode = false;
        IsPerformanceModePolicyControlled = false;
    }

    /// <summary>
    /// "See Filters": reads trust/filter detail live via the elevated, READ-ONLY helper and populates the
    /// detail in place (no full-app restart). On a null result (UAC declined / unavailable / failed) the
    /// affordance stays and an inline "couldn't read filters" note is shown.
    /// </summary>
    [RelayCommand]
    private async Task SeeFiltersAsync()
    {
        if (_devLetter is not char dl || IsInspecting)
        {
            return;
        }

        IsInspecting = true;
        CouldNotReadFilters = false;
        try
        {
            DevDriveTrustInfo? trust = await _probe.ProbeAsync(dl);
            if (trust is null)
            {
                // Keep the affordance; surface the brief inline note.
                CouldNotReadFilters = true;
                return;
            }

            // Same projection as the elevated-at-launch path. The global Defender preference is reliable
            // and drives the On/Off verdict for this (now trust-confirmed) volume, so read it off the UI
            // thread and pass it through.
            bool? globalPerf = await Task.Run(_defenderPerformanceModeReader);
            EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerf);
            UpdateTrust(trust, effective, dl);
        }
        finally
        {
            IsInspecting = false;
        }
    }
}
