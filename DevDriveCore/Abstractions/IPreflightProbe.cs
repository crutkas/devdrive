using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Captures the pre-flight context (Defender performance mode, Dev Drive trust, storage class,
/// machine summary, free space) that the real-workload comparison surfaces so its numbers can be
/// trusted — see the methodology's pre-flight checklist (§7.5).
/// </summary>
/// <remarks>Every probe is best-effort and must degrade gracefully (e.g. trust is <c>null</c> when
/// <c>fsutil devdrv query</c> is denied for lack of elevation). The composing implementation is
/// mockable so the orchestrator's unit tests inject a canned <see cref="PreflightInfo"/>.</remarks>
public interface IPreflightProbe
{
    /// <summary>
    /// Probes the environment for the given drives. <paramref name="devDriveLetter"/> is the Dev
    /// Drive letter without a colon (e.g. <c>'G'</c>), used to find the backing physical disk.
    /// </summary>
    PreflightInfo Capture(string systemRoot, string devRoot, char devDriveLetter, CancellationToken cancellationToken = default);
}
