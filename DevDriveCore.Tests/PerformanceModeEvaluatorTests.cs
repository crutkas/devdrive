using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the effective-performance-mode derivation. The verdict comes from the volume's
/// (elevation-dependent) trust/policy detail plus the global Defender preference — NOT from attached
/// antivirus minifilters, which attach in both async and sync modes and must NOT drive the on/off
/// decision. Each rule is exercised in isolation with a purpose-built <see cref="DevDriveTrustInfo"/>.
/// </summary>
[TestClass]
public sealed class PerformanceModeEvaluatorTests
{
    private static DevDriveTrustInfo Trusted(
        bool policyEnforced = false,
        string[]? attached = null) =>
        new()
        {
            TrustState = DevDriveTrustState.Trusted,
            AntivirusPolicyEnforced = policyEnforced,
            AttachedFilters = attached ?? Array.Empty<string>(),
        };

    // ---- Unelevated (trust == null) ⇒ Unknown; never assert from the unreliable global pref --------

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [DataRow(null)]
    public void Evaluate_Unelevated_IsUnknown_RegardlessOfGlobalPref(bool? global)
    {
        EffectivePerformanceMode result = PerformanceModeEvaluator.Evaluate(trust: null, globalPerformanceModeOn: global);

        Assert.AreEqual(PerformanceModeEffectiveness.Unknown, result.State,
            "Unelevated: must not assert On/Off from the unreliable global preference alone.");
        Assert.AreEqual("Unknown", result.Headline);
        Assert.IsFalse(result.FromTrust);
        StringAssert.Contains(result.Reason, "admin");
    }

    // ---- Unelevated but a detected Dev Drive ⇒ report On/Off from the reliable global pref ----------

    [TestMethod]
    [DataRow(true, PerformanceModeEffectiveness.On)]
    [DataRow(false, PerformanceModeEffectiveness.Off)]
    [DataRow(null, PerformanceModeEffectiveness.Unknown)]
    public void Evaluate_UnelevatedDetectedDevDrive_FollowsGlobalPref(bool? global, PerformanceModeEffectiveness expected)
    {
        // Unelevated: the per-volume trust detail is unreadable, but we DID detect a Dev Drive (FSCTL works
        // unelevated) and the global pref is reliable + readable. So On/Off can be reported (no "Unknown").
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(trust: null, globalPerformanceModeOn: global, isDevDrive: true);

        Assert.AreEqual(expected, result.State);
        Assert.IsFalse(result.FromTrust, "Unelevated => not derived from the authoritative fsutil trust.");
        Assert.IsFalse(result.PolicyEnforced, "Policy enforcement is unknown unelevated.");
    }

    // ---- M4: unelevated, detected but UNTRUSTED Dev Drive ⇒ Off even when the global pref is on --------

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [DataRow(null)]
    public void Evaluate_UnelevatedDetectedButUntrusted_IsOff_RegardlessOfGlobalPref(bool? global)
    {
        // The authoritative elevated detail is unreadable (trust == null), but the unelevated trusted bit is
        // clear. Defender scans an untrusted Dev Drive synchronously, so performance mode must report Off even
        // when the global preference is on — never On (M4).
        EffectivePerformanceMode result = PerformanceModeEvaluator.Evaluate(
            trust: null, globalPerformanceModeOn: global, isDevDrive: true, unelevatedTrusted: false);

        Assert.AreEqual(PerformanceModeEffectiveness.Off, result.State,
            "An untrusted detected Dev Drive is scanned synchronously regardless of the global pref.");
        Assert.IsFalse(result.FromTrust, "Unelevated => not derived from the authoritative fsutil trust.");
        Assert.IsFalse(result.PolicyEnforced, "Policy enforcement is unknown unelevated.");
        StringAssert.Contains(result.Reason, "trusted Dev Drive");
    }

    // ---- Untrusted volume ⇒ Off (performance mode doesn't apply) ------------------------------------

    [TestMethod]
    public void Evaluate_Untrusted_IsOff()
    {
        var trust = new DevDriveTrustInfo { TrustState = DevDriveTrustState.Untrusted };

        EffectivePerformanceMode result = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true);

        Assert.AreEqual(PerformanceModeEffectiveness.Off, result.State,
            "An untrusted volume is scanned synchronously regardless of the global pref.");
        Assert.IsTrue(result.FromTrust);
        Assert.IsFalse(result.PolicyEnforced);
        StringAssert.Contains(result.Reason, "trusted Dev Drive");
        StringAssert.Contains(result.Display, "Off");
    }

    // ---- Trusted + policy-managed: still report On/Off from the reliable pref, flag PolicyEnforced ---

    [TestMethod]
    public void Evaluate_TrustedPolicyEnforced_GlobalOn_IsOnAndPolicyFlagged()
    {
        // THIS MACHINE's case: managed by group policy AND performance mode on. The reliable global pref
        // (1 = on) drives the verdict; PolicyEnforced is set so the UI can note "managed by your org"
        // without hiding the "On (async)" fact.
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(Trusted(policyEnforced: true), globalPerformanceModeOn: true);

        Assert.AreEqual(PerformanceModeEffectiveness.On, result.State);
        Assert.IsTrue(result.PolicyEnforced);
        Assert.IsTrue(result.FromTrust);
        StringAssert.Contains(result.Display, "On (async)");
    }

    [TestMethod]
    public void Evaluate_TrustedPolicyEnforced_GlobalOff_IsOffAndPolicyFlagged()
    {
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(Trusted(policyEnforced: true), globalPerformanceModeOn: false);

        Assert.AreEqual(PerformanceModeEffectiveness.Off, result.State);
        Assert.IsTrue(result.PolicyEnforced, "PolicyEnforced lets the UI suppress the local 'turn on' lever.");
    }

    [TestMethod]
    public void Evaluate_TrustedPolicyEnforced_GlobalUnknown_FallsBackToManaged()
    {
        // Only when the (otherwise reliable) pref is unreadable do we degrade to Managed rather than guess.
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(Trusted(policyEnforced: true), globalPerformanceModeOn: null);

        Assert.AreEqual(PerformanceModeEffectiveness.Managed, result.State);
        Assert.IsTrue(result.PolicyEnforced);
        Assert.IsTrue(result.FromTrust);
        Assert.AreEqual("Managed by your organization", result.Headline);
        Assert.AreEqual("managed by your organization", result.Display);
    }

    // ---- Trusted + not policy-managed ⇒ follow the global Defender pref -----------------------------

    [TestMethod]
    public void Evaluate_TrustedUnmanaged_GlobalOn_IsOn()
    {
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(Trusted(), globalPerformanceModeOn: true);

        Assert.AreEqual(PerformanceModeEffectiveness.On, result.State);
        Assert.IsTrue(result.FromTrust);
        Assert.IsFalse(result.PolicyEnforced);
        StringAssert.Contains(result.Reason, "asynchronous");
        StringAssert.Contains(result.Display, "On");
    }

    [TestMethod]
    public void Evaluate_TrustedUnmanaged_GlobalOff_IsOff()
    {
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(Trusted(), globalPerformanceModeOn: false);

        Assert.AreEqual(PerformanceModeEffectiveness.Off, result.State);
        Assert.IsTrue(result.FromTrust);
        StringAssert.Contains(result.Display, "Off");
    }

    [TestMethod]
    public void Evaluate_TrustedUnmanaged_GlobalUnknown_IsUnknown()
    {
        EffectivePerformanceMode result =
            PerformanceModeEvaluator.Evaluate(Trusted(), globalPerformanceModeOn: null);

        Assert.AreEqual(PerformanceModeEffectiveness.Unknown, result.State);
        Assert.IsTrue(result.FromTrust);
    }

    // ---- Attached AV minifilter alone does NOT force Off -------------------------------------------

    [TestMethod]
    public void Evaluate_TrustedUnmanaged_WdFilterAttached_GlobalOn_StillOn()
    {
        // WdFilter / MsSecFlt attach in BOTH async and sync modes. Their presence must NOT drive the
        // verdict — a trusted, unmanaged volume with the global pref on is On even with the filter attached.
        EffectivePerformanceMode result = PerformanceModeEvaluator.Evaluate(
            Trusted(attached: new[] { "WdFilter", "MsSecFlt" }),
            globalPerformanceModeOn: true);

        Assert.AreEqual(PerformanceModeEffectiveness.On, result.State,
            "An attached antivirus filter must not force Off.");
        StringAssert.Contains(result.Reason, "asynchronous");
    }

    [TestMethod]
    public void Evaluate_TrustedPolicyEnforced_WdFilterAttached_GlobalOff_IsOffNotForcedByFilter()
    {
        EffectivePerformanceMode result = PerformanceModeEvaluator.Evaluate(
            Trusted(policyEnforced: true, attached: new[] { "WdFilter" }),
            globalPerformanceModeOn: false);

        Assert.AreEqual(PerformanceModeEffectiveness.Off, result.State,
            "Filter presence is irrelevant; the reliable global pref (off) drives the verdict.");
        Assert.IsTrue(result.PolicyEnforced);
    }

    // ---- Indeterminate trust (had fsutil output but trust state unknown) ⇒ Unknown ----------------

    [TestMethod]
    public void Evaluate_TrustStateUnknownButTrustNotNull_IsUnknown()
    {
        var trust = new DevDriveTrustInfo { TrustState = DevDriveTrustState.Unknown };

        EffectivePerformanceMode result = PerformanceModeEvaluator.Evaluate(trust, globalPerformanceModeOn: true);

        Assert.AreEqual(PerformanceModeEffectiveness.Unknown, result.State);
        Assert.IsTrue(result.FromTrust);
    }
}
