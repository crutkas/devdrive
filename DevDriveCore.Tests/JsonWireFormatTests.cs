using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Pins the exact JSON this library writes and the JSON it accepts, through the public and internal
/// call sites rather than through a serializer. Two of these shapes are files already sitting on
/// users' machines (the free-space history and the reversibility journal), and the rest cross a UAC
/// boundary to the elevated helper or come back from a PowerShell script. Any drift in naming,
/// casing, indentation, escaping or number handling breaks one side of those contracts silently.
/// </summary>
/// <remarks>
/// The literals were captured from the reflection-based serializer these call sites used before they
/// moved to source-generated contexts, so a pass here means the move changed nothing on the wire.
/// </remarks>
[TestClass]
public sealed class JsonWireFormatTests
{
    private const string VhdPath = @"C:\DevDrives\Dev.vhdx";
    private const ulong Gib = 1024UL * 1024UL * 1024UL;

    // ---- Free-space history file ----------------------------------------------------------------

    [TestMethod]
    public void FreeSpaceHistory_WritesCompactPascalCaseIncludingComputedUsedBytes()
    {
        string path = TempPath("history");
        try
        {
            var store = new JsonFreeSpaceHistoryStore(path);
            store.Append(Sample(hour: 3, free: 400));
            store.Append(Sample(hour: 4, free: 390));

            const string expected =
                """[{"TakenAtUtc":"2026-01-02T03:04:05+00:00","VolumeId":"C:","TotalBytes":1000,"FreeBytes":400,"UsedBytes":600},""" +
                """{"TakenAtUtc":"2026-01-02T04:04:05+00:00","VolumeId":"C:","TotalBytes":1000,"FreeBytes":390,"UsedBytes":610}]""";
            Assert.AreEqual(expected, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FreeSpaceHistory_ReadsAnExistingFileWrittenByEarlierBuilds()
    {
        string path = TempPath("history");
        try
        {
            File.WriteAllText(
                path,
                """[{"TakenAtUtc":"2026-01-02T04:04:05+00:00","VolumeId":"G:","TotalBytes":2000,"FreeBytes":500,"UsedBytes":1},""" +
                """{"TakenAtUtc":"2026-01-02T03:04:05Z","VolumeId":"C:","TotalBytes":1000,"FreeBytes":400,"Extra":true}]""");

            IReadOnlyList<FreeSpaceSample> samples = new JsonFreeSpaceHistoryStore(path).Load();

            Assert.HasCount(2, samples);
            Assert.AreEqual(Sample(hour: 3, free: 400), samples[0]);
            Assert.AreEqual(
                new FreeSpaceSample
                {
                    TakenAtUtc = new DateTimeOffset(2026, 1, 2, 4, 4, 5, TimeSpan.Zero),
                    VolumeId = "G:",
                    TotalBytes = 2000,
                    FreeBytes = 500,
                },
                samples[1]);

            // UsedBytes on disk is ignored on the way in; it is always recomputed.
            Assert.AreEqual(1500, samples[1].UsedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FreeSpaceHistory_PropertyNamesAreCaseSensitiveOnRead()
    {
        // camelCase never bound, so such a row has no volume and is dropped. Pinned so a resolver
        // change cannot quietly start accepting a different shape.
        string path = TempPath("history");
        try
        {
            File.WriteAllText(
                path,
                """[{"takenAtUtc":"2026-01-02T03:04:05Z","volumeId":"C:","totalBytes":1000,"freeBytes":400}]""");

            Assert.HasCount(0, new JsonFreeSpaceHistoryStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Reversibility journal ------------------------------------------------------------------

    [TestMethod]
    public void Reversibility_WritesIndentedPascalCaseWithEscapedPaths()
    {
        string path = TempPath("reversibility");
        try
        {
            var store = new JsonFileReversibilityStore(path);
            store.Save(new ReversibilityEntry
            {
                Id = "package-cache:npm_config_cache",
                Kind = ReversibilityKinds.PackageCacheMove,
                TimestampUtc = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
                ToolName = "npm",
                EnvironmentVariable = "npm_config_cache",
                EnvironmentValueWasSet = true,
                PriorEnvironmentValue = null,
                SourcePath = @"C:\Users\dev\AppData\Local\npm-cache",
                TargetPath = @"G:\packages\npm",
                SourceDeleted = true,
            });

            string expected = string.Join(
                Environment.NewLine,
                "[",
                "  {",
                "    \"Id\": \"package-cache:npm_config_cache\",",
                "    \"Kind\": \"PackageCacheMove\",",
                "    \"TimestampUtc\": \"2024-01-02T03:04:05+00:00\",",
                "    \"ToolName\": \"npm\",",
                "    \"EnvironmentVariable\": \"npm_config_cache\",",
                "    \"EnvironmentValueWasSet\": true,",
                "    \"PriorEnvironmentValue\": null,",
                "    \"SourcePath\": \"C:\\\\Users\\\\dev\\\\AppData\\\\Local\\\\npm-cache\",",
                "    \"TargetPath\": \"G:\\\\packages\\\\npm\",",
                "    \"SourceDeleted\": true",
                "  }",
                "]");
            Assert.AreEqual(expected, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Reversibility_ReadsAnExistingJournalWrittenByEarlierBuilds()
    {
        string path = TempPath("reversibility");
        try
        {
            File.WriteAllText(
                path,
                """
                [
                  {
                    "Id": "vhd:c:\\devdrives\\dev.vhdx",
                    "Kind": "VhdProvision",
                    "TimestampUtc": "2024-01-02T03:04:05+00:00",
                    "ToolName": null,
                    "TargetPath": "C:\\DevDrives\\Dev.vhdx",
                    "Unknown": [1, 2, 3]
                  },
                  {
                    "id": "camel-case-is-not-bound",
                    "kind": "PackageCacheMove"
                  }
                ]
                """);

            IReadOnlyList<ReversibilityEntry> entries = new JsonFileReversibilityStore(path).GetAll();

            Assert.HasCount(1, entries);
            Assert.AreEqual(
                new ReversibilityEntry
                {
                    Id = @"vhd:c:\devdrives\dev.vhdx",
                    Kind = ReversibilityKinds.VhdProvision,
                    TimestampUtc = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
                    TargetPath = VhdPath,
                },
                entries[0]);
            Assert.IsTrue(File.Exists(path), "a readable journal must not be quarantined");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Resize helper wire format --------------------------------------------------------------

    [TestMethod]
    public async Task ResizePlan_IsSentAsCompactPascalCase()
    {
        var broker = Substitute.For<IElevatedResizeBroker>();
        ResizeBrokerRequest? observed = null;
        broker
            .InvokeAsync(Arg.Any<ResizeBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<ResizeBrokerRequest>();
                return Task.FromResult<string?>(null);
            });

        await new VolumeResizer(broker).PreviewAsync(new ResizePlan
        {
            SourceVolumeLetter = 'C',
            ShrinkBytes = 100UL * Gib,
            NewDriveLetter = 'D',
            Label = "My \"Dev\" <Drive> & more",
            ExpectedDiskNumber = 7,
            ExpectedPartitionOffsetBytes = 1024UL * 1024UL,
        });

        Assert.IsNotNull(observed);
        Assert.AreEqual(
            """{"SourceVolumeLetter":"C","ShrinkBytes":107374182400,"NewDriveLetter":"D","Label":"My \u0022Dev\u0022 \u003CDrive\u003E \u0026 more","ExpectedDiskNumber":7,"ExpectedDiskUniqueId":"","ExpectedPartitionNumber":null,"ExpectedPartitionOffsetBytes":1048576,"ExpectedPartitionGuid":"","ExpectedAlignedShrinkBytes":null,"ExecuteAuthorized":false}""",
            observed.PlanJson);
    }

    [TestMethod]
    public async Task ResizeFeasibility_IsReadCaseInsensitivelyAndKeepsDefaultsForMissingMembers()
    {
        var broker = Substitute.For<IElevatedResizeBroker>();
        broker
            .InvokeAsync(Arg.Any<ResizeBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(
                """{"canProceed":false,"REASON":"too small","sourceVolumeLetter":"C","newDriveLetter":"D","alignedShrinkBytes":5,"steps":["a","b"],"unknownMember":{"x":1}}"""));

        ResizeFeasibility? result = await new VolumeResizer(broker).PreviewAsync(new ResizePlan());

        Assert.IsNotNull(result);
        Assert.IsFalse(result.CanProceed);
        Assert.AreEqual("too small", result.Reason);
        Assert.AreEqual('C', result.SourceVolumeLetter);
        Assert.AreEqual('D', result.NewDriveLetter);
        Assert.AreEqual(5UL, result.AlignedShrinkBytes);
        CollectionAssert.AreEqual(new[] { "a", "b" }, result.Steps.ToArray());
        Assert.AreEqual(string.Empty, result.DiskUniqueId);
        Assert.IsTrue(result.IsReadOnlyProbe, "a member missing from the JSON keeps its initializer");
    }

    [TestMethod]
    public void ResizeStateUnknown_IsWrittenAsCompactPascalCaseFromACaseInsensitivePlan()
    {
        var request = new ResizeBrokerRequest(
            ResizeMode.Execute,
            """{"sourceVolumeLetter":"e","newDriveLetter":"q"}""");

        string? json = ElevatedResizeBroker.ClassifyCompletedInvocation(request, exitCode: 1, resultJson: null);

        Assert.AreEqual(
            """{"Success":false,"Executed":true,"StateUnknown":true,"Message":"The elevated resize helper exited with code 1 without a trustworthy result. State is unknown \u2014 check Disk Management before retrying.","SourceVolumeLetter":"E","NewDriveLetter":"Q","DevDriveBytes":0,"DiskNumber":null,"PartitionNumber":null,"FileSystem":"","IsDevDrive":false,"CompletedSteps":[]}""",
            json);
    }

    [TestMethod]
    public void ResizeFinalState_IsReadCaseInsensitivelyButNumbersMustBeNumbers()
    {
        ResizePlan plan = new()
        {
            SourceVolumeLetter = 'C',
            ShrinkBytes = 100UL * Gib,
            NewDriveLetter = 'D',
            ExpectedDiskNumber = 7,
            ExpectedDiskUniqueId = "NVME-DISK-7",
            ExpectedPartitionNumber = 3,
            ExpectedPartitionOffsetBytes = 1024UL * 1024UL,
            ExpectedAlignedShrinkBytes = 100UL * Gib,
        };

        Assert.IsTrue(ResizePowerShellScript.TryParseFinalState(
            """{"finalDriveLetter":"d","finalPartitionSizeBytes":107374182400,"finalFileSystem":"ReFS","finalDiskNumber":7,"finalPartitionNumber":4,"isDevDrive":true}""",
            plan,
            out ResizeFinalState state));
        Assert.AreEqual('D', state.FinalDriveLetter);
        Assert.AreEqual(4, state.FinalPartitionNumber);

        Assert.IsFalse(ResizePowerShellScript.TryParseFinalState(
            """{"FinalDriveLetter":"D","FinalPartitionSizeBytes":"107374182400","FinalFileSystem":"ReFS","FinalDiskNumber":7,"FinalPartitionNumber":4,"IsDevDrive":true}""",
            plan,
            out _));
    }

    // ---- VHDX helper wire format ----------------------------------------------------------------

    [TestMethod]
    public async Task VhdProvisionPlan_IsSentAsCompactPascalCase_AndResultIsReadCaseInsensitively()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        VhdBrokerRequest? observed = null;
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<VhdBrokerRequest>();
                return Task.FromResult<string?>(
                    """{"success":true,"EXECUTED":true,"message":"Created V:.","filePath":"C:\\DevDrives\\Dev.vhdx","physicalPath":"\\\\.\\PhysicalDrive7","diskNumber":7,"driveLetter":"V","sizeBytes":68719476736,"fileSystem":"ReFS"}""");
            });
        var provisioner = new ElevatedVhdProvisioner(
            broker,
            new InMemoryFileSystem(),
            new InMemoryReversibilityStore());

        VhdProvisionResult result = await provisioner.ProvisionAsync(new VhdProvisionPlan
        {
            FilePath = VhdPath,
            MaximumSizeBytes = 65UL * Gib,
            VolumeSizeBytes = 64UL * Gib,
            DriveLetter = 'v',
            Label = "Dev Drive",
        });

        Assert.IsNotNull(observed);
        Assert.AreEqual(
            """{"FilePath":"C:\\DevDrives\\Dev.vhdx","MaximumSizeBytes":69793218560,"VolumeSizeBytes":68719476736,"DynamicallyExpanding":true,"DriveLetter":"V","Label":"Dev Drive","ExecuteAuthorized":true}""",
            observed.PlanJson);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(7, result.DiskNumber);
        Assert.AreEqual('V', result.DriveLetter);
        Assert.AreEqual(@"\\.\PhysicalDrive7", result.PhysicalPath);
    }

    [TestMethod]
    public async Task VhdRevertPlan_IsSentAsCompactPascalCase()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        VhdBrokerRequest? observed = null;
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<VhdBrokerRequest>();
                return Task.FromResult<string?>("""{"Success":true,"Executed":true,"Message":"Reverted."}""");
            });
        var store = new InMemoryReversibilityStore();
        var entry = new ReversibilityEntry
        {
            Id = VhdProvisioner.ReversibilityId(VhdPath),
            Kind = ReversibilityKinds.VhdProvision,
            TargetPath = VhdPath,
        };
        store.Save(entry);

        await new ElevatedVhdProvisioner(broker, new InMemoryFileSystem().AddFile(VhdPath, "vhd"), store)
            .RevertAsync(entry);

        Assert.IsNotNull(observed);
        Assert.AreEqual("""{"FilePath":"C:\\DevDrives\\Dev.vhdx","ExecuteAuthorized":true}""", observed.PlanJson);
        Assert.IsNull(store.TryGet(entry.Id));
    }

    [TestMethod]
    public void VhdStateUnknown_IsWrittenAsCompactPascalCaseFromACaseInsensitivePlan()
    {
        string json = ElevatedVhdBroker.ClassifyCompletedInvocation(
            new VhdBrokerRequest
            {
                Mode = VhdBrokerMode.Create,
                PlanJson = """{"filePath":"C:\\DevDrives\\Dev.vhdx","driveLetter":"v"}""",
            },
            exitCode: 5,
            resultJson: null);

        Assert.AreEqual(
            """{"Success":false,"Executed":true,"StateUnknown":true,"RolledBack":false,"Message":"The elevated VHDX helper exited with code 5 without a trustworthy result. Check Disk Management before retrying.","FilePath":"C:\\DevDrives\\Dev.vhdx","PhysicalPath":"","DiskNumber":null,"DriveLetter":"V","SizeBytes":0,"FileSystem":"","ReversibilityId":""}""",
            json);

        string revert = ElevatedVhdBroker.ClassifyCompletedInvocation(
            new VhdBrokerRequest
            {
                Mode = VhdBrokerMode.Revert,
                PlanJson = """{"FILEPATH":"C:\\DevDrives\\Dev.vhdx"}""",
            },
            exitCode: 5,
            resultJson: null);

        StringAssert.Contains(revert, "\"FilePath\":\"C:\\\\DevDrives\\\\Dev.vhdx\"");
        StringAssert.Contains(revert, "\"DriveLetter\":null");
    }

    [TestMethod]
    public void VhdScriptOutput_IsReadCaseInsensitively()
    {
        Assert.IsTrue(VhdPowerShellScript.TryParseFinalState(
            """{"driveLetter":"V","sizeBytes":68719476736,"fileSystem":"ReFS","isDevDrive":true}""",
            out VhdFinalState final));
        Assert.AreEqual(new VhdFinalState { DriveLetter = 'V', SizeBytes = 64UL * Gib, FileSystem = "ReFS", IsDevDrive = true }, final);

        Assert.IsTrue(VhdPowerShellScript.TryParseCapability(
            """{"SUPPORTED":true,"build":26100,"revision":1,"hasDevDriveParameter":true}""",
            out VhdCapabilityState capability));
        Assert.AreEqual(
            new VhdCapabilityState { Supported = true, Build = 26100, Revision = 1, HasDevDriveParameter = true },
            capability);

        Assert.IsFalse(VhdPowerShellScript.TryParseCapability("""{"Supported":true,"Build":"26100","Revision":1}""", out _));
    }

    private static FreeSpaceSample Sample(int hour, long free) => new()
    {
        TakenAtUtc = new DateTimeOffset(2026, 1, 2, hour, 4, 5, TimeSpan.Zero),
        VolumeId = "C:",
        TotalBytes = 1000,
        FreeBytes = free,
    };

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"ddm-{name}-{Guid.NewGuid():N}.json");
}
