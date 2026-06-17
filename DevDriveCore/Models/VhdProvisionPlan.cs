namespace DevDriveCore.Models;

/// <summary>
/// Describes the virtual disk <see cref="DevDriveCore.Services.VhdProvisioner"/> should create. A
/// SAFE description only — building it changes nothing; execution is the engine's job, run only after
/// an explicit user confirmation.
/// </summary>
public sealed record VhdProvisionPlan
{
    /// <summary>Full path of the backing <c>.vhdx</c> file to create, e.g. <c>C:\DevDrives\dev.vhdx</c>.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Maximum (virtual) size of the disk in bytes.</summary>
    public ulong MaximumSizeBytes { get; init; }

    /// <summary>When <c>true</c> (default) the backing file grows on demand; otherwise it is pre-allocated fixed-size.</summary>
    public bool DynamicallyExpanding { get; init; } = true;
}
