using System.Globalization;
using System.Runtime.InteropServices;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Default <see cref="IPreflightProbe"/>. Captures the pre-flight context the real-workload
/// comparison surfaces (Defender performance mode, Dev Drive trust, storage class, machine summary,
/// free space) by composing an <see cref="IProcessRunner"/> (PowerShell <c>Get-MpPreference</c> and
/// <c>fsutil devdrv query</c>), an <see cref="IStorageQuery"/> (drive&#160;→&#160;disk) and an
/// <see cref="IPhysicalDiskQuery"/> (disk media/bus).
/// </summary>
/// <remarks>
/// <para>Every probe is best-effort and isolated in its own try/catch: a failure (e.g. Defender
/// absent, or <c>fsutil</c> denied for lack of elevation) yields <c>null</c>/"Unknown" rather than an
/// exception, so the comparison still runs.</para>
/// <para>The parsing helpers are pure <c>static</c> methods so they can be unit-tested directly; the
/// free-space / CPU / OS lookups are injectable seams so <see cref="Capture"/> is testable without
/// real hardware.</para>
/// </remarks>
public sealed class PreflightProbe : IPreflightProbe
{
    private readonly IProcessRunner _processRunner;
    private readonly IStorageQuery _storageQuery;
    private readonly IPhysicalDiskQuery _physicalDiskQuery;
    private readonly Func<string, long> _freeBytesProbe;
    private readonly Func<string?> _cpuNameProvider;
    private readonly Func<string> _osDescriptionProvider;

    /// <summary>Creates a probe over the supplied process runner and storage queries, with optional environment seams.</summary>
    public PreflightProbe(
        IProcessRunner processRunner,
        IStorageQuery storageQuery,
        IPhysicalDiskQuery physicalDiskQuery,
        Func<string, long>? freeBytesProbe = null,
        Func<string?>? cpuNameProvider = null,
        Func<string>? osDescriptionProvider = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _storageQuery = storageQuery ?? throw new ArgumentNullException(nameof(storageQuery));
        _physicalDiskQuery = physicalDiskQuery ?? throw new ArgumentNullException(nameof(physicalDiskQuery));
        _freeBytesProbe = freeBytesProbe ?? DefaultFreeBytes;
        _cpuNameProvider = cpuNameProvider ?? (() => Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"));
        _osDescriptionProvider = osDescriptionProvider ?? (() => RuntimeInformation.OSDescription);
    }

    /// <summary>Convenience factory wiring the real process runner and Storage WMI queries.</summary>
    public static PreflightProbe CreateDefault() =>
        new(new ProcessRunner(30_000), new StorageQueryWmi(), new PhysicalDiskQueryWmi());

    /// <inheritdoc />
    public PreflightInfo Capture(string systemRoot, string devRoot, char devDriveLetter, CancellationToken cancellationToken = default)
    {
        return new PreflightInfo
        {
            DefenderPerformanceModeOn = ProbePerformanceMode(),
            DevDriveTrusted = ProbeTrust(devDriveLetter),
            StorageClass = ProbeStorageClass(devDriveLetter),
            MachineSummary = FormatMachineSummary(_cpuNameProvider(), Environment.ProcessorCount, _osDescriptionProvider()),
            SystemFreeBytes = _freeBytesProbe(systemRoot),
            DevFreeBytes = _freeBytesProbe(devRoot),
        };
    }

    private bool? ProbePerformanceMode()
    {
        try
        {
            // [int] forces the PerformanceModeStatus enum to print as 0/1 regardless of formatting.
            ProcessRunResult run = _processRunner.Run(
                "powershell",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[int](Get-MpPreference).PerformanceModeStatus\"");

            if (run is null || run.ExitCode < 0 || run.TimedOut)
            {
                return null;
            }

            return ParsePerformanceMode(run.StandardOutput);
        }
        catch
        {
            return null;
        }
    }

    private bool? ProbeTrust(char devDriveLetter)
    {
        try
        {
            ProcessRunResult run = _processRunner.Run("fsutil", $"devdrv query {char.ToUpperInvariant(devDriveLetter)}:");
            DevDriveTrustInfo? info = FsutilDevDrvParser.Parse(run.ExitCode, run.StandardOutput, run.StandardError);
            return info?.TrustState switch
            {
                DevDriveTrustState.Trusted => true,
                DevDriveTrustState.Untrusted => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private string ProbeStorageClass(char devDriveLetter)
    {
        try
        {
            char letter = char.ToUpperInvariant(devDriveLetter);

            uint? diskNumber = _storageQuery.GetPartitions()
                .Where(p => p.DriveLetter == letter)
                .Select(p => (uint?)p.DiskNumber)
                .FirstOrDefault();

            if (diskNumber is null)
            {
                return "Unknown";
            }

            PhysicalDiskRecord? disk = _physicalDiskQuery.GetPhysicalDisks()
                .FirstOrDefault(d => d.DeviceId == diskNumber.Value);

            return disk is null
                ? "Unknown"
                : StorageClassFormatter.Format(disk.MediaType, disk.BusType, disk.FriendlyName);
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Interprets the <c>[int](Get-MpPreference).PerformanceModeStatus</c> output:
    /// <c>1</c> = Enabled (performance mode ON / asynchronous scanning) → <c>true</c>; <c>0</c> = Disabled
    /// (OFF / synchronous) → <c>false</c>; anything else (empty, error, unexpected) → <c>null</c>. Also
    /// tolerates the enum name forms. (Verified empirically: on a machine with performance mode ON,
    /// <c>[int](Get-MpPreference).PerformanceModeStatus</c> reads <c>1</c>, matching Windows Security ›
    /// Dev Drive protection › "See volumes". This is the inverse of the Intune/CSP OMA-URI integer.)
    /// </summary>
    public static bool? ParsePerformanceMode(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string trimmed = output.Trim();

        // Numeric form (forced by the [int] cast): grab the first integer token.
        foreach (string line in trimmed.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value switch { 1 => true, 0 => false, _ => null };
            }
        }

        // Defensive fallback if the enum printed by name.
        if (trimmed.Contains("enabl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Contains("disabl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    /// <summary>
    /// Formats a one-line machine summary from a CPU descriptor, logical-core count, and OS string,
    /// skipping blank parts, e.g. "Intel64 Family 6 … · 16 logical cores · Microsoft Windows 10.0.26100".
    /// </summary>
    public static string FormatMachineSummary(string? cpu, int logicalCores, string? osDescription)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(cpu))
        {
            parts.Add(cpu.Trim());
        }

        if (logicalCores > 0)
        {
            parts.Add($"{logicalCores} logical cores");
        }

        if (!string.IsNullOrWhiteSpace(osDescription))
        {
            parts.Add(osDescription.Trim());
        }

        return parts.Count == 0 ? "Unknown" : string.Join(" · ", parts);
    }

    private static long DefaultFreeBytes(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0L;
        }
    }
}
