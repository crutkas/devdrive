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

    /// <summary>Maximum virtual size of the VHD container, including GPT/alignment headroom.</summary>
    public ulong MaximumSizeBytes { get; init; }

    /// <summary>Requested size of the formatted Dev Drive partition.</summary>
    public ulong VolumeSizeBytes { get; init; }

    /// <summary>When <c>true</c> (default) the backing file grows on demand; otherwise it is pre-allocated fixed-size.</summary>
    public bool DynamicallyExpanding { get; init; } = true;

    /// <summary>Drive letter to assign to the formatted Dev Drive.</summary>
    public char DriveLetter { get; init; } = 'D';

    /// <summary>Volume label to apply during the ReFS Dev Drive format.</summary>
    public string Label { get; init; } = "DevDrive";

    /// <summary>
    /// Set only by the production provisioner immediately before it crosses the UAC boundary. The
    /// elevated helper refuses a create request without this explicit execution authorization.
    /// </summary>
    public bool ExecuteAuthorized { get; init; }
}
