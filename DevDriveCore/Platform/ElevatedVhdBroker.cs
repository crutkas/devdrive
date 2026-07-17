using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Runs complete VHDX creation or recorded VHDX reversion in the bundled UAC-elevated helper.
/// </summary>
public sealed class ElevatedVhdBroker : IElevatedVhdBroker
{
    public const string HelperExeName = "DevDriveManager.FilterProbe.exe";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly string _helperPath;
    private readonly TimeSpan _timeout;

    public ElevatedVhdBroker()
        : this(ResolveHelperPath(), DefaultTimeout)
    {
    }

    public ElevatedVhdBroker(string helperPath, TimeSpan timeout)
    {
        _helperPath = helperPath;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<string?> InvokeAsync(VhdBrokerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(_helperPath) || !File.Exists(_helperPath))
        {
            return null;
        }

        string tempDirectory = Path.GetTempPath();
        string outputPath = Path.Combine(tempDirectory, $"ddm-vhd-{Guid.NewGuid():N}.json");
        string planBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.PlanJson));

        bool processStarted = false;
        bool leaveOutputForChild = false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _helperPath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = BuildArguments(request.Mode, planBase64, outputPath, tempDirectory),
            };

            using Process? process = Process.Start(startInfo);
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
                leaveOutputForChild = true;
                return StateUnknownJson(
                    request,
                    "The elevated VHDX operation did not confirm completion and was not cancelled. " +
                    "Check Disk Management before retrying.");
            }

            string? resultJson = null;
            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                resultJson = await File.ReadAllTextAsync(outputPath, CancellationToken.None).ConfigureAwait(false);
            }

            return ClassifyCompletedInvocation(request, process.ExitCode, resultJson);
        }
        catch (Win32Exception) when (!processStarted)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return processStarted
                ? StateUnknownJson(
                    request,
                    "The elevated VHDX request was interrupted after the helper started. " +
                    "Check Disk Management before retrying.")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return processStarted
                ? StateUnknownJson(
                    request,
                    $"The elevated VHDX helper stopped without a trustworthy result ({ex.Message}). " +
                    "Check Disk Management before retrying.")
                : null;
        }
        finally
        {
            if (!leaveOutputForChild)
            {
                TryDelete(outputPath);
            }
        }
    }

    internal static string BuildArguments(
        VhdBrokerMode mode,
        string planBase64,
        string outputPath,
        string allowedRoot)
    {
        string modeFlag = mode == VhdBrokerMode.Revert ? "--revert" : "--create";
        string normalizedRoot = Path.TrimEndingDirectorySeparator(allowedRoot);
        return $"vhd {modeFlag} --plan {planBase64} --out \"{outputPath}\" " +
               $"--allowed-root \"{normalizedRoot}\"";
    }

    internal static string ClassifyCompletedInvocation(
        VhdBrokerRequest request,
        int exitCode,
        string? resultJson)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(resultJson))
        {
            return resultJson;
        }

        return StateUnknownJson(
            request,
            $"The elevated VHDX helper exited with code {exitCode} without a trustworthy result. " +
            "Check Disk Management before retrying.");
    }

    private static string StateUnknownJson(VhdBrokerRequest request, string message)
    {
        string filePath = string.Empty;
        char? driveLetter = null;
        try
        {
            if (request.Mode == VhdBrokerMode.Create)
            {
                VhdProvisionPlan? plan = JsonSerializer.Deserialize<VhdProvisionPlan>(
                    request.PlanJson,
                    JsonOptions);
                if (plan is not null)
                {
                    filePath = plan.FilePath;
                    driveLetter = char.ToUpperInvariant(plan.DriveLetter);
                }
            }
            else
            {
                VhdRevertPlan? plan = JsonSerializer.Deserialize<VhdRevertPlan>(
                    request.PlanJson,
                    JsonOptions);
                filePath = plan?.FilePath ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // The recovery result remains useful even if the request itself was malformed.
        }

        return JsonSerializer.Serialize(
            new VhdProvisionResult
            {
                Success = false,
                Executed = true,
                StateUnknown = true,
                Message = message,
                FilePath = filePath,
                DriveLetter = driveLetter,
            },
            JsonOptions);
    }

    private static string ResolveHelperPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory))
        {
            baseDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
        }

        return string.IsNullOrEmpty(baseDirectory)
            ? string.Empty
            : Path.Combine(baseDirectory, HelperExeName);
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
            // Temp-result cleanup must not mask the operation outcome.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
