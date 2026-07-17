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
        StringAssert.Contains(script, "Import-Module -Name $storageModule");
        StringAssert.Contains(script, "Storage\\Get-Partition");
        StringAssert.Contains(script, "DiskUniqueId=[string]$d.UniqueId");
        StringAssert.Contains(script, "PartitionOffsetBytes=[uint64]$p.Offset");
        StringAssert.Contains(script, "SupportsDevDriveFormat=$supportsDevDriveFormat");
        StringAssert.Contains(script, "Microsoft.Win32.Registry]::GetValue");
        StringAssert.Contains(script, "$updateBuildRevision -ge 2338");
        Assert.DoesNotContain("Resize-Partition", script);
        Assert.DoesNotContain("New-Partition", script);
        Assert.DoesNotContain("Storage\\Format-Volume -DriveLetter", script);
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
        StringAssert.Contains(script, "$currentPartitionNumber -ne 3");
        StringAssert.Contains(script, "$currentPartitionOffset -ne [uint64]1048576");
        StringAssert.Contains(script, "The source disk identity changed");
        StringAssert.Contains(script, "$updateBuildRevision -ge 2338");
        StringAssert.Contains(script, "$currentReclaimable -lt [uint64]107374182400");
        StringAssert.Contains(script, "Get-Volume -DriveLetter 'D' -ErrorAction SilentlyContinue");
        StringAssert.Contains(script, "Get-Volume -Partition $currentPartition");
        StringAssert.Contains(script, "Get-PartitionSupportedSize -InputObject $currentPartition");
        StringAssert.Contains(script, "Resize-Partition -InputObject $currentPartition");
        StringAssert.Contains(script, "New-Partition -InputObject $currentDisk -Size 107374182400");
        StringAssert.Contains(script, "Format-Volume -Partition $newPartition -DevDrive -FileSystem ReFS");
        StringAssert.Contains(script, "Get-Partition -UniqueId $newPartitionUniqueId");
        StringAssert.Contains(script, "Get-Volume -Partition $fp");
        StringAssert.Contains(script, "& $fsutil devdrv query 'D:'");
        StringAssert.Contains(script, "FinalDiskNumber=[int]$fp.DiskNumber");
        StringAssert.Contains(script, "IsDevDrive=$true");
        StringAssert.Contains(script, "Storage\\Resize-Partition");
        Assert.DoesNotContain("Resize-Partition -DriveLetter", script);
        Assert.DoesNotContain("Format-Volume -DriveLetter", script);
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

    [TestMethod]
    public void TryParseFinalState_RequiresMatchingVerifiedDevDrive()
    {
        ResizePlan plan = ApprovedPlan();
        const string valid =
            """{"FinalDriveLetter":"D","FinalPartitionSizeBytes":107374182400,"FinalFileSystem":"ReFS","FinalDiskNumber":7,"FinalPartitionNumber":4,"IsDevDrive":true}""";

        Assert.IsTrue(ResizePowerShellScript.TryParseFinalState(valid, plan, out ResizeFinalState state));
        Assert.AreEqual(7, state.FinalDiskNumber);
        Assert.IsFalse(ResizePowerShellScript.TryParseFinalState(
            valid.Replace("\"ReFS\"", "\"NTFS\"", StringComparison.Ordinal),
            plan,
            out _));
        Assert.IsFalse(ResizePowerShellScript.TryParseFinalState(
            valid.Replace("\"FinalDiskNumber\":7", "\"FinalDiskNumber\":8", StringComparison.Ordinal),
            plan,
            out _));
        Assert.IsFalse(ResizePowerShellScript.TryParseFinalState("not-json", plan, out _));
    }

    private static string BuildExecute(string label = "DevDrive") =>
        ResizePowerShellScript.BuildExecute(
            ApprovedPlan() with { Label = label },
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

    private static ResizePlan ApprovedPlan() => new()
    {
        SourceVolumeLetter = 'C',
        NewDriveLetter = 'D',
        ShrinkBytes = 100UL * Gib,
        Label = "DevDrive",
        ExpectedDiskNumber = 7,
        ExpectedDiskUniqueId = "NVME-DISK-7",
        ExpectedPartitionNumber = 3,
        ExpectedPartitionOffsetBytes = 1024UL * 1024UL,
        ExpectedPartitionGuid = "{11111111-2222-3333-4444-555555555555}",
        ExpectedAlignedShrinkBytes = 100UL * Gib,
    };
}
