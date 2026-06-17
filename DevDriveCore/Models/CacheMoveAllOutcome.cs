namespace DevDriveCore.Models;

/// <summary>
/// Combined result of a "Move all" run — a continue-on-error sweep of several
/// <see cref="CacheMoveOutcome"/>s. Pure data: <see cref="From"/> aggregates the per-cache outcomes
/// into counts, a total size, the list of tools that failed, and a ready-to-show
/// <see cref="CombinedText"/>.
/// </summary>
public sealed record CacheMoveAllOutcome
{
    /// <summary>Number of caches actually moved this run.</summary>
    public int MovedCount { get; init; }

    /// <summary>Number that were already on the Dev Drive (idempotent no-ops).</summary>
    public int AlreadyCount { get; init; }

    /// <summary>Number that failed or were cancelled.</summary>
    public int FailedCount { get; init; }

    /// <summary>Total bytes copied across the moved caches.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Display names of the tools whose move failed/cancelled.</summary>
    public IReadOnlyList<string> FailedTools { get; init; } = Array.Empty<string>();

    /// <summary>Ready-to-show, human-readable combined result line.</summary>
    public string CombinedText { get; init; } = string.Empty;

    /// <summary>True when at least one cache failed.</summary>
    public bool HasFailures => FailedCount > 0;

    /// <summary>Aggregates per-cache outcomes into a combined result (continue-on-error semantics).</summary>
    public static CacheMoveAllOutcome From(IReadOnlyList<CacheMoveOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        int moved = 0, already = 0, failed = 0;
        long bytes = 0;
        var failedTools = new List<string>();

        foreach (CacheMoveOutcome outcome in outcomes)
        {
            switch (outcome.Status)
            {
                case CacheMoveStatus.Moved:
                    moved++;
                    bytes += Math.Max(0L, outcome.BytesCopied);
                    break;
                case CacheMoveStatus.AlreadyOnDevDrive:
                    already++;
                    break;
                case CacheMoveStatus.Failed:
                case CacheMoveStatus.Cancelled:
                    failed++;
                    failedTools.Add(outcome.ToolName);
                    break;
            }
        }

        return new CacheMoveAllOutcome
        {
            MovedCount = moved,
            AlreadyCount = already,
            FailedCount = failed,
            TotalBytes = bytes,
            FailedTools = failedTools,
            CombinedText = BuildText(moved, already, failed, bytes, failedTools),
        };
    }

    private static string BuildText(int moved, int already, int failed, long bytes, IReadOnlyList<string> failedTools)
    {
        var parts = new List<string>(3);

        parts.Add(moved switch
        {
            0 => "No caches were moved",
            1 => $"Moved 1 cache ({ByteSizeFormatter.Format((ulong)bytes)}) to your Dev Drive",
            _ => $"Moved {moved} caches ({ByteSizeFormatter.Format((ulong)bytes)}) to your Dev Drive",
        });

        if (already > 0)
        {
            parts.Add(already == 1 ? "1 was already there" : $"{already} were already there");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed: {string.Join(", ", failedTools)}");
        }

        return string.Join(" \u00B7 ", parts) + ".";
    }
}
