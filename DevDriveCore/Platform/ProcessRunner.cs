using System.Diagnostics;
using System.Text;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IProcessRunner"/>. Captures stdout/stderr asynchronously to avoid pipe
/// deadlocks and enforces a timeout. Never throws for ordinary failures (e.g. tool not found):
/// those are surfaced as a negative exit code so callers can degrade gracefully.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly int _timeoutMs;

    /// <summary>Creates a runner with the given per-process timeout (default 15s).</summary>
    public ProcessRunner(int timeoutMs = 15_000) => _timeoutMs = timeoutMs;

    /// <inheritdoc />
    public ProcessRunResult Run(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            // F22: OutputDataReceived/ErrorDataReceived fire on threadpool threads while this method reads
            // the builders (timeout + normal paths). StringBuilder is not thread-safe, so guard every
            // append AND every snapshot with the same lock to avoid torn reads / corruption.
            object outputLock = new();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { stdout.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { stderr.AppendLine(e.Data); } } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(_timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                lock (outputLock)
                {
                    return new ProcessRunResult(-1, stdout.ToString(), stderr.ToString()) { TimedOut = true };
                }
            }

            // Ensure the async output handlers have flushed.
            process.WaitForExit();
            lock (outputLock)
            {
                return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
            }
        }
        catch (Exception ex)
        {
            // e.g. the executable was not found. Surface as a launch failure rather than throwing.
            return new ProcessRunResult(-1, string.Empty, ex.Message);
        }
    }
}
