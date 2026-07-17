using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class VhdPowerShellScriptTests
{
    private const ulong Size = 64UL * 1024 * 1024 * 1024;

    private static VhdProvisionPlan Plan() => new()
    {
        FilePath = @"C:\Dev Drives\O'Brien.vhdx",
        MaximumSizeBytes = Size + DevDriveSizeMath.VhdContainerHeadroomBytesExact,
        VolumeSizeBytes = Size,
        DynamicallyExpanding = true,
        DriveLetter = 'V',
        Label = "Dev Drive!",
        ExecuteAuthorized = true,
    };

    [TestMethod]
    public void BuildFinalize_BindsEveryMutationToExactNewVhd()
    {
        string script = VhdPowerShellScript.BuildFinalize(Plan(), 7);

        StringAssert.Contains(script, @"$imagePath='C:\Dev Drives\O''Brien.vhdx'");
        StringAssert.Contains(script, "Storage\\Get-DiskImage -ImagePath $imagePath");
        StringAssert.Contains(script, "$image | Storage\\Get-Disk");
        StringAssert.Contains(script, "[int]$disk.Number -ne $expectedDiskNumber");
        StringAssert.Contains(script, "[string]$disk.PartitionStyle -ne 'RAW'");
        StringAssert.Contains(script, "Get-Partition -DiskNumber $expectedDiskNumber");
        StringAssert.Contains(script, "$sizeDelta -gt [uint64]1048576");
        StringAssert.Contains(script, "$partitionSizeDelta -gt [uint64]1048576");
        StringAssert.Contains(script, "Get-Volume -DriveLetter $target");
    }

    [TestMethod]
    public void BuildFinalize_InitializesPartitionsFormatsAndVerifiesInOrder()
    {
        string script = VhdPowerShellScript.BuildFinalize(Plan(), 7);

        int initialize = script.IndexOf("Initialize-Disk", StringComparison.Ordinal);
        int partition = script.IndexOf("New-Partition", StringComparison.Ordinal);
        int format = script.IndexOf("Storage\\Format-Volume -DriveLetter", StringComparison.Ordinal);
        int verify = script.IndexOf("& $fsutil devdrv query", StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, initialize);
        Assert.IsGreaterThan(initialize, partition);
        Assert.IsGreaterThan(partition, format);
        Assert.IsGreaterThan(format, verify);
        StringAssert.Contains(script, "-Size $volumeSize -DriveLetter $target");
        StringAssert.Contains(script, "-DevDrive -FileSystem ReFS");
        StringAssert.Contains(script, "Import-Module -Name $storageModule");
        StringAssert.Contains(script, "[Environment]::SystemDirectory");
        StringAssert.Contains(script, VhdPowerShellScript.MutationStartedMarker);
        Assert.IsGreaterThan(
            script.IndexOf("Parameters.ContainsKey('DevDrive')", StringComparison.Ordinal),
            script.IndexOf(VhdPowerShellScript.MutationStartedMarker, StringComparison.Ordinal),
            "Capability checks must complete before the mutation marker.");
    }

    [TestMethod]
    public void BuildFinalize_SanitizesLabel()
    {
        string script = VhdPowerShellScript.BuildFinalize(Plan(), 7);

        StringAssert.Contains(script, "-NewFileSystemLabel 'Dev Drive'");
        Assert.IsFalse(script.Contains("Dev Drive!", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildFinalize_RejectsUnsafeLetterAndUndersizedDisk()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => VhdPowerShellScript.BuildFinalize(Plan() with { DriveLetter = 'C' }, 7));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => VhdPowerShellScript.BuildFinalize(
                Plan() with { VolumeSizeBytes = DevDriveSizeMath.MinimumSizeBytesExact - 1 },
                7));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => VhdPowerShellScript.BuildFinalize(
                Plan() with { MaximumSizeBytes = Size },
                7));
    }

    [TestMethod]
    public void TryParseFinalState_RequiresVerifiedRefsDevDrive()
    {
        Assert.IsTrue(VhdPowerShellScript.TryParseFinalState(
            """{"DriveLetter":"V","SizeBytes":68719476736,"FileSystem":"ReFS","IsDevDrive":true}""",
            out VhdFinalState state));
        Assert.AreEqual('V', state.DriveLetter);
        Assert.AreEqual(Size, state.SizeBytes);

        Assert.IsFalse(VhdPowerShellScript.TryParseFinalState(
            """{"DriveLetter":"V","SizeBytes":68719476736,"FileSystem":"NTFS","IsDevDrive":true}""",
            out _));
        Assert.IsFalse(VhdPowerShellScript.TryParseFinalState("not-json", out _));
    }

    [TestMethod]
    public void MutationMarker_ClassifiesAndCleansError()
    {
        string stderr = $"{VhdPowerShellScript.MutationStartedMarker}{Environment.NewLine}format failed";

        Assert.IsTrue(VhdPowerShellScript.MutationMayHaveStarted(1, stderr));
        Assert.AreEqual("format failed", VhdPowerShellScript.RemoveMutationMarker(stderr));
        Assert.IsFalse(VhdPowerShellScript.MutationMayHaveStarted(1, "preflight failed"));
    }

    [TestMethod]
    public void CapabilityProbe_RequiresSupportedBuildAndDevDriveParameter()
    {
        string script = VhdPowerShellScript.BuildCapabilityProbe();

        StringAssert.Contains(script, "22621");
        StringAssert.Contains(script, "2338");
        StringAssert.Contains(script, "Microsoft.Win32.Registry]::GetValue");
        StringAssert.Contains(script, "Revision=$updateBuildRevision");
        StringAssert.Contains(script, "Parameters.ContainsKey('DevDrive')");
        StringAssert.Contains(script, "Supported=$supported");

        Assert.IsTrue(VhdPowerShellScript.TryParseCapability(
            """{"Supported":true,"Build":26100,"Revision":1,"HasDevDriveParameter":true}""",
            out VhdCapabilityState state));
        Assert.IsTrue(state.Supported);
        Assert.IsFalse(VhdPowerShellScript.TryParseCapability("not-json", out _));
    }
}
