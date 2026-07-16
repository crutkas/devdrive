using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="VolumeResizer"/> — the production <see cref="IVolumeResizer"/> that marshals a
/// <see cref="ResizePlan"/> across the elevation boundary. The <see cref="IElevatedResizeBroker"/> is
/// ALWAYS a recording fake returning canned JSON, so these tests verify the mode/flag selection, the
/// plan serialization, the result parsing, and the safe null-fallback WITHOUT spawning the elevated
/// helper or touching a real disk.
/// </summary>
[TestClass]
public sealed class VolumeResizerTests
{
    private const ulong Gib = 1024UL * 1024UL * 1024UL;

    private static ResizePlan Plan() =>
        new() { SourceVolumeLetter = 'C', ShrinkBytes = 100UL * Gib, NewDriveLetter = 'D', Label = "MyDev" };

    // ---- preview (--whatif) --------------------------------------------------------------------

    [TestMethod]
    public async Task PreviewAsync_RequestsWhatIf_SerializesPlan_AndParsesFeasibility()
    {
        var expected = new ResizeFeasibility
        {
            CanProceed = true,
            Reason = "ok",
            SourceVolumeLetter = 'C',
            NewDriveLetter = 'D',
            AlignedShrinkBytes = 100UL * Gib,
            ReclaimableBytes = 300UL * Gib,
        };
        var broker = new RecordingBroker(JsonSerializer.Serialize(expected));
        var resizer = new VolumeResizer(broker);

        ResizeFeasibility? result = await resizer.PreviewAsync(Plan());

        Assert.IsNotNull(result);
        Assert.IsTrue(result!.CanProceed);
        Assert.AreEqual(100UL * Gib, result.AlignedShrinkBytes);
        Assert.AreEqual(300UL * Gib, result.ReclaimableBytes);

        // The broker was asked for the READ-ONLY mode exactly once.
        Assert.AreEqual(1, broker.CallCount);
        Assert.AreEqual(ResizeMode.WhatIf, broker.LastRequest!.Mode);

        // The plan round-trips through the wire JSON unchanged.
        ResizePlan sent = JsonSerializer.Deserialize<ResizePlan>(broker.LastRequest.PlanJson)!;
        Assert.AreEqual('C', sent.SourceVolumeLetter);
        Assert.AreEqual('D', sent.NewDriveLetter);
        Assert.AreEqual(100UL * Gib, sent.ShrinkBytes);
        Assert.AreEqual("MyDev", sent.Label);
    }

    [TestMethod]
    public async Task PreviewAsync_NullResponse_ReturnsNull_ForFallback()
    {
        var resizer = new VolumeResizer(new RecordingBroker(null));
        Assert.IsNull(await resizer.PreviewAsync(Plan()));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not json at all")]
    [DataRow("{ \"canProceed\": ")] // truncated
    public async Task PreviewAsync_UnusableResponse_ReturnsNull(string response)
    {
        var resizer = new VolumeResizer(new RecordingBroker(response));
        Assert.IsNull(await resizer.PreviewAsync(Plan()));
    }

    [TestMethod]
    public async Task PreviewAsync_ParsesCamelCaseJson_CaseInsensitively()
    {
        // The helper writes PascalCase, but parsing must be case-insensitive (PowerShell casing varies).
        const string camel = "{\"canProceed\":true,\"alignedShrinkBytes\":107374182400,\"reason\":\"go\"}";
        var resizer = new VolumeResizer(new RecordingBroker(camel));

        ResizeFeasibility? result = await resizer.PreviewAsync(Plan());

        Assert.IsNotNull(result);
        Assert.IsTrue(result!.CanProceed);
        Assert.AreEqual(100UL * Gib, result.AlignedShrinkBytes);
    }

    // ---- execute (--execute) -------------------------------------------------------------------

    [TestMethod]
    public async Task ExecuteAsync_RequestsExecute_AndParsesOutcome()
    {
        var expected = new ResizeExecuteOutcome
        {
            Success = true,
            Executed = true,
            Message = "done",
            SourceVolumeLetter = 'C',
            NewDriveLetter = 'D',
            DevDriveBytes = 100UL * Gib,
        };
        var broker = new RecordingBroker(JsonSerializer.Serialize(expected));
        var resizer = new VolumeResizer(broker);

        ResizeExecuteOutcome outcome = await resizer.ExecuteAsync(Plan());

        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(outcome.Executed);
        Assert.AreEqual(100UL * Gib, outcome.DevDriveBytes);
        Assert.AreEqual(ResizeMode.Execute, broker.LastRequest!.Mode);
    }

    [TestMethod]
    public async Task ExecuteAsync_NullResponse_ReportsNotExecuted()
    {
        var resizer = new VolumeResizer(new RecordingBroker(null));

        ResizeExecuteOutcome outcome = await resizer.ExecuteAsync(Plan());

        Assert.IsFalse(outcome.Success);
        Assert.IsFalse(outcome.Executed, "A null helper response must NEVER report a real mutation.");
        StringAssert.Contains(outcome.Message, "Nothing was changed", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("garbage")]
    [DataRow("{ broken")]
    public async Task ExecuteAsync_UnparseableResponse_ReportsStateUnknown(string response)
    {
        var resizer = new VolumeResizer(new RecordingBroker(response));

        ResizeExecuteOutcome outcome = await resizer.ExecuteAsync(Plan());

        Assert.IsTrue(outcome.Executed, "An unparseable execute response must conservatively report unknown state.");
        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "State is unknown", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ExecuteAsync_IncompleteResponse_ReportsStateUnknown()
    {
        var resizer = new VolumeResizer(new RecordingBroker("{}"));

        ResizeExecuteOutcome outcome = await resizer.ExecuteAsync(Plan());

        Assert.IsTrue(outcome.Executed);
        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "State is unknown", StringComparison.OrdinalIgnoreCase);
    }

    // ---- guards --------------------------------------------------------------------------------

    [TestMethod]
    public void Ctor_NullBroker_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new VolumeResizer(null!));
    }

    [TestMethod]
    public async Task NullPlan_Throws()
    {
        var resizer = new VolumeResizer(new RecordingBroker(null));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await resizer.PreviewAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await resizer.ExecuteAsync(null!));
    }

    [TestMethod]
    public void CreateDefault_ReturnsInstance()
    {
        Assert.IsNotNull(VolumeResizer.CreateDefault());
    }

    // A fake broker that records the request and returns a canned response — never elevates or runs the
    // helper, so no real disk op can occur.
    private sealed class RecordingBroker : IElevatedResizeBroker
    {
        private readonly string? _response;

        public RecordingBroker(string? response) => _response = response;

        public int CallCount { get; private set; }

        public ResizeBrokerRequest? LastRequest { get; private set; }

        public Task<string?> InvokeAsync(ResizeBrokerRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }
}
