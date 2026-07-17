using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>Crosses the UAC boundary for complete VHDX creation and recorded VHDX reversion.</summary>
public interface IElevatedVhdBroker
{
    /// <summary>
    /// Returns helper JSON, or <c>null</c> when the helper was not started (for example, UAC was declined).
    /// Once a helper starts, broker failures are returned as state-unknown JSON rather than <c>null</c>.
    /// </summary>
    Task<string?> InvokeAsync(VhdBrokerRequest request, CancellationToken cancellationToken = default);
}
