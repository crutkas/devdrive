using DevDriveReclaim;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Byte formatting for reclaim rows.
/// </summary>
/// <remarks>
/// Sizes are <see cref="long"/> throughout the reclaim subsystem while
/// <see cref="DevDriveCore.ByteSizeFormatter"/> takes <see cref="ulong"/>. Clamping at zero here
/// keeps that conversion in one place rather than scattering casts through the bindings, where a
/// stray negative would wrap to an absurd number instead of showing "0 B".
/// <para>
/// The formatter is named in full because <c>DevDriveStorage</c> has one too, and the two disagree:
/// storage counts in powers of 1000, core in powers of 1024. Reclaim totals and the volume bars sit
/// side by side in this room, so they must come from the same one.
/// </para>
/// </remarks>
internal static class ReclaimFormat
{
    public static string Bytes(long value) =>
        DevDriveCore.ByteSizeFormatter.Format(value <= 0 ? 0UL : (ulong)value);
}

/// <summary>
/// The two lines the Reclaim room says about itself: how a scan went, and how a run went.
/// </summary>
/// <remarks>
/// Lives beside <see cref="ReclaimConfirmationRequest"/>, free of any XAML dependency, for the same
/// reason: these sentences are the only account the user gets of a destructive operation, so they
/// are behaviour rather than copy. The distinctions they draw — cancelled against failed, a floor
/// against a total, "nothing else was touched" against a run that touched something and failed —
/// are exactly the kind that survive a rewrite by looking right and being wrong.
/// </remarks>
internal static class ReclaimNarration
{
    /// <summary>
    /// One line for how the scan went. Cancelled and failed are worded apart on purpose: a category
    /// that failed hit something the app could not handle, one that was cancelled was simply not
    /// reached, and calling the user's own Cancel a failure is both wrong and alarming. Either way
    /// the total is announced as a floor, because a partial answer presented as a complete one is
    /// how someone concludes there is nothing left to reclaim when there is.
    /// </summary>
    public static string DescribeScan(ReclaimResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string found = ReclaimFormat.Bytes(result.TotalBytes);
        int cancelled = result.CancelledCategories.Length;
        int failed = result.FailedCategories.Length - cancelled;

        if (cancelled > 0 && result.Categories.All(c => !c.Succeeded))
        {
            return "Cancelled before anything finished.";
        }

        List<string> caveats = [];
        if (cancelled > 0)
        {
            caveats.Add($"{Categories(cancelled)} not reached");
        }

        if (failed > 0)
        {
            caveats.Add($"{Categories(failed)} could not be checked");
        }

        if (caveats.Count == 0)
        {
            return $"Found {found}.";
        }

        string prefix = cancelled > 0 ? "Cancelled." : string.Empty;
        return $"{prefix} Found {found} — {string.Join(" and ", caveats)}, so this is a floor."
            .TrimStart();

        static string Categories(int count) => count == 1 ? "1 category" : $"{count} categories";
    }

    /// <summary>One line for how a reclaim run went.</summary>
    /// <remarks>
    /// Freed and recycled are reported apart. Recycling moves an item into the same volume's
    /// Recycle Bin, so it frees nothing until the bin is emptied, and a receipt that called it
    /// "freed" promised space the drive never got back.
    /// </remarks>
    public static string SummarizeRun(ReclaimOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        long freed = outcome.BytesFreed;
        long recycled = outcome.BytesRecycled;
        string items = outcome.RemovedCount == 1 ? "1 item" : $"{outcome.RemovedCount:N0} items";
        string failed = outcome.FailedCount == 1 ? "1 item" : $"{outcome.FailedCount:N0} items";
        string binNote = recycled > 0 ? " " + RecycledSpaceNote : string.Empty;

        if (outcome.Cancelled)
        {
            // "Nothing else was touched" is only true of what was never reached. Items that were
            // reached and refused were very much touched — one of them can even be a shell delete
            // that aborted partway — so saying it unconditionally is the exact over-promise this
            // room's copy is written to avoid, and it contradicts the failure banner shown beside it.
            return outcome.FailedCount == 0
                ? $"Stopped after {Gerund(freed, recycled)} across {items}. Nothing else was touched.{binNote}"
                : $"Stopped after {Gerund(freed, recycled)} across {items}. {failed} could not be removed " +
                  $"and is still listed. Nothing after that was touched.{binNote}";
        }

        if (outcome.FailedCount == 0)
        {
            return outcome.RemovedCount == 0
                ? "Nothing was removed."
                : $"{Past(freed, recycled)} across {items}.{binNote}";
        }

        return $"{Past(freed, recycled)} across {items}. {failed} could not be removed and is still listed.{binNote}";
    }

    /// <summary>The progress line while a run is removing things.</summary>
    public static string DescribeProgress(ReclaimExecutionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (progress.Total <= 1)
        {
            return $"Removing {progress.CurrentItem}…";
        }

        List<string> parts = [];
        if (progress.BytesFreedSoFar > 0)
        {
            parts.Add($"{ReclaimFormat.Bytes(progress.BytesFreedSoFar)} freed");
        }

        if (progress.BytesRecycledSoFar > 0)
        {
            parts.Add($"{ReclaimFormat.Bytes(progress.BytesRecycledSoFar)} to the Recycle Bin");
        }

        string counted = $"{progress.Completed:N0} of {progress.Total:N0}";
        return parts.Count == 0 ? counted : $"{counted} — {string.Join(" · ", parts)}";
    }

    /// <summary>Why recycled bytes are not in the freed total.</summary>
    public const string RecycledSpaceNote = "Recycled items still use space until the Recycle Bin is emptied.";

    private static string Past(long freed, long recycled) => (freed > 0, recycled > 0) switch
    {
        (true, true) => $"Freed {ReclaimFormat.Bytes(freed)} and moved {ReclaimFormat.Bytes(recycled)} to the Recycle Bin",
        (false, true) => $"Moved {ReclaimFormat.Bytes(recycled)} to the Recycle Bin",
        _ => $"Freed {ReclaimFormat.Bytes(freed)}",
    };

    private static string Gerund(long freed, long recycled) => (freed > 0, recycled > 0) switch
    {
        (true, true) => $"freeing {ReclaimFormat.Bytes(freed)} and moving {ReclaimFormat.Bytes(recycled)} to the Recycle Bin",
        (false, true) => $"moving {ReclaimFormat.Bytes(recycled)} to the Recycle Bin",
        _ => $"freeing {ReclaimFormat.Bytes(freed)}",
    };
}

/// <summary>
/// Everything the confirmation must state, in the order it should be stated: what is about to
/// happen, how much of it cannot be undone, and how bad the worst thing in the pile is.
/// </summary>
/// <remarks>
/// This is the last screen before data stops existing, so its wording is treated as behaviour rather
/// than as copy, and lives here — free of any XAML dependency — so it can be asserted headlessly.
/// A dialog whose text is only checked by looking at it is a dialog whose text will eventually be
/// wrong about whether something is recoverable.
/// <para>
/// A record rather than a handful of arguments so that adding a fact the dialog must mention is a
/// compile error at the one place that builds it, not a fact quietly missing from the last screen
/// before a permanent delete.
/// </para>
/// </remarks>
public sealed record ReclaimConfirmationRequest(
    int ItemCount,
    long Bytes,
    ReclaimRisk HighestRisk,
    int CarefulCount,
    int PermanentCount,
    long PermanentBytes,
    IReadOnlyList<string> PermanentNames,
    string VolumeSplitText)
{
    /// <summary>
    /// The request for a plan. The plan has already moved anything the Recycle Bin would refuse into
    /// the permanent group, so the names listed here are everything that will not come back.
    /// </summary>
    public static ReclaimConfirmationRequest FromPlan(ReclaimPlan plan, string volumeSplitText)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<PlannedRemoval> permanent = [.. plan.Permanent];

        return new ReclaimConfirmationRequest(
            ItemCount: plan.Items.Count,
            Bytes: plan.TotalBytes,
            HighestRisk: plan.Items.Count == 0 ? ReclaimRisk.Safe : plan.Items.Max(i => i.Candidate.Risk),
            CarefulCount: plan.Items.Count(i => i.Candidate.Risk == ReclaimRisk.Careful),
            PermanentCount: permanent.Count,
            PermanentBytes: permanent.Sum(i => i.Candidate.SizeBytes),
            PermanentNames: [.. permanent
                .OrderByDescending(i => i.Candidate.SizeBytes)
                .Take(MaxPermanentNames)
                .Select(i => i.PermanentLabel)],
            VolumeSplitText: volumeSplitText);
    }

    private const int MaxPermanentNames = 4;

    public string BytesText => ReclaimFormat.Bytes(Bytes);

    public string PermanentBytesText => ReclaimFormat.Bytes(PermanentBytes);

    /// <summary>Bytes going to the Recycle Bin — removed from view, not from the volume.</summary>
    public long RecycledBytes => Math.Max(0, Bytes - PermanentBytes);

    public string RecycledBytesText => ReclaimFormat.Bytes(RecycledBytes);

    public string ItemCountText => ItemCount == 1 ? "1 item" : $"{ItemCount:N0} items";

    /// <summary>
    /// True when the pile contains something that can destroy work existing nowhere else. Gates the
    /// typed confirmation: a tier described as "can destroy work" and then dismissed with the same
    /// reflexive click as an empty build folder is not really a tier.
    /// </summary>
    public bool RequiresTypedConfirmation => HighestRisk == ReclaimRisk.Careful;

    /// <summary>True when some of this will not be recoverable from the Recycle Bin.</summary>
    public bool HasPermanent => PermanentCount > 0;

    private bool AllPermanent => HasPermanent && PermanentCount >= ItemCount;

    /// <summary>
    /// The headline. Leads with the irreversible part when there is one, because that is the fact
    /// that should change someone's mind, and a headline leading with the amount freed is an
    /// advertisement rather than a confirmation. A batch that only goes to the Recycle Bin does not
    /// say "free" at all: moving something into the same volume's bin frees nothing yet.
    /// </summary>
    public string Headline => !HasPermanent
        ? $"Move {ItemCountText} ({BytesText}) to the Recycle Bin"
        : AllPermanent
            ? $"Permanently delete {ItemCountText} and free {BytesText}"
            : $"Remove {ItemCountText} ({BytesText}) — {PermanentBytesText} of it permanently";

    /// <summary>
    /// Whether this can be undone, and for the mixed case, which parts cannot. Names the permanent
    /// items rather than counting them: "3 of these cannot go to the Recycle Bin" tells the reader
    /// there is a decision without telling them enough to make it.
    /// </summary>
    /// <remarks>
    /// The all-recyclable promise is only made because it is kept: the plan has already moved
    /// anything the bin would refuse into the permanent list, and the executor keeps — rather than
    /// deletes — any item that the bin refuses later anyway.
    /// </remarks>
    public string RecoveryText => HasPermanent
        ? AllPermanent
            ? "None of this goes to the Recycle Bin. It cannot be undone."
            : $"{PermanentCount:N0} of these cannot go to the Recycle Bin: " +
              string.Join(", ", PermanentNames) +
              (PermanentCount > PermanentNames.Count ? $", and {PermanentCount - PermanentNames.Count:N0} more" : string.Empty) +
              ". Everything else can be restored from the bin."
        : "Everything here goes to the Recycle Bin, so it can be restored. Nothing is deleted " +
          "permanently without asking you first.";

    /// <summary>
    /// When the space actually comes back. Recycling moves bytes into a bin on the same volume, so
    /// only the permanent part is free the moment the run ends.
    /// </summary>
    public string SpaceText => !HasPermanent
        ? $"This doesn't free space yet — the {BytesText} comes back when the Recycle Bin is emptied."
        : AllPermanent
            ? $"{BytesText} is freed as soon as this finishes."
            : $"{PermanentBytesText} is freed now. The other {RecycledBytesText} comes back when the " +
              "Recycle Bin is emptied.";

    /// <summary>
    /// The worst thing in the pile, stated as a consequence rather than as a tier name. "Careful"
    /// means nothing to someone who has not read the risk model; "can destroy work that exists
    /// nowhere else" needs no glossary.
    /// </summary>
    public string RiskText => HighestRisk switch
    {
        ReclaimRisk.Safe => "Everything selected is regenerable — nothing here is the only copy.",
        ReclaimRisk.Check => "Some of this is worth a glance first, but none of it is the only copy.",
        _ => CarefulCount == 1
            ? "1 item can destroy work that exists nowhere else."
            : $"{CarefulCount:N0} items can destroy work that exists nowhere else.",
    };
}
