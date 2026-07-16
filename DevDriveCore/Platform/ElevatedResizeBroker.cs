using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Platform;

/// <summary>
/// Production <see cref="IElevatedResizeBroker"/>: runs the whole resize sequence in ONE UAC-elevated
/// invocation of <c>DevDriveManager.FilterProbe.exe</c> (the same helper that does the read-only filter
/// query), using exactly the pattern <c>ElevatedFilterProbe</c> uses.
/// </summary>
/// <remarks>
/// <para>
/// Flow: base64-encode the plan JSON &#8594; pick a temp <c>ddm-resize-*.json</c> OUTPUT path &#8594;
/// start the helper elevated (<c>UseShellExecute=true</c>, <c>Verb="runas"</c> &#8594; one UAC prompt,
/// separate high-integrity process) with
/// <c>resize --whatif|--execute --plan &lt;base64&gt; --out &lt;outPath&gt; --allowed-root &lt;userTemp&gt;</c>
/// &#8594; await exit &#8594; read the JSON result &#8594; delete the temp output file in a
/// <c>finally</c>.
/// </para>
/// <para>
/// <b>F3 — the plan travels as a base64 command-line argument, NOT a temp file.</b> Passing the plan
/// inline eliminates the mutable plan-FILE TOCTOU (nothing on disk can be swapped between the guard read
/// and execution). The OUTPUT still needs a file because <c>Verb="runas"</c> requires
/// <c>UseShellExecute=true</c>, which can't redirect stdout. We pass the USER's temp dir explicitly via
/// <c>--allowed-root</c> so over-the-shoulder elevation works: when an administrator approves the prompt,
/// the elevated child's <c>%TEMP%</c> is the admin's, not the user's, so without this the app-owned-root
/// check would reject the user-side output path.
/// </para>
/// <para>
/// <b>SAFETY:</b> returns <c>null</c> when the helper was not started (for example, UAC was declined or
/// the helper is missing) and for failed <see cref="ResizeMode.WhatIf"/> calls. Once an execute helper
/// starts, a timeout, nonzero exit, missing output, or unreadable output becomes a "state unknown — check
/// Disk Management" outcome, so the UI never falsely claims nothing changed. A timed-out destructive
/// child is <b>NOT</b> killed because cancelling a shrink/format mid-flight can corrupt the volume.
/// </para>
/// </remarks>
public sealed class ElevatedResizeBroker : IElevatedResizeBroker
{
    /// <summary>The helper executable name, shipped next to <c>DevDriveManager.exe</c> in the install dir.</summary>
    public const string HelperExeName = "DevDriveManager.FilterProbe.exe";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly string _helperPath;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a broker that resolves the helper next to the running app.</summary>
    public ElevatedResizeBroker()
        : this(ResolveHelperPath(), DefaultTimeout)
    {
    }

    /// <summary>Test/DI-friendly constructor (explicit helper path + timeout).</summary>
    public ElevatedResizeBroker(string helperPath, TimeSpan timeout)
    {
        _helperPath = helperPath;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<string?> InvokeAsync(ResizeBrokerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(_helperPath) || !File.Exists(_helperPath))
        {
            // Helper missing from the install dir — caller falls back to a pure-compute estimate.
            return null;
        }

        string tempDir = Path.GetTempPath();
        string outPath = Path.Combine(tempDir, $"ddm-resize-{Guid.NewGuid():N}.json");

        // F3: the plan is base64-encoded onto the command line (no plan FILE → no plan-file TOCTOU).
        string planB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.PlanJson));

        bool leaveOutputForChild = false;
        bool processStarted = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _helperPath,
                // runas requires ShellExecute; only the OUTPUT travels through a temp file the helper hardens.
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = BuildArguments(request.Mode, planB64, outPath, tempDir),
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            processStarted = true;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (request.Mode == ResizeMode.Execute)
                {
                    // F7: the elevated child may be mid-mutation (shrink/carve/format). Do NOT kill it —
                    // aborting partway can corrupt the volume. Leave it running and report state-unknown so
                    // the UI tells the user to check Disk Management rather than "Nothing was changed".
                    leaveOutputForChild = true;
                    return StateUnknownJson(
                        request,
                        "The resize did not confirm completion within the time limit and was NOT cancelled " +
                        "(cancelling a shrink/format mid-operation can corrupt the disk). State is unknown — " +
                        "check Disk Management.");
                }

                // WhatIf is read-only — safe to kill on timeout/cancel.
                TryKill(process);
                return null;
            }

            string? resultJson = null;
            if (process.ExitCode == 0 && File.Exists(outPath))
            {
                CancellationToken readToken =
                    request.Mode == ResizeMode.Execute ? CancellationToken.None : cancellationToken;
                resultJson = await File.ReadAllTextAsync(outPath, readToken).ConfigureAwait(false);
            }

            return ClassifyCompletedInvocation(request, process.ExitCode, resultJson);
        }
        catch (Win32Exception) when (!processStarted)
        {
            // UAC declined (ERROR_CANCELLED) or the shell couldn't elevate.
            return null;
        }
        catch (OperationCanceledException)
        {
            return request.Mode == ResizeMode.Execute && processStarted
                ? StateUnknownJson(
                    request,
                    "The elevated resize request was interrupted after the helper started. State is unknown — " +
                    "check Disk Management before retrying.")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return request.Mode == ResizeMode.Execute && processStarted
                ? StateUnknownJson(
                    request,
                    $"The elevated resize helper stopped without a trustworthy result ({ex.Message}). " +
                    "State is unknown — check Disk Management before retrying.")
                : null;
        }
        finally
        {
            if (!leaveOutputForChild)
            {
                // On an execute-timeout the child may still write outPath; leave it for the child (a stray
                // temp .json on a genuine 10-minute timeout is harmless next to a possibly-mutated disk).
                TryDelete(outPath);
            }
        }
    }

    internal static string BuildArguments(
        ResizeMode mode,
        string planBase64,
        string outputPath,
        string allowedRoot)
    {
        string modeFlag = mode == ResizeMode.Execute ? "--execute" : "--whatif";
        string normalizedRoot = Path.TrimEndingDirectorySeparator(allowedRoot);
        return $"resize {modeFlag} --plan {planBase64} --out \"{outputPath}\" " +
               $"--allowed-root \"{normalizedRoot}\"";
    }

    internal static string? ClassifyCompletedInvocation(
        ResizeBrokerRequest request,
        int exitCode,
        string? resultJson)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(resultJson))
        {
            return resultJson;
        }

        return request.Mode == ResizeMode.Execute
            ? StateUnknownJson(
                request,
                $"The elevated resize helper exited with code {exitCode} without a trustworthy result. " +
                "State is unknown — check Disk Management before retrying.")
            : null;
    }

    // A self-describing "state unknown" outcome for an execute helper that was launched but did not
    // return a trustworthy result. Executed=true so the UI never reports "Nothing was changed".
    private static string StateUnknownJson(ResizeBrokerRequest request, string message)
    {
        char source = 'C';
        char target = 'D';
        try
        {
            ResizePlan? plan = JsonSerializer.Deserialize<ResizePlan>(request.PlanJson, VolumeResizer.JsonOptions);
            if (plan is not null)
            {
                source = char.ToUpperInvariant(plan.SourceVolumeLetter);
                target = char.ToUpperInvariant(plan.NewDriveLetter);
            }
        }
        catch (JsonException)
        {
            // Fall back to the C:/D: defaults for the message only.
        }

        var outcome = new ResizeExecuteOutcome
        {
            Success = false,
            Executed = true,
            Message = message,
            SourceVolumeLetter = source,
            NewDriveLetter = target,
        };
        return JsonSerializer.Serialize(outcome, VolumeResizer.JsonOptions);
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
            // best effort — temp file cleanup must never throw out of the broker.
        }
    }
}
