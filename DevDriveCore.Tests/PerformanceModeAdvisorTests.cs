using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the pure <see cref="PerformanceModeAdvisor"/> nudge helper. The nudge appears ONLY when
/// Defender performance mode is explicitly off — never when it is on or its state is unknown — and the
/// user-facing copy always carries the exact, reversible command.
/// </summary>
[TestClass]
public sealed class PerformanceModeAdvisorTests
{
    [TestMethod]
    public void ShouldNudge_PerformanceModeOff_IsTrue()
    {
        var preflight = new PreflightInfo { DefenderPerformanceModeOn = false };

        Assert.IsTrue(PerformanceModeAdvisor.ShouldNudge(preflight));
    }

    [TestMethod]
    public void ShouldNudge_PerformanceModeOn_IsFalse()
    {
        var preflight = new PreflightInfo { DefenderPerformanceModeOn = true };

        Assert.IsFalse(PerformanceModeAdvisor.ShouldNudge(preflight));
    }

    [TestMethod]
    public void ShouldNudge_PerformanceModeUnknown_IsFalse()
    {
        // null = couldn't read the state (e.g. no elevation): the app never asserts a state it doesn't know.
        var preflight = new PreflightInfo { DefenderPerformanceModeOn = null };

        Assert.IsFalse(PerformanceModeAdvisor.ShouldNudge(preflight));
    }

    [TestMethod]
    public void ShouldNudge_NullPreflight_IsFalse()
    {
        Assert.IsFalse(PerformanceModeAdvisor.ShouldNudge(null));
    }

    [TestMethod]
    public void Commands_ToggleDefenderPerformanceMode()
    {
        // Pin the exact, security-sensitive elevated commands — a typo here would be a real bug. Locals
        // keep the analyzer from const-folding the assertion (the fields are const).
        string enable = PerformanceModeAdvisor.EnableCommand;
        string disable = PerformanceModeAdvisor.DisableCommand;

        StringAssert.StartsWith(enable, "Set-MpPreference -PerformanceModeStatus ");
        StringAssert.EndsWith(enable, "Enabled");
        StringAssert.StartsWith(disable, "Set-MpPreference -PerformanceModeStatus ");
        StringAssert.EndsWith(disable, "Disabled");
    }

    [TestMethod]
    public void Copy_IsPopulated_AndCarriesTheExactCommands()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(PerformanceModeAdvisor.NudgeTitle));
        Assert.IsFalse(string.IsNullOrWhiteSpace(PerformanceModeAdvisor.EnableButtonText));
        Assert.IsFalse(string.IsNullOrWhiteSpace(PerformanceModeAdvisor.NudgeMessage));

        // The confirm + manual-guidance copy must show the user exactly what will run / what to run.
        StringAssert.Contains(PerformanceModeAdvisor.ConfirmMessage, PerformanceModeAdvisor.EnableCommand);
        StringAssert.Contains(PerformanceModeAdvisor.ConfirmMessage, PerformanceModeAdvisor.DisableCommand);
        StringAssert.Contains(PerformanceModeAdvisor.ManualGuidance, PerformanceModeAdvisor.EnableCommand);
    }
}
