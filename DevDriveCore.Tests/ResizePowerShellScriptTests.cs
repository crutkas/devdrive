using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class ResizePowerShellScriptTests
{
    private const ulong Gib = 1024UL * 1024UL * 1024UL;

    [TestMethod]
    public void BuildSnapshot_UsesReadOnlyStorageQueries()
    {
        string script = ResizePowerShellScript.BuildSnapshot('c');

        StringAssert.Contains(script, "Get-Partition -DriveLetter 'C'");
        StringAssert.Contains(script, "Get-PartitionSupportedSize");
        Assert.DoesNotContain("Resize-Partition", script);
        Assert.DoesNotContain("New-Partition", script);
        Assert.DoesNotContain("Format-Volume", script);
    }

    [TestMethod]
    public void BuildExecute_RevalidatesLiveStateBeforeShrinking()
    {
        string script = BuildExecute();

        int supportedSizeQuery = script.IndexOf("Get-PartitionSupportedSize", StringComparison.Ordinal);
        int mutationMarker = script.IndexOf(ResizePowerShellScript.MutationStartedMarker, StringComparison.Ordinal);
        int resize = script.IndexOf("Resize-Partition", StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, supportedSizeQuery);
        Assert.IsGreaterThan(supportedSizeQuery, resize, "The live supported-size check must precede Resize-Partition.");
        Assert.IsGreaterThan(supportedSizeQuery, mutationMarker, "The mutation marker must follow every live preflight check.");
        Assert.IsGreaterThan(mutationMarker, resize, "The mutation marker must be flushed before Resize-Partition.");
        StringAssert.Contains(script, "$currentDiskNumber -ne 7");
        StringAssert.Contains(script, "$currentReclaimable -lt [uint64]107374182400");
        StringAssert.Contains(script, "Get-Volume -DriveLetter 'D' -ErrorAction SilentlyContinue");
        StringAssert.Contains(script, "New-Partition -DiskNumber $currentDiskNumber -Size 107374182400");
        StringAssert.Contains(script, "Format-Volume -DriveLetter 'D' -DevDrive -FileSystem ReFS");
    }

    [TestMethod]
    public void BuildExecute_SanitizesLabelBeforeInterpolation()
    {
        string script = BuildExecute("Dev';$(Get-Process)`");

        StringAssert.Contains(script, "-NewFileSystemLabel 'DevGet-Process'");
        Assert.DoesNotContain("';", script);
        Assert.DoesNotContain("$(", script);
        Assert.DoesNotContain("`", script);
    }

    [TestMethod]
    public void SanitizeLabel_DefaultsAndCapsLength()
    {
        Assert.AreEqual("DevDrive", ResizePowerShellScript.SanitizeLabel("';$()"));
        Assert.HasCount(32, ResizePowerShellScript.SanitizeLabel(new string('A', 40)));
    }

    [TestMethod]
    public void MutationMayHaveStarted_DistinguishesPreflightFromMutationFailures()
    {
        Assert.IsFalse(ResizePowerShellScript.MutationMayHaveStarted(1, "preflight failed"));
        Assert.IsTrue(ResizePowerShellScript.MutationMayHaveStarted(
            1,
            $"{ResizePowerShellScript.MutationStartedMarker}\r\nresize failed"));
        Assert.IsTrue(ResizePowerShellScript.MutationMayHaveStarted(-1, "process state unavailable"));
        Assert.AreEqual(
            "resize failed",
            ResizePowerShellScript.RemoveMutationMarker(
                $"{ResizePowerShellScript.MutationStartedMarker}\r\nresize failed"));
    }

    private static string BuildExecute(string label = "DevDrive") =>
        ResizePowerShellScript.BuildExecute(
            new ResizePlan
            {
                SourceVolumeLetter = 'C',
                NewDriveLetter = 'D',
                ShrinkBytes = 100UL * Gib,
                Label = label,
            },
            new ResizeFeasibility
            {
                CanProceed = true,
                SourceVolumeLetter = 'C',
                NewDriveLetter = 'D',
                AlignedShrinkBytes = 100UL * Gib,
                SourceSizeBytesBefore = 500UL * Gib,
                SourceSizeBytesAfter = 400UL * Gib,
            },
            expectedDiskNumber: 7);
}
