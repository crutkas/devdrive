using System.Diagnostics;
using System.Text;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IWorkloadProcessRunner"/>. Like <see cref="ProcessRunner"/> (async stdout/stderr
/// capture, never throws for ordinary failures) but adds a working directory, extra environment
/// variables, a per-request timeout, and cooperative cancellation — all needed by the benchmarks.
/// </summary>
public sealed class WorkloadProcessRunner : IWorkloadProcessRunner
{
    /// <inheritdoc />
    public ProcessRunResult Run(WorkloadProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!string.IsNullOrEmpty(request.WorkingDirectory))
            {
                process.StartInfo.WorkingDirectory = request.WorkingDirectory;
            }

            if (request.Environment is not null)
            {
                foreach (KeyValuePair<string, string> pair in request.Environment)
                {
                    process.StartInfo.Environment[pair.Key] = pair.Value;
                }
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            // F4: OutputDataReceived/ErrorDataReceived fire on threadpool threads while this method reads
            // the builders (timeout + normal paths). StringBuilder is not thread-safe, so guard every
            // append AND every snapshot with the same lock to avoid torn reads / corruption.
            object outputLock = new();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { stdout.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { stderr.AppendLine(e.Data); } } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            int timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 600_000;
            var stopwatch = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    throw new OperationCanceledException(cancellationToken);
                }

                if (stopwatch.ElapsedMilliseconds > timeoutMs)
                {
                    TryKill(process);
                    lock (outputLock)
                    {
                        return new ProcessRunResult(-1, stdout.ToString(), stderr.ToString()) { TimedOut = true };
                    }
                }
            }

            // Ensure the async output handlers have flushed.
            process.WaitForExit();
            lock (outputLock)
            {
                return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // e.g. the executable was not found. Surface as a launch failure rather than throwing.
            return new ProcessRunResult(-1, string.Empty, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
}
