using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Pure advisor for the Defender performance-mode nudge. Most of the Dev Drive's real-workload
/// advantage comes from Defender <b>performance mode</b> (asynchronous scanning of trusted Dev
/// Drives). When it is off, synchronous antivirus scanning mutes the gains, so a real-workload
/// comparison understates the Dev Drive. This helper decides when to nudge and supplies the
/// user-facing copy plus the exact, reversible, elevated command that turns the mode on.
/// </summary>
/// <remarks>
/// All members are pure/<c>static</c> so the nudge condition and copy are unit-testable without any
/// process, registry, or UI. Enabling the mode is a deliberate, user-confirmed, machine-wide setting
/// change — the app never applies it silently (see the page's confirm + elevation flow).
/// </remarks>
public static class PerformanceModeAdvisor
{
    /// <summary>The elevated, reversible PowerShell command that turns Defender performance mode on.</summary>
    public const string EnableCommand = "Set-MpPreference -PerformanceModeStatus Enabled";

    /// <summary>The reverse command, for completeness in guidance copy.</summary>
    public const string DisableCommand = "Set-MpPreference -PerformanceModeStatus Disabled";

    /// <summary>
    /// Deep link that opens Windows Security to the "Virus &amp; threat protection settings" page, where
    /// the Dev Drive protection toggle lives (per the Microsoft Defender performance-mode docs:
    /// "Virus &amp; threat protection settings &gt; Manage settings &gt; Dev Drive protection"). The
    /// <c>windowsdefender://</c> scheme reliably opens Windows Security; <c>threatsettings/</c> lands on
    /// that settings sub-page. This is the single swap-point if a more precise Dev Drive-protection deep
    /// link is ever published — change only this const.
    /// </summary>
    public const string WindowsSecurityDevDriveProtectionUri = "windowsdefender://threatsettings/";

    /// <summary>Honest caption shown when group policy (not the user) controls performance mode on this PC.</summary>
    public const string PolicyControlledNote =
        "Managed by your organization on this PC \u2014 opens Windows Security \u00B7 Dev Drive protection " +
        "(the authoritative per-volume status is under \u201CSee volumes\u201D)";

    /// <summary>Short nudge title for the global card and the inline caveat.</summary>
    public const string NudgeTitle = "Turn on Defender performance mode";

    /// <summary>Label for the actionable button.</summary>
    public const string EnableButtonText = "Turn on performance mode";

    /// <summary>
    /// Non-actionable, one-line caption for the DEMOTED perf-mode mentions (Workload card + Package-cache
    /// section). It states the fact (muted gains) and points to the single AUTHORITATIVE, actionable home
    /// — Drive health → Trust &amp; filters — without a second/third enable button. Consolidates the prompt
    /// to one place.
    /// </summary>
    public const string ManageInDriveHealthCaption =
        "Defender performance mode is off, muting the Dev Drive advantage \u2014 turn it on in Drive health \u203A Trust & filters.";

    /// <summary>
    /// Whether to show the performance-mode nudge: <c>true</c> ONLY when performance mode is explicitly
    /// OFF. A <c>null</c> (unknown / couldn't read) value does NOT nudge — the app never asserts a state
    /// it couldn't determine.
    /// </summary>
    public static bool ShouldNudge(PreflightInfo? preflight) => preflight?.DefenderPerformanceModeOn == false;

    /// <summary>The nudge body: why it matters and what the action does.</summary>
    public static string NudgeMessage =>
        "Most of the Dev Drive advantage comes from Defender performance mode (asynchronous scanning). " +
        "It's currently off, so synchronous antivirus scanning is muting the gains in this comparison. " +
        "Turning it on is a reversible, machine-wide setting that needs administrator approval.";

    /// <summary>The confirm-dialog body shown before the elevated change is applied.</summary>
    public static string ConfirmMessage =>
        "This turns on Microsoft Defender performance mode (asynchronous scanning for trusted Dev " +
        "Drives) for the whole machine. It is reversible and needs administrator approval (UAC).\n\n" +
        $"Command:  {EnableCommand}\n\n" +
        $"To undo later:  {DisableCommand}";

    /// <summary>Manual fallback shown when elevation isn't available or UAC is declined.</summary>
    public static string ManualGuidance =>
        "Performance mode wasn't changed. To turn it on yourself, run this in an elevated " +
        $"(Run as administrator) PowerShell, then re-run the test:\n\n{EnableCommand}";
}
