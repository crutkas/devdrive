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
    public string BytesText => ReclaimFormat.Bytes(Bytes);

    public string PermanentBytesText => ReclaimFormat.Bytes(PermanentBytes);

    public string ItemCountText => ItemCount == 1 ? "1 item" : $"{ItemCount:N0} items";

    /// <summary>
    /// True when the pile contains something that can destroy work existing nowhere else. Gates the
    /// typed confirmation: a tier described as "can destroy work" and then dismissed with the same
    /// reflexive click as an empty build folder is not really a tier.
    /// </summary>
    public bool RequiresTypedConfirmation => HighestRisk == ReclaimRisk.Careful;

    /// <summary>True when some of this will not be recoverable from the Recycle Bin.</summary>
    public bool HasPermanent => PermanentCount > 0;

    /// <summary>
    /// The headline. Leads with the irreversible part when there is one, because that is the fact
    /// that should change someone's mind, and a headline leading with the amount freed is an
    /// advertisement rather than a confirmation.
    /// </summary>
    public string Headline => HasPermanent
        ? $"Remove {ItemCountText} and free {BytesText} — {PermanentBytesText} of it permanently"
        : $"Remove {ItemCountText} and free {BytesText}";

    /// <summary>
    /// Whether this can be undone, and for the mixed case, which parts cannot. Names the permanent
    /// items rather than counting them: "3 of these cannot go to the Recycle Bin" tells the reader
    /// there is a decision without telling them enough to make it.
    /// </summary>
    public string RecoveryText => HasPermanent
        ? PermanentCount == ItemCount
            ? "None of this goes to the Recycle Bin. It cannot be undone."
            : $"{PermanentCount:N0} of these cannot go to the Recycle Bin: " +
              string.Join(", ", PermanentNames) + ". Everything else can be restored from the bin."
        : "Everything here goes to the Recycle Bin, so it can be restored.";

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
