using System.Text.Json;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class ElevatedResizeBrokerTests
{
    [TestMethod]
    public void BuildArguments_TrimsAllowedRootTrailingSeparator()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-broker-root") + Path.DirectorySeparatorChar;
        string output = Path.Combine(root, "ddm-resize-result.json");

        string arguments = ElevatedResizeBroker.BuildArguments(
            ResizeMode.Execute,
            "cGxhbg==",
            output,
            root);

        StringAssert.EndsWith(
            arguments,
            $"--allowed-root \"{Path.TrimEndingDirectorySeparator(root)}\"");
        StringAssert.Contains(arguments, $"--out \"{output}\"");
    }

    [TestMethod]
    public void ClassifyCompletedInvocation_ExecuteFailure_ReportsStateUnknown()
    {
        ResizeBrokerRequest request = ExecuteRequest();

        string? json = ElevatedResizeBroker.ClassifyCompletedInvocation(request, 3, null);
        ResizeExecuteOutcome outcome =
            JsonSerializer.Deserialize<ResizeExecuteOutcome>(json!, VolumeResizer.JsonOptions)!;

        Assert.IsFalse(outcome.Success);
        Assert.IsTrue(outcome.Executed);
        StringAssert.Contains(outcome.Message, "State is unknown", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ClassifyCompletedInvocation_WhatIfFailure_ReturnsNull()
    {
        var request = new ResizeBrokerRequest(ResizeMode.WhatIf, "{}");

        Assert.IsNull(ElevatedResizeBroker.ClassifyCompletedInvocation(request, 2, null));
    }

    [TestMethod]
    public void ClassifyCompletedInvocation_ExecuteSuccess_ReturnsExactResult()
    {
        ResizeBrokerRequest request = ExecuteRequest();
        const string result = "{\"Success\":false,\"Executed\":false,\"Message\":\"guard refused\"}";

        Assert.AreEqual(
            result,
            ElevatedResizeBroker.ClassifyCompletedInvocation(request, 0, result));
    }

    [TestMethod]
    public void ClassifyCompletedInvocation_ExecuteMissingResult_ReportsStateUnknown()
    {
        ResizeBrokerRequest request = ExecuteRequest();

        string? json = ElevatedResizeBroker.ClassifyCompletedInvocation(request, 0, null);
        ResizeExecuteOutcome outcome =
            JsonSerializer.Deserialize<ResizeExecuteOutcome>(json!, VolumeResizer.JsonOptions)!;

        Assert.IsTrue(outcome.Executed);
        StringAssert.Contains(outcome.Message, "State is unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static ResizeBrokerRequest ExecuteRequest()
    {
        string planJson = JsonSerializer.Serialize(
            new ResizePlan
            {
                SourceVolumeLetter = 'C',
                NewDriveLetter = 'D',
                ShrinkBytes = 100UL * 1024UL * 1024UL * 1024UL,
            },
            VolumeResizer.JsonOptions);
        return new ResizeBrokerRequest(ResizeMode.Execute, planJson);
    }
}
