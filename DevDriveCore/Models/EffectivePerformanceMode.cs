namespace DevDriveCore.Models;

/// <summary>Whether Defender performance mode is <em>effective on a specific Dev Drive volume</em>.</summary>
public enum PerformanceModeEffectiveness
{
    /// <summary>Couldn't be determined (e.g. unelevated, so the per-volume trust detail is unreadable).</summary>
    Unknown,

    /// <summary>Performance mode is on for this volume — the Dev Drive is scanned asynchronously.</summary>
    On,

    /// <summary>Performance mode is off for this volume — the Dev Drive is scanned synchronously.</summary>
    Off,

    /// <summary>
    /// Asynchronous scanning for trusted Dev Drives is <em>managed by group policy</em> on this PC AND
    /// the global Defender preference couldn't be read, so we report it as managed rather than guessing
    /// On/Off. (When the preference IS readable it is reliable and drives the On/Off verdict even on a
    /// policy-managed PC.) The authoritative per-volume status is Windows Security &#8250; Dev Drive
    /// protection &#8250; See volumes.
    /// </summary>
    Managed,
}

/// <summary>
/// The <em>effective</em> Defender performance mode for one Dev Drive volume, derived by
/// <see cref="DevDriveCore.Services.PerformanceModeEvaluator"/>. Pure data.
/// </summary>
/// <remarks>
/// <para>There is no documented programmatic per-volume async/sync signal. The verdict is therefore
/// derived from the volume's trust state (from <c>fsutil devdrv query</c>, elevation-only, when
/// available) plus the global <c>Get-MpPreference PerformanceModeStatus</c> — which IS reliable
/// (1 = on/async, 0 = off/sync; it agrees with Windows Security) and readable unelevated. When the
/// trust detail is enforced by group policy we additionally flag <see cref="PolicyEnforced"/> so the
/// UI can note "managed by your organization" without hiding the On/Off fact.</para>
/// <para>Antivirus minifilters (WdFilter / MsSecFlt) attach to a Dev Drive in BOTH asynchronous and
/// synchronous modes, so filter presence says nothing about the scan mode and is deliberately NOT used
/// here. The authoritative per-volume source is Windows Security &#8250; Dev Drive protection &#8250;
/// "See volumes".</para>
/// </remarks>
public sealed record EffectivePerformanceMode
{
    /// <summary>The effective state on this volume.</summary>
    public PerformanceModeEffectiveness State { get; init; } = PerformanceModeEffectiveness.Unknown;

    /// <summary>Short, honest reason, e.g. "scanning asynchronously" or "this volume isn't a trusted Dev Drive".</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>True when asynchronous scanning is enforced by group policy on this PC. May be set
    /// alongside any On/Off/Managed state — the UI uses it to add a "managed by your organization" note
    /// and to suppress the local "turn on" lever (the user can't flip a policy-managed setting).</summary>
    public bool PolicyEnforced { get; init; }

    /// <summary>
    /// True when this verdict was derived from the elevated <c>fsutil devdrv query</c> trust detail
    /// (authoritative for trust state and policy enforcement); false when the trust detail was unreadable.
    /// </summary>
    public bool FromTrust { get; init; }

    /// <summary>Short headline: "On (async)" / "Off (sync)" / "Managed by your organization" / "Unknown".</summary>
    public string Headline => State switch
    {
        PerformanceModeEffectiveness.On => "On (async)",
        PerformanceModeEffectiveness.Off => "Off (sync)",
        PerformanceModeEffectiveness.Managed => "Managed by your organization",
        _ => "Unknown",
    };

    /// <summary>
    /// Honest one-line description for the Drive-health / Trust &amp; filters perf-mode line, e.g.
    /// "On (async)", "Off (sync)", "managed by your organization", or "Unknown \u2014 run as admin to
    /// confirm".
    /// </summary>
    public string Display => State switch
    {
        PerformanceModeEffectiveness.On => "On (async)",
        PerformanceModeEffectiveness.Off => "Off (sync)",
        // Managed reads as a complete sentence fragment on its own ("Performance mode: managed by your
        // organization"); never prefix it with "Managed \u2014 ...".
        PerformanceModeEffectiveness.Managed => "managed by your organization",
        _ => string.IsNullOrEmpty(Reason) ? Headline : $"{Headline} \u2014 {Reason}",
    };
}
