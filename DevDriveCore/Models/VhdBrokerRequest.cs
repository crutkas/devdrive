namespace DevDriveCore.Models;

/// <summary>JSON request sent to the elevated VHD broker.</summary>
public sealed record VhdBrokerRequest
{
    public VhdBrokerMode Mode { get; init; }

    public string PlanJson { get; init; } = string.Empty;
}
