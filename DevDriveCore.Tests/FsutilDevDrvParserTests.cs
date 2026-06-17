using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class FsutilDevDrvParserTests
{
    [TestMethod]
    public void Parse_TrustedFixture_ReportsTrustedPerfModeAndAttachedFilters()
    {
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(0, TestData.FsutilTrusted, string.Empty);

        Assert.IsNotNull(info);
        Assert.AreEqual(DevDriveTrustState.Trusted, info.TrustState);
        Assert.IsTrue(info.PerformanceModeOn, "Antivirus filter not allowed => performance mode on.");
        CollectionAssert.AreEqual(
            new[] { "FileInfo", "Wof", "WdFilter" },
            info.AttachedFilters.ToArray());
        Assert.HasCount(7, info.AllowedFilters);
        CollectionAssert.Contains(info.AllowedFilters.ToArray(), "PrjFlt");
    }

    [TestMethod]
    public void Parse_UntrustedFixture_ReportsUntrustedAndPerfModeOff()
    {
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(0, TestData.FsutilUntrusted, string.Empty);

        Assert.IsNotNull(info);
        Assert.AreEqual(DevDriveTrustState.Untrusted, info.TrustState);
        Assert.IsFalse(info.PerformanceModeOn, "Antivirus filter allowed => performance mode off.");
        Assert.HasCount(5, info.AttachedFilters);
        CollectionAssert.Contains(info.AttachedFilters.ToArray(), "luafv");
    }

    [TestMethod]
    public void Parse_SentenceForm_DetectsTrustedAndFilters()
    {
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(0, TestData.FsutilSentenceForm, string.Empty);

        Assert.IsNotNull(info);
        Assert.AreEqual(DevDriveTrustState.Trusted, info.TrustState);
        CollectionAssert.AreEqual(new[] { "FileInfo", "Wof" }, info.AttachedFilters.ToArray());
    }

    [TestMethod]
    public void Parse_AccessDenied_ReturnsNull()
    {
        // The real unelevated output. Must NOT throw, must signal "undeterminable".
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(1, TestData.FsutilAccessDenied, string.Empty);

        Assert.IsNull(info);
    }

    [TestMethod]
    public void Parse_AccessDeniedOnStdErr_ReturnsNull()
    {
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(1, string.Empty, TestData.FsutilAccessDenied);

        Assert.IsNull(info);
    }

    [TestMethod]
    public void Parse_EmptyOutputWithFailure_ReturnsNull()
    {
        Assert.IsNull(FsutilDevDrvParser.Parse(-1, string.Empty, string.Empty));
    }

    [TestMethod]
    public void Parse_NotADevDrive_ReturnsUnknownTrustNotNull()
    {
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(0, TestData.FsutilNotDevDrive, string.Empty);

        Assert.IsNotNull(info);
        Assert.AreEqual(DevDriveTrustState.Unknown, info.TrustState);
        Assert.IsEmpty(info.AttachedFilters);
    }

    [TestMethod]
    public void Parse_GroupPolicyEnforced_FlagsPolicyEnforcedAndPerfModeOff()
    {
        DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(0, TestData.FsutilPolicyEnforced, string.Empty);

        Assert.IsNotNull(info);
        Assert.IsTrue(info.AntivirusPolicyEnforced, "The 'by group policy' phrase must flag policy enforcement.");
        Assert.IsFalse(info.PerformanceModeOn, "AV filter allowed (by policy) => performance mode is off.");
        Assert.AreEqual(DevDriveTrustState.Trusted, info.TrustState);
        CollectionAssert.Contains(info.AttachedFilters.ToArray(), "MsSecFlt");
    }

    [TestMethod]
    public void Parse_NoGroupPolicy_DoesNotFlagPolicyEnforced()
    {
        // The trusted fixture (no "group policy" phrase) must leave the flag false so the UI offers the
        // local one-click enable instead of the org-controlled deep link.
        DevDriveTrustInfo? trusted = FsutilDevDrvParser.Parse(0, TestData.FsutilTrusted, string.Empty);
        DevDriveTrustInfo? untrusted = FsutilDevDrvParser.Parse(0, TestData.FsutilUntrusted, string.Empty);

        Assert.IsNotNull(trusted);
        Assert.IsNotNull(untrusted);
        Assert.IsFalse(trusted.AntivirusPolicyEnforced);
        Assert.IsFalse(untrusted.AntivirusPolicyEnforced);
    }

    [TestMethod]
    public void IndicatesAccessDenied_MatchesRealMessage()
    {
        Assert.IsTrue(FsutilDevDrvParser.IndicatesAccessDenied(TestData.FsutilAccessDenied));
        Assert.IsFalse(FsutilDevDrvParser.IndicatesAccessDenied(TestData.FsutilTrusted));
    }
}
