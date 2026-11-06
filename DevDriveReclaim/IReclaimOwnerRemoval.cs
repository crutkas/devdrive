namespace DevDriveReclaim;

/// <summary>
/// Removes the candidates that have a <see cref="ReclaimCandidate.Owner"/>: through the tool that owns them, never as a plain
/// path delete, so the tool's own records stay right and files other candidates share stay put.
/// </summary>
/// <remarks>
/// <see cref="ReclaimExecutor"/> hands an owned candidate here after <see cref="ReclaimPathGuard"/> has let it through. A
/// dry-run executor asks <see cref="WhyKept"/> and <see cref="Describe"/> only, which read and never change anything.
/// </remarks>
public interface IReclaimOwnerRemoval
{
    /// <summary>
    /// Why a run would keep <paramref name="candidate"/> right now (it's in use, its tool is missing, it changed since the
    /// scan), or null when it would go. Reads only.
    /// </summary>
    string? WhyKept(ReclaimCandidate candidate);

    /// <summary>The command removing it runs (<c>ollama rm qwen3:0.6b</c>), or null when it's removed by deleting its files.</summary>
    string? Describe(ReclaimCandidate candidate);

    /// <summary>Removes it. Asks <see cref="WhyKept"/> again first, and keeps it when there's a reason to.</summary>
    ReclaimItemOutcome Remove(ReclaimCandidate candidate, CancellationToken cancellationToken);
}
