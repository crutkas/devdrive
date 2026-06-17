namespace DevDriveCore.Models;

/// <summary>
/// Outcome of a <see cref="DevDriveCore.Services.PackageCacheMover"/> run. Captures what happened
/// (and the prior environment value) so a caller can report it or revert. Pure data.
/// </summary>
public sealed record PackageCacheMoveReceipt
{
    /// <summary>True when the move completed successfully (including the idempotent no-op case).</summary>
    public bool Success { get; init; }

    /// <summary>True when nothing needed to be done because the cache was already on the Dev Drive.</summary>
    public bool AlreadyOnDevDrive { get; init; }

    /// <summary>Tool whose cache was moved.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Source the cache was copied from.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Target the cache was copied to (on the Dev Drive).</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>The per-user environment variable that was set.</summary>
    public string EnvironmentVariable { get; init; } = string.Empty;

    /// <summary>True when the variable had a value before the move.</summary>
    public bool PriorEnvironmentValueWasSet { get; init; }

    /// <summary>The variable's value before the move (<c>null</c> when it was unset).</summary>
    public string? PriorEnvironmentValue { get; init; }

    /// <summary>Number of files copied and verified.</summary>
    public int FilesCopied { get; init; }

    /// <summary>Total bytes copied.</summary>
    public long BytesCopied { get; init; }

    /// <summary>True when the source was deleted after the copy verified.</summary>
    public bool SourceDeleted { get; init; }

    /// <summary><see cref="ReversibilityEntry.Id"/> of the entry recorded for this move (empty for a no-op).</summary>
    public string ReversibilityId { get; init; } = string.Empty;
}
