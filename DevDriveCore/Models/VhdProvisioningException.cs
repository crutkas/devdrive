namespace DevDriveCore.Models;

/// <summary>Identifies the failed native VHD stage and whether its side effects were fully removed.</summary>
public sealed class VhdProvisioningException : Exception
{
    public VhdProvisioningException(
        VhdProvisioningStage stage,
        bool rollbackConfirmed,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Stage = stage;
        RollbackConfirmed = rollbackConfirmed;
    }

    public VhdProvisioningStage Stage { get; }

    public bool RollbackConfirmed { get; }
}

public enum VhdProvisioningStage
{
    Create,
    Attach,
}
