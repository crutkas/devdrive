using DevDriveCore.Services;

namespace DevDriveCore.Models;

/// <summary>Coarse status of a single package-cache move/revert, projected from a <see cref="PackageCacheMoveReceipt"/>.</summary>
public enum CacheMoveStatus
{
    /// <summary>The cache was copied to the Dev Drive and the environment variable was set.</summary>
    Moved,

    /// <summary>Nothing to do — the variable already pointed at the Dev Drive (idempotent no-op).</summary>
    AlreadyOnDevDrive,

    /// <summary>A prior move was reverted — the environment variable was restored and the copy removed.</summary>
    MovedBack,

    /// <summary>Revert was requested but no reversible move was recorded for this variable.</summary>
    NothingToRevert,

    /// <summary>The per-user environment variable was pointed at a user-chosen path (no files copied), reversibly.</summary>
    Mapped,

    /// <summary>The operation failed (the engine already rolled back any partial copy).</summary>
    Failed,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// UI-facing outcome of a single package-cache move or move-back, produced by
/// <see cref="PackageCacheMoveCoordinator"/>. Pure data: it carries the coarse
/// <see cref="Status"/>, the numbers, and the ready-to-show <see cref="ResultText"/> so the ViewModel
/// only has to bind it.
/// </summary>
public sealed record CacheMoveOutcome
{
    /// <summary>Coarse status.</summary>
    public CacheMoveStatus Status { get; init; }

    /// <summary>Tool whose cache the operation targeted.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Dev Drive target the cache was (or is) at.</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>The per-user environment variable involved.</summary>
    public string EnvironmentVariable { get; init; } = string.Empty;

    /// <summary>Bytes copied (0 for a no-op / revert).</summary>
    public long BytesCopied { get; init; }

    /// <summary>Files copied (0 for a no-op / revert).</summary>
    public int FilesCopied { get; init; }

    /// <summary>Reversibility id recorded for the move (empty when none).</summary>
    public string ReversibilityId { get; init; } = string.Empty;

    /// <summary>Error detail when <see cref="Status"/> is <see cref="CacheMoveStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Ready-to-show, human-readable result line.</summary>
    public string ResultText { get; init; } = string.Empty;

    /// <summary>True when the operation succeeded (moved, mapped, already there, reverted, or nothing to revert).</summary>
    public bool Succeeded =>
        Status is CacheMoveStatus.Moved or CacheMoveStatus.AlreadyOnDevDrive
            or CacheMoveStatus.MovedBack or CacheMoveStatus.NothingToRevert or CacheMoveStatus.Mapped;

    /// <summary>True when the cache is now on the Dev Drive (so the row should show "On Dev Drive").</summary>
    public bool IsOnDevDrive => Status is CacheMoveStatus.Moved or CacheMoveStatus.AlreadyOnDevDrive;

    /// <summary>True when a "Move back" affordance should be offered after this outcome.</summary>
    public bool CanMoveBack => Status is CacheMoveStatus.Moved or CacheMoveStatus.AlreadyOnDevDrive
        or CacheMoveStatus.Mapped;

    // ---- Factories -----------------------------------------------------------------------------

    /// <summary>Projects a move receipt into a UI outcome, building the "Moved …"/"Already on …" copy.</summary>
    public static CacheMoveOutcome FromReceipt(PackageCacheMoveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (receipt.AlreadyOnDevDrive)
        {
            return new CacheMoveOutcome
            {
                Status = CacheMoveStatus.AlreadyOnDevDrive,
                ToolName = receipt.ToolName,
                TargetPath = receipt.TargetPath,
                EnvironmentVariable = receipt.EnvironmentVariable,
                ReversibilityId = PackageCacheMover.ReversibilityId(receipt.EnvironmentVariable),
                ResultText = $"On your Dev Drive at {receipt.TargetPath} \u2014 {receipt.EnvironmentVariable} is set.",
            };
        }

        string size = ByteSizeFormatter.Format((ulong)Math.Max(0L, receipt.BytesCopied));
        return new CacheMoveOutcome
        {
            Status = CacheMoveStatus.Moved,
            ToolName = receipt.ToolName,
            TargetPath = receipt.TargetPath,
            EnvironmentVariable = receipt.EnvironmentVariable,
            BytesCopied = receipt.BytesCopied,
            FilesCopied = receipt.FilesCopied,
            ReversibilityId = receipt.ReversibilityId,
            ResultText = $"Moved {size} to {receipt.TargetPath}; {receipt.EnvironmentVariable} set.",
        };
    }

    /// <summary>A successful move-back (env var restored, copy removed).</summary>
    public static CacheMoveOutcome MovedBack(string toolName, string environmentVariable) => new()
    {
        Status = CacheMoveStatus.MovedBack,
        ToolName = toolName,
        EnvironmentVariable = environmentVariable,
        ResultText = $"Moved back to your system drive \u2014 {environmentVariable} restored.",
    };

    /// <summary>Revert requested but nothing was recorded to revert.</summary>
    public static CacheMoveOutcome NothingToRevert(string toolName, string environmentVariable) => new()
    {
        Status = CacheMoveStatus.NothingToRevert,
        ToolName = toolName,
        EnvironmentVariable = environmentVariable,
        ResultText = "Nothing to move back \u2014 no recorded move for this cache.",
    };

    /// <summary>A successful "Map path": the variable was pointed at the user's chosen folder, reversibly.</summary>
    public static CacheMoveOutcome Mapped(PackageCacheMoveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new CacheMoveOutcome
        {
            Status = CacheMoveStatus.Mapped,
            ToolName = receipt.ToolName,
            TargetPath = receipt.TargetPath,
            EnvironmentVariable = receipt.EnvironmentVariable,
            ReversibilityId = receipt.ReversibilityId,
            ResultText = $"Mapped \u2014 {receipt.EnvironmentVariable} now points at {receipt.TargetPath}. "
                + "New shells use it; \u201CMove back\u201D undoes it.",
        };
    }

    /// <summary>A failed operation (the engine already rolled back any partial copy).</summary>
    public static CacheMoveOutcome Failed(string toolName, string error) => new()
    {
        Status = CacheMoveStatus.Failed,
        ToolName = toolName,
        ErrorMessage = error,
        ResultText = $"Couldn't complete \u2014 {error}",
    };

    /// <summary>A cancelled operation.</summary>
    public static CacheMoveOutcome Cancelled(string toolName) => new()
    {
        Status = CacheMoveStatus.Cancelled,
        ToolName = toolName,
        ResultText = "Cancelled \u2014 nothing changed.",
    };
}
