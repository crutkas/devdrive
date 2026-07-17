namespace DevDriveCore.Models;

/// <summary>Authorized request to detach and delete a previously recorded VHDX.</summary>
public sealed record VhdRevertPlan
{
    public string FilePath { get; init; } = string.Empty;

    public bool ExecuteAuthorized { get; init; }
}
