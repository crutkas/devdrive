using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Production <see cref="IVolumeResizer"/>: serializes a <see cref="ResizePlan"/>, hands it to the
/// elevated helper through an injected <see cref="IElevatedResizeBroker"/>, and parses the JSON the
/// helper writes back. The resize counterpart to <see cref="VhdProvisioner"/> &#8212; the broker seam
/// here plays the role <see cref="DevDriveCore.Abstractions.INativeVhdApi"/> plays there.
/// </summary>
/// <remarks>
/// <para>
/// <b>SAFETY.</b> <see cref="PreviewAsync"/> only ever requests <see cref="ResizeMode.WhatIf"/> &#8212;
/// the helper's READ-ONLY feasibility check. <see cref="ExecuteAsync"/> requests
/// <see cref="ResizeMode.Execute"/>, the real destructive path; the app keeps that behind a default-off
/// feature flag + explicit confirmation + UAC. Every unit test injects a MOCK
/// <see cref="IElevatedResizeBroker"/> returning canned JSON, so no test spawns the helper or touches a
/// real disk.
/// </para>
/// </remarks>
public sealed class VolumeResizer : IVolumeResizer
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IElevatedResizeBroker _broker;

    /// <summary>Creates the resizer over an injected broker (tests pass a fake; the app passes the real one).</summary>
    public VolumeResizer(IElevatedResizeBroker broker)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    /// <summary>
    /// Convenience factory wiring the REAL <see cref="DevDriveCore.Platform.ElevatedResizeBroker"/>
    /// (UAC-elevated helper). The app composes this only after the user opts in; the UI-test seam
    /// substitutes a safe fake instead.
    /// </summary>
    public static VolumeResizer CreateDefault() => new(new Platform.ElevatedResizeBroker());

    /// <inheritdoc />
    public async Task<ResizeFeasibility?> PreviewAsync(ResizePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string planJson = JsonSerializer.Serialize(plan, JsonOptions);
        string? resultJson = await _broker
            .InvokeAsync(new ResizeBrokerRequest(ResizeMode.WhatIf, planJson), cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resultJson))
        {
            // Helper unavailable / UAC declined / timed out — let the caller fall back to SimulateResize.
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ResizeFeasibility>(resultJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ResizeExecuteOutcome> ExecuteAsync(ResizePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // F3: stamp the execute-authorization flag ONLY on this gated path, immediately before serializing.
        // The helper refuses a `--execute` request whose plan lacks it, so a bare command-line `--execute`
        // (or a reused preview plan) can't reach the destructive path. Defence in depth — see ResizePlan.
        ResizePlan authorizedPlan = plan with { ExecuteAuthorized = true };
        string planJson = JsonSerializer.Serialize(authorizedPlan, JsonOptions);
        string? resultJson = await _broker
            .InvokeAsync(new ResizeBrokerRequest(ResizeMode.Execute, planJson), cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return NotExecuted(plan, "The elevated resize helper was unavailable or the request was declined. Nothing was changed.");
        }

        try
        {
            ResizeExecuteOutcome? outcome =
                JsonSerializer.Deserialize<ResizeExecuteOutcome>(resultJson, JsonOptions);
            return IsTrustworthy(outcome)
                ? outcome!
                : StateUnknown(plan, "The elevated helper returned an incomplete resize result.");
        }
        catch (JsonException)
        {
            return StateUnknown(plan, "The elevated helper returned an unreadable resize result.");
        }
    }

    private static bool IsTrustworthy(ResizeExecuteOutcome? outcome) =>
        outcome is not null &&
        !string.IsNullOrWhiteSpace(outcome.Message) &&
        IsDriveLetter(outcome.SourceVolumeLetter) &&
        IsDriveLetter(outcome.NewDriveLetter) &&
        (!outcome.Success || outcome.Executed);

    private static bool IsDriveLetter(char letter)
    {
        char normalized = char.ToUpperInvariant(letter);
        return normalized is >= 'A' and <= 'Z';
    }

    private static ResizeExecuteOutcome StateUnknown(ResizePlan plan, string reason) => new()
    {
        Success = false,
        Executed = true,
        Message = $"{reason} State is unknown — check Disk Management before retrying.",
        SourceVolumeLetter = char.ToUpperInvariant(plan.SourceVolumeLetter),
        NewDriveLetter = char.ToUpperInvariant(plan.NewDriveLetter),
        DevDriveBytes = plan.ShrinkBytes,
    };

    private static ResizeExecuteOutcome NotExecuted(ResizePlan plan, string message) => new()
    {
        Success = false,
        Executed = false,
        Message = message,
        SourceVolumeLetter = char.ToUpperInvariant(plan.SourceVolumeLetter),
        NewDriveLetter = char.ToUpperInvariant(plan.NewDriveLetter),
        DevDriveBytes = plan.ShrinkBytes,
    };
}
