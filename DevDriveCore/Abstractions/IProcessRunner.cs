namespace DevDriveCore.Abstractions;

/// <summary>Result of running an external process.</summary>
/// <param name="ExitCode">Process exit code; negative values denote launch failure / timeout.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the process was killed because it exceeded the timeout.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Wraps launching a process so the service can shell out to tools such as <c>fsutil</c> while
/// staying unit-testable (tests inject crafted <see cref="ProcessRunResult"/> values).
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> and captures its output.</summary>
    ProcessRunResult Run(string fileName, string arguments);
}
