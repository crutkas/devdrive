using System.Diagnostics;
using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.Services;

/// <summary>
/// Production <see cref="IElevatedFilterProbe"/>: reads a Dev Drive's trust/filter detail by launching
/// the short-lived, UAC-elevated, READ-ONLY <c>DevDriveManager.FilterProbe.exe</c> helper (the "Iso
/// app"), then parsing its result back into the still-running app — no full-app restart as admin.
/// </summary>
/// <remarks>
/// <para>
/// Flow: pick a temp output path under <c>%TEMP%</c> → start the helper elevated
/// (<c>UseShellExecute=true</c>, <c>Verb="runas"</c>, so the UAC prompt is raised and the helper runs
/// as a separate high-integrity process) with the drive letter + output path → await its exit → read
/// the JSON it wrote → parse with <see cref="FsutilDevDrvParser"/> → delete the temp file in a
/// <c>finally</c>.
/// </para>
/// <para>
/// <b>Why a temp file, not stdout?</b> <c>Verb="runas"</c> requires <c>UseShellExecute=true</c>, which
/// is incompatible with stdout redirection — so the elevated child hands its result back through a file
/// the (medium-integrity) app can read.
/// </para>
/// <para>
/// SAFETY: the only privileged action is the read-only <c>fsutil devdrv query</c> the helper runs.
/// Returns <c>null</c> on UAC-declined (<see cref="System.ComponentModel.Win32Exception"/>), a missing
/// helper, any failure, or a timeout, so the caller keeps the "See Filters" affordance and shows a
/// brief "couldn't read filters" note.
/// </para>
/// </remarks>
public sealed class ElevatedFilterProbe : IElevatedFilterProbe
{
    /// <summary>The helper executable name, shipped next to <c>DevDriveManager.exe</c> in the install dir.</summary>
    public const string HelperExeName = "DevDriveManager.FilterProbe.exe";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _helperPath;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a probe that resolves the helper next to the running app.</summary>
    public ElevatedFilterProbe()
        : this(ResolveHelperPath(), DefaultTimeout)
    {
    }

    /// <summary>Test/DI-friendly constructor (explicit helper path + timeout).</summary>
    public ElevatedFilterProbe(string helperPath, TimeSpan timeout)
    {
        _helperPath = helperPath;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<DevDriveTrustInfo?> ProbeAsync(char driveLetter, CancellationToken cancellationToken = default)
    {
        char letter = char.ToUpperInvariant(driveLetter);
        if (letter is < 'A' or > 'Z')
        {
            return null;
        }

        if (string.IsNullOrEmpty(_helperPath) || !File.Exists(_helperPath))
        {
            // Helper missing from the install dir — keep the affordance + restart fallback.
            return null;
        }

        string outPath = Path.Combine(Path.GetTempPath(), $"ddm-filters-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _helperPath,
                // runas requires ShellExecute; the helper writes its result to outPath (a file the app reads).
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                // The "query" verb, drive letter, then quoted output path (the temp path may contain spaces).
                Arguments = $"query {letter} \"{outPath}\"",
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }

            if (!File.Exists(outPath))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false);
            ElevatedProbeResult? result = JsonSerializer.Deserialize<ElevatedProbeResult>(json, JsonOptions);
            if (result is null)
            {
                return null;
            }

            // Reuse the shared, tested parser — the elevated path produces the SAME DevDriveTrustInfo the
            // elevated-at-launch path does today.
            return FsutilDevDrvParser.Parse(result.ExitCode, result.Stdout, result.Stderr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined (ERROR_CANCELLED) or the shell couldn't elevate.
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Couldn't launch, read, or parse — treat as "couldn't read filters".
            return null;
        }
        finally
        {
            TryDelete(outPath);
        }
    }

    /// <summary>Resolves the helper path next to <c>DevDriveManager.exe</c> in the (packaged) install dir.</summary>
    private static string ResolveHelperPath()
    {
        string baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
        }

        return string.IsNullOrEmpty(baseDir) ? string.Empty : Path.Combine(baseDir, HelperExeName);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort — temp file cleanup must never throw out of the probe.
        }
    }

    /// <summary>JSON shape written by the helper: the raw <c>fsutil devdrv query</c> result.</summary>
    private sealed record ElevatedProbeResult(int ExitCode, string? Stdout, string? Stderr);
}
