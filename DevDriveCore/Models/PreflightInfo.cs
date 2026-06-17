namespace DevDriveCore.Models;

/// <summary>
/// Read-only context captured <em>before</em> the real-workload benchmarks run, so the numbers can be
/// trusted (or appropriately doubted). Surfaced alongside the results — see the methodology's
/// pre-flight checklist (§7.5): without Defender performance mode active on a trusted Dev Drive the
/// comparison measures ReFS-vs-NTFS, not the developer-workload advantage Dev Drive is meant to give.
/// </summary>
/// <remarks>Every probe is best-effort; a <c>null</c> bool means "couldn't determine" (e.g.
/// <c>fsutil devdrv query</c> needs elevation and was denied). Pure data.</remarks>
public sealed record PreflightInfo
{
    /// <summary>
    /// Whether Microsoft Defender performance mode (asynchronous scanning) is on. <c>null</c> when it
    /// couldn't be read (e.g. Defender absent, or <c>Get-MpPreference</c> unavailable).
    /// </summary>
    public bool? DefenderPerformanceModeOn { get; init; }

    /// <summary>
    /// Whether the Dev Drive is trusted. <c>null</c> when it couldn't be read (almost always because
    /// <c>fsutil devdrv query</c> needs elevation).
    /// </summary>
    public bool? DevDriveTrusted { get; init; }

    /// <summary>Storage class of the disk backing the Dev Drive, e.g. "Samsung NVMe · SSD · NVMe" or "Unspecified · SAS".</summary>
    public string StorageClass { get; init; } = "Unknown";

    /// <summary>One-line machine summary, e.g. "Intel Core i7 · 16 logical cores · Microsoft Windows 10.0.26100".</summary>
    public string MachineSummary { get; init; } = string.Empty;

    /// <summary>Free space on the system drive at probe time, in bytes (0 when unreadable).</summary>
    public long SystemFreeBytes { get; init; }

    /// <summary>Free space on the Dev Drive at probe time, in bytes (0 when unreadable).</summary>
    public long DevFreeBytes { get; init; }

    /// <summary>How the runs were measured, e.g. "Cold first build · 5 runs · first discarded · median".</summary>
    public string RunModeNote { get; init; } = string.Empty;
}
