using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;

namespace DevDriveCore.Tests;

/// <summary>
/// Unit tests for <see cref="TrustFiltersViewModel"/> — the UI-agnostic Drive-health "Trust and filters"
/// sub-VM. It is linked into this test project (CommunityToolkit.Mvvm + DevDriveCore + the
/// <see cref="IElevatedFilterProbe"/> seam only — no WinUI), so the elevated "See Filters" probe flow
/// AND the trust/filter projection are verified headlessly with a FAKE probe. No real elevation or
/// process is ever spawned here.
/// </summary>
[TestClass]
public sealed class TrustFiltersViewModelTests
{
    private static TrustFiltersViewModel NewVm(IElevatedFilterProbe probe, Func<bool?>? reader = null) =>
        new(probe, reader);

    /// <summary>A trusted, NOT-policy-managed volume detail (drives the global-pref-dependent cases).</summary>
    private static DevDriveTrustInfo TrustedUnmanaged() =>
        FsutilDevDrvParser.Parse(0, TestData.FsutilTrusted, string.Empty)!;

    /// <summary>Drives the VM into the unelevated "See Filters" state for drive G: (probe not yet run).</summary>
    private static TrustFiltersViewModel UnelevatedForG(IElevatedFilterProbe probe, Func<bool?>? reader = null)
    {
        TrustFiltersViewModel vm = NewVm(probe, reader);
        // Unelevated load: no trust detail, a Dev Drive exists (G:) — offer the affordance.
        vm.UpdateTrust(null, PerformanceModeEvaluator.Evaluate(null, null), 'G');
        return vm;
    }

    // ---- UpdateTrust projection matrix ---------------------------------------------------------

    [TestMethod]
    public void UpdateTrust_NoDevDrive_HidesAffordanceAndDetail()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());

        vm.UpdateTrust(null, PerformanceModeEvaluator.Evaluate(null, null), devLetter: null);

        Assert.IsFalse(vm.HasTrustDetail);
        Assert.IsFalse(vm.ShowSeeFilters);
        Assert.IsFalse(vm.CouldNotReadFilters);
        Assert.IsFalse(vm.CanTurnOnPerformanceMode);
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
        Assert.AreEqual(string.Empty, vm.ProtectionStatusText);
    }

    [TestMethod]
    public void UpdateTrust_UnelevatedWithDevDrive_ShowsSeeFiltersAffordance()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());

        vm.UpdateTrust(null, PerformanceModeEvaluator.Evaluate(null, null), 'G');

        Assert.IsTrue(vm.ShowSeeFilters, "Unelevated + a Dev Drive exists => offer 'See Filters'.");
        Assert.IsFalse(vm.HasTrustDetail);
        Assert.IsFalse(vm.CouldNotReadFilters);
        // Unelevated => Unknown: must NOT claim a perf-mode state nor offer the local enable.
        Assert.IsFalse(vm.CanTurnOnPerformanceMode);
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
    }

    [TestMethod]
    public void UpdateTrust_TrustedUnmanaged_GlobalOff_PopulatesDetailAndOffersLocalEnable()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = TrustedUnmanaged();
        // Trusted + not policy-managed + global pref off => effectively Off.
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: false);

        vm.UpdateTrust(trust, effective, 'G');

        Assert.IsTrue(vm.HasTrustDetail);
        Assert.IsFalse(vm.ShowSeeFilters);
        Assert.AreEqual("Trusted", vm.TrustStateText);
        StringAssert.Contains(vm.AttachedFiltersText, "WdFilter");
        StringAssert.Contains(vm.AllowedFiltersText, "PrjFlt");
        StringAssert.Contains(vm.ProtectionStatusText, "Trusted");
        StringAssert.Contains(vm.PerformanceModeText, "Off");
        // Off + trusted + not policy-managed => offer the local one-click enable.
        Assert.IsTrue(vm.CanTurnOnPerformanceMode);
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
    }

    [TestMethod]
    public void UpdateTrust_TrustedUnmanaged_GlobalOn_WithdrawsLocalEnableAndShowsAsync()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = TrustedUnmanaged();
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true);

        vm.UpdateTrust(trust, effective, 'G');

        Assert.IsTrue(vm.HasTrustDetail);
        StringAssert.StartsWith(vm.PerformanceModeText, "On");
        StringAssert.Contains(vm.ProtectionStatusText, "asynchronously");
        Assert.IsFalse(vm.CanTurnOnPerformanceMode, "Perf mode already on => no 'Turn on' link.");
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
    }

    [TestMethod]
    public void UpdateTrust_TrustedUnmanaged_GlobalUnknown_DoesNotAssertOnOrOff()
    {
        // The honest unmanaged-but-unknown case: never claim On/Off, never offer the local enable.
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = TrustedUnmanaged();
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: null);

        vm.UpdateTrust(trust, effective, 'G');

        Assert.IsFalse(vm.CanTurnOnPerformanceMode);
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
        Assert.IsFalse(vm.PerformanceModeText.Contains("Off", StringComparison.OrdinalIgnoreCase),
            "Unknown must never read as 'Off'.");
    }

    [TestMethod]
    public void UpdateTrust_PolicyEnforced_PrefUnreadable_IsManaged_PointsToWindowsSecurityNotLocalEnable()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = FsutilDevDrvParser.Parse(0, TestData.FsutilPolicyEnforced, string.Empty)!;
        // Policy-managed AND the global pref couldn't be read => degrade honestly to Managed (don't guess).
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: null);

        vm.UpdateTrust(trust, effective, 'G');

        Assert.IsTrue(vm.IsPerformanceModePolicyControlled, "AV enforced by group policy => the org controls it.");
        Assert.IsFalse(vm.CanTurnOnPerformanceMode, "Policy-managed => no local one-click enable.");
        Assert.AreEqual(PerformanceModeAdvisor.PolicyControlledNote, vm.PerformanceModePolicyNote);
        StringAssert.Contains(vm.PerformanceModeText, "managed by your organization");
        StringAssert.Contains(vm.ProtectionStatusText, "managed by your organization");
        Assert.IsFalse(vm.PerformanceModeText.Contains("Off", StringComparison.OrdinalIgnoreCase),
            "Managed must never read as 'Off'.");
    }

    [TestMethod]
    public void UpdateTrust_PolicyEnforced_PrefOff_IsOffButPolicyControlledNotLocalEnable()
    {
        // Policy-managed AND the reliable pref says off => report Off, but the org controls it: point to
        // Windows Security, not a local one-click enable (the user can't flip a policy-managed setting).
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = FsutilDevDrvParser.Parse(0, TestData.FsutilPolicyEnforced, string.Empty)!;
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: false);

        vm.UpdateTrust(trust, effective, 'G');

        Assert.IsFalse(vm.CanTurnOnPerformanceMode, "Policy-managed => no local enable even when off.");
        Assert.IsTrue(vm.IsPerformanceModePolicyControlled, "Off + policy-enforced => org controls it.");
        StringAssert.Contains(vm.ProtectionStatusText, "managed by your organization");
    }

    [TestMethod]
    public void UpdateTrust_PolicyEnforced_PrefOn_IsOnAndNotesManaged()
    {
        // THIS MACHINE: policy-managed + perf mode on => "On (async)" is shown (never hidden), with a
        // "managed by your organization" note. No local "turn on" lever (already on, and policy-managed).
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = FsutilDevDrvParser.Parse(0, TestData.FsutilPolicyEnforced, string.Empty)!;
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true);

        vm.UpdateTrust(trust, effective, 'G');

        StringAssert.Contains(vm.PerformanceModeText, "On (async)");
        StringAssert.Contains(vm.ProtectionStatusText, "asynchronously");
        StringAssert.Contains(vm.ProtectionStatusText, "managed by your organization");
        Assert.IsFalse(vm.CanTurnOnPerformanceMode, "Already on (and policy-managed) => no local enable.");
    }

    [TestMethod]
    public void UpdateTrust_Untrusted_SetsUntrustedTextAndOffNoLocalEnable()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = FsutilDevDrvParser.Parse(0, TestData.FsutilUntrusted, string.Empty)!;
        EffectivePerformanceMode effective = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true);

        vm.UpdateTrust(trust, effective, 'G');

        Assert.AreEqual("Untrusted", vm.TrustStateText);
        StringAssert.Contains(vm.ProtectionStatusText, "Untrusted");
        // Off because untrusted, but the local enable is for trusted volumes only.
        Assert.IsFalse(vm.CanTurnOnPerformanceMode, "Untrusted => 'turn on' is not offered.");
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
    }

    // ---- SeeFilters probe flow (the new elevated-helper seam) ----------------------------------

    [TestMethod]
    public async Task SeeFilters_ProbeReturnsTrustInfo_PopulatesDetailAndHidesAffordance()
    {
        // Fake helper hands back exactly what the real elevated handoff would: a parsed fsutil result.
        DevDriveTrustInfo canned = TrustedUnmanaged();
        var probe = new FakeElevatedFilterProbe(canned);
        TrustFiltersViewModel vm = UnelevatedForG(probe);

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.AreEqual(1, probe.CallCount);
        Assert.AreEqual('G', probe.LastDriveLetter);
        Assert.IsTrue(vm.HasTrustDetail, "A non-null probe result populates trust/filters live.");
        Assert.IsFalse(vm.ShowSeeFilters, "Detail is now shown => the affordance is withdrawn.");
        Assert.IsFalse(vm.CouldNotReadFilters);
        Assert.IsFalse(vm.IsInspecting);
        Assert.AreEqual("Trusted", vm.TrustStateText);
        StringAssert.Contains(vm.AttachedFiltersText, "WdFilter");
    }

    [TestMethod]
    public async Task SeeFilters_ConsultsTheGlobalPrefForTrustedUnmanaged()
    {
        // After a successful probe, a trusted+unmanaged volume's verdict follows the global Defender pref.
        var probe = new FakeElevatedFilterProbe(TrustedUnmanaged());
        TrustFiltersViewModel vm = UnelevatedForG(probe, reader: () => false); // global says off

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        StringAssert.Contains(vm.PerformanceModeText, "Off");
        Assert.IsTrue(vm.CanTurnOnPerformanceMode, "Trusted + unmanaged + global off => offer the local enable.");
    }

    [TestMethod]
    public async Task SeeFilters_ProbeReturnsNull_KeepsAffordanceAndShowsNote()
    {
        // UAC declined / helper unavailable / failed => the probe returns null.
        var probe = new FakeElevatedFilterProbe(result: null);
        TrustFiltersViewModel vm = UnelevatedForG(probe);

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.AreEqual(1, probe.CallCount);
        Assert.IsTrue(vm.ShowSeeFilters, "Null result => keep the 'See Filters' affordance.");
        Assert.IsFalse(vm.HasTrustDetail);
        Assert.IsTrue(vm.CouldNotReadFilters, "Null result => show the brief 'couldn't read filters' note.");
        Assert.IsFalse(vm.IsInspecting);
    }

    [TestMethod]
    public async Task SeeFilters_SuccessAfterPriorFailure_ClearsTheCouldNotReadNote()
    {
        var probe = new FakeElevatedFilterProbe(result: null);
        TrustFiltersViewModel vm = UnelevatedForG(probe);

        // First attempt fails (UAC declined).
        await vm.SeeFiltersCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.CouldNotReadFilters);
        Assert.IsTrue(vm.ShowSeeFilters);

        // User retries and approves: the note clears and detail populates.
        probe.Result = TrustedUnmanaged();
        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.AreEqual(2, probe.CallCount);
        Assert.IsFalse(vm.CouldNotReadFilters, "A subsequent success clears the note.");
        Assert.IsTrue(vm.HasTrustDetail);
        Assert.IsFalse(vm.ShowSeeFilters);
    }

    [TestMethod]
    public async Task SeeFilters_NoDevLetter_DoesNotProbe()
    {
        var probe = new FakeElevatedFilterProbe(TrustedUnmanaged());
        TrustFiltersViewModel vm = NewVm(probe); // never given a drive letter

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.AreEqual(0, probe.CallCount, "With no known Dev Drive letter, the probe must not run.");
        Assert.IsFalse(vm.CouldNotReadFilters);
        Assert.IsFalse(vm.HasTrustDetail);
    }

    [TestMethod]
    public async Task SeeFilters_PassesTheStoredDriveLetterToTheProbe()
    {
        var probe = new FakeElevatedFilterProbe(TrustedUnmanaged());
        TrustFiltersViewModel vm = NewVm(probe);
        vm.UpdateTrust(null, PerformanceModeEvaluator.Evaluate(null, null), 'D'); // a different letter

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.AreEqual('D', probe.LastDriveLetter);
    }

    [TestMethod]
    public void CouldNotReadFiltersNote_IsConciseAndActionable()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());

        Assert.IsFalse(string.IsNullOrWhiteSpace(vm.CouldNotReadFiltersNote));
        StringAssert.Contains(vm.CouldNotReadFiltersNote, "filters");
    }

    // ---- Filter table projection ---------------------------------------------------------------
    //
    // The Drives room's Filter drivers tab is only populated after an elevated read, so on an
    // unelevated developer machine it cannot be verified by looking at it. These assertions are the
    // proof that the tab is right.

    [TestMethod]
    public void UpdateTrust_Elevated_ProjectsAFilterRowPerReportedFilter()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = TrustedUnmanaged();

        vm.UpdateTrust(trust, PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true), 'G');

        Assert.IsTrue(vm.HasFilters);
        // The union of attached and allowed, not just attached: an allowed filter that is NOT running
        // is the interesting row, because that absence is where the speed comes from.
        Assert.IsTrue(vm.Filters.Count >= trust.AttachedFilters.Count);
        Assert.IsTrue(vm.Filters.Any(row => row.Name == "PrjFlt"), "An allowed-but-detached filter must be listed.");
        Assert.IsTrue(vm.Filters.Any(row => row is { Name: "WdFilter", IsAttached: true }));
    }

    [TestMethod]
    public void UpdateTrust_Elevated_SummarisesHowManyFiltersActuallyRun()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = TrustedUnmanaged();

        vm.UpdateTrust(trust, PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true), 'G');

        int attached = vm.Filters.Count(row => row.IsAttached);
        Assert.AreEqual($"{attached} of {vm.Filters.Count} run on G:", vm.FilterSummaryText);
    }

    [TestMethod]
    public void UpdateTrust_Unelevated_LeavesTheFilterTableEmptyRatherThanGuessing()
    {
        TrustFiltersViewModel vm = UnelevatedForG(new FakeElevatedFilterProbe());

        Assert.HasCount(0, vm.Filters);
        Assert.IsFalse(vm.HasFilters);
        Assert.AreEqual(string.Empty, vm.FilterSummaryText);
    }

    [TestMethod]
    public async Task SeeFilters_PopulatesTheFilterTableFromTheElevatedRead()
    {
        var probe = new FakeElevatedFilterProbe(TrustedUnmanaged());
        TrustFiltersViewModel vm = UnelevatedForG(probe);
        Assert.HasCount(0, vm.Filters, "Precondition: nothing to show before the elevated read.");

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.HasFilters);
        StringAssert.Contains(vm.FilterSummaryText, "run on G:");
    }

    [TestMethod]
    public async Task SeeFilters_ProbeDeclined_LeavesTheFilterTableEmpty()
    {
        // A declined UAC prompt must not leave a half-populated table behind — an empty table with an
        // explanation is honest; stale rows claiming to describe this volume are not.
        var probe = new FakeElevatedFilterProbe(result: null);
        TrustFiltersViewModel vm = UnelevatedForG(probe);

        await vm.SeeFiltersCommand.ExecuteAsync(null);

        Assert.HasCount(0, vm.Filters);
        Assert.IsFalse(vm.HasFilters);
    }

    // ---- OnPerformanceModeEnabled callback ----------------------------------------------------

    [TestMethod]
    public void OnPerformanceModeEnabled_FlipsTextOnAndWithdrawsDeepLink()
    {
        TrustFiltersViewModel vm = NewVm(new FakeElevatedFilterProbe());
        DevDriveTrustInfo trust = TrustedUnmanaged();
        // Precondition: trusted + unmanaged + global off => the local enable link is offered.
        vm.UpdateTrust(trust, PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: false), 'G');
        Assert.IsTrue(vm.CanTurnOnPerformanceMode, "Precondition: the local enable link is offered.");

        vm.OnPerformanceModeEnabled();

        StringAssert.StartsWith(vm.PerformanceModeText, "On");
        StringAssert.Contains(vm.ProtectionStatusText, "asynchronously");
        Assert.IsFalse(vm.CanTurnOnPerformanceMode, "After enabling, the deep link is withdrawn.");
        Assert.IsFalse(vm.IsPerformanceModePolicyControlled);
    }

    /// <summary>
    /// A FAKE <see cref="IElevatedFilterProbe"/> — returns a canned <see cref="DevDriveTrustInfo"/> (or
    /// null) WITHOUT any real elevation or process. <see cref="Result"/> is settable so a test can model
    /// a decline-then-approve retry.
    /// </summary>
    private sealed class FakeElevatedFilterProbe : IElevatedFilterProbe
    {
        public FakeElevatedFilterProbe(DevDriveTrustInfo? result = null) => Result = result;

        /// <summary>The trust info the next probe returns (null models UAC-declined / unavailable / failed).</summary>
        public DevDriveTrustInfo? Result { get; set; }

        /// <summary>How many times <see cref="ProbeAsync"/> was invoked.</summary>
        public int CallCount { get; private set; }

        /// <summary>The drive letter passed to the most recent probe.</summary>
        public char? LastDriveLetter { get; private set; }

        public Task<DevDriveTrustInfo?> ProbeAsync(char driveLetter, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastDriveLetter = driveLetter;
            return Task.FromResult(Result);
        }
    }
}
