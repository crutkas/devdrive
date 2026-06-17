using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Pure evaluator for the <em>effective</em> Defender performance mode on a Dev Drive volume.
/// </summary>
/// <remarks>
/// <para>There is <b>no documented programmatic per-volume async/sync signal</b> (the authoritative
/// per-volume status lives only in Windows Security &#8250; Dev Drive protection &#8250; "See volumes").
/// So this evaluator derives an honest verdict from the signals that <em>are</em> trustworthy:</para>
/// <list type="bullet">
///   <item><b>Untrusted volume</b> ⇒ performance mode is not in effect (Defender scans it synchronously
///   like any normal volume) ⇒ <see cref="PerformanceModeEffectiveness.Off"/>.</item>
///   <item><b>Trusted (or, unelevated, a detected Dev Drive)</b> ⇒ the global
///   <c>Get-MpPreference PerformanceModeStatus</c> applies. It is <b>reliable</b> (1 = Enabled / On /
///   asynchronous, 0 = Disabled / Off / synchronous — verified against Windows Security "See volumes")
///   and readable unelevated, so it drives On/Off.</item>
///   <item><b>Trusted + antivirus protection enforced by group policy</b>
///   (<see cref="DevDriveTrustInfo.AntivirusPolicyEnforced"/>, from the fsutil "by group policy"
///   sentinel) ⇒ the same On/Off verdict still applies, plus <see cref="EffectivePerformanceMode.PolicyEnforced"/>
///   is set so the UI can add "managed by your organization" and suppress the local "turn on" lever. Only
///   when the preference is <em>unreadable</em> do we fall back to
///   <see cref="PerformanceModeEffectiveness.Managed"/> rather than guess.</item>
///   <item><b>Unelevated, not a detected Dev Drive, preference unknown</b> ⇒
///   <see cref="PerformanceModeEffectiveness.Unknown"/>.</item>
/// </list>
/// <para><b>Antivirus minifilters (WdFilter / MsSecFlt) are deliberately ignored</b> as a scan-mode
/// signal: they attach to a Dev Drive in BOTH asynchronous and synchronous modes, so their presence
/// says nothing about whether performance mode is on.</para>
/// <para>All members are pure/<c>static</c> so the derivation is unit-testable without any process or UI.</para>
/// </remarks>
public static class PerformanceModeEvaluator
{
    /// <summary>
    /// Derives the effective performance mode for a volume from its (optional, elevation-dependent)
    /// trust/policy detail and the global Defender preference.
    /// </summary>
    /// <param name="trust">
    /// The volume's parsed <c>fsutil devdrv query</c> detail, or <c>null</c> when it couldn't be read
    /// (almost always because the process is unelevated).
    /// </param>
    /// <param name="globalPerformanceModeOn">
    /// The global <c>Get-MpPreference PerformanceModeStatus</c> reading (<c>true</c> = on, <c>false</c>
    /// = off, <c>null</c> = unknown). This is reliable and is consulted for any trusted volume — or, when
    /// <paramref name="trust"/> is null, for a detected Dev Drive (see <paramref name="isDevDrive"/>).
    /// </param>
    /// <param name="isDevDrive">
    /// True when the caller knows this volume is a detected Dev Drive (FSCTL detection works unelevated).
    /// Lets the unelevated path report On/Off from the reliable global preference instead of "Unknown".
    /// </param>
    /// <param name="unelevatedTrusted">
    /// The unelevated <c>PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME</c> bit (<see cref="VolumeInfo.IsTrusted"/>),
    /// readable via the same FSCTL as detection. Only consulted when <paramref name="trust"/> is null (i.e.
    /// the authoritative elevated detail is unavailable): a detected Dev Drive whose trusted bit is clear is
    /// scanned synchronously, so performance mode is reported Off regardless of the global preference. Defaults
    /// to <c>true</c> so callers that don't supply it keep the prior behaviour.
    /// </param>
    public static EffectivePerformanceMode Evaluate(
        DevDriveTrustInfo? trust,
        bool? globalPerformanceModeOn,
        bool isDevDrive = false,
        bool unelevatedTrusted = true)
    {
        // Untrusted volume: performance mode (asynchronous scanning) does not apply — Defender scans it
        // synchronously like any normal volume, regardless of the global preference.
        if (trust is { TrustState: DevDriveTrustState.Untrusted })
        {
            return new EffectivePerformanceMode
            {
                State = PerformanceModeEffectiveness.Off,
                Reason = "this volume isn't a trusted Dev Drive",
                FromTrust = true,
            };
        }

        // Unelevated untrusted detection: when the authoritative elevated detail is unavailable, fall back to
        // the unelevated trusted bit. A detected Dev Drive whose trusted bit is clear is scanned synchronously,
        // so performance mode is Off even when the global preference is on (M4: an untrusted detected Dev Drive
        // must never be reported "On").
        if (trust is null && isDevDrive && !unelevatedTrusted)
        {
            return new EffectivePerformanceMode
            {
                State = PerformanceModeEffectiveness.Off,
                Reason = "this volume isn't a trusted Dev Drive",
                FromTrust = false,
            };
        }

        bool trusted = trust is { TrustState: DevDriveTrustState.Trusted };
        bool policyEnforced = trust?.AntivirusPolicyEnforced ?? false;

        // We can speak to On/Off only when the volume is trusted (elevated, authoritative) OR it's a
        // detected Dev Drive whose per-volume trust we couldn't read but whose scan mode the reliable
        // global preference reflects. Otherwise (e.g. unelevated + not a Dev Drive, or an indeterminate
        // fsutil trust state) we don't guess.
        if (!trusted && !isDevDrive)
        {
            return new EffectivePerformanceMode
            {
                State = PerformanceModeEffectiveness.Unknown,
                Reason = trust is null ? "run as admin to confirm" : "trust state couldn't be determined",
                FromTrust = trust is not null,
            };
        }

        // From here, the (reliable) global Defender preference drives the verdict. When it's genuinely
        // unreadable we degrade honestly: to Managed if policy-enforced, else Unknown.
        return globalPerformanceModeOn switch
        {
            true => new EffectivePerformanceMode
            {
                State = PerformanceModeEffectiveness.On,
                Reason = "scanning asynchronously",
                PolicyEnforced = policyEnforced,
                FromTrust = trusted,
            },
            false => new EffectivePerformanceMode
            {
                State = PerformanceModeEffectiveness.Off,
                Reason = "performance mode is turned off in Defender settings",
                PolicyEnforced = policyEnforced,
                FromTrust = trusted,
            },
            _ => policyEnforced
                ? new EffectivePerformanceMode
                {
                    State = PerformanceModeEffectiveness.Managed,
                    Reason = "managed by your organization",
                    PolicyEnforced = true,
                    FromTrust = trusted,
                }
                : new EffectivePerformanceMode
                {
                    State = PerformanceModeEffectiveness.Unknown,
                    Reason = trusted ? "couldn't read the Defender preference" : "run as admin to confirm",
                    FromTrust = trusted,
                },
        };
    }
}
