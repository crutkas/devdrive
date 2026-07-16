using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// The narrow seam between <see cref="DevDriveCore.Services.VolumeResizer"/> and the actual elevation:
/// hand the elevated helper a serialized <see cref="ResizePlan"/> in the requested
/// <see cref="ResizeMode"/> and return the raw result JSON it writes back.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation (<see cref="DevDriveCore.Platform.ElevatedResizeBroker"/>) launches
/// the UAC-elevated helper with <c>Process.Start(Verb="runas")</c> and exchanges JSON through temp
/// files &#8212; exactly the pattern <c>ElevatedFilterProbe</c> uses. Returning the raw JSON (rather
/// than a typed object) keeps this seam trivial to fake: a unit test substitutes canned JSON so
/// <see cref="DevDriveCore.Services.VolumeResizer"/>'s marshalling and mode/flag logic can be verified
/// WITHOUT spawning a real process or touching a real disk.
/// </para>
/// </remarks>
public interface IElevatedResizeBroker
{
    /// <summary>
    /// Invokes the elevated helper for <paramref name="request"/> and returns the raw result JSON.
    /// Returns <c>null</c> when the helper was not started (for example, it was unavailable or UAC was
    /// declined). Execute failures after launch return a state-unknown result instead.
    /// </summary>
    Task<string?> InvokeAsync(ResizeBrokerRequest request, CancellationToken cancellationToken = default);
}
