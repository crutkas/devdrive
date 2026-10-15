using DevDriveCore.Models;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One card in the Overview room's "Opportunities" list.
/// </summary>
/// <remarks>
/// A thin wrapper over <see cref="AttentionSignal"/> that adds only what XAML needs and the model has
/// no business knowing: an automation id, an icon and the action button's wording. The ranking and
/// the wording of the finding stay in <c>DevDriveCore</c>, where they are tested without a UI thread.
/// </remarks>
public sealed class AttentionSignalViewModel(AttentionSignal signal)
{
    /// <summary>The signal this row shows.</summary>
    public AttentionSignal Signal { get; } = signal;

    public string Title => Signal.Title;

    public string Detail => Signal.Detail;

    public string Where => Signal.Where;

    public string Impact => Signal.Impact;

    public string RoomLabel => Signal.RoomLabel;

    public string RoomTag => Signal.RoomTag;

    /// <summary>
    /// The impact figure beside the action button. Only shown when it is a size — "not set up"
    /// beside a "Create a Dev Drive" button says the same thing twice.
    /// </summary>
    public bool HasImpactSize => Signal.ImpactBytes > 0;

    /// <summary>What pressing the card's button does, in the words of the room that does it.</summary>
    public string ActionText => Signal.Id switch
    {
        "NoDevDrive" => "Create a Dev Drive",
        "Reclaimable" => "Review",
        "NoSpaceScan" => "Scan",
        _ when Signal.Id.StartsWith("LowSpace_", StringComparison.Ordinal) => "Free up space",
        _ when Signal.RoomTag == "caches" => "Move caches",
        _ => $"Open {Signal.RoomLabel}",
    };

    /// <summary>The card's icon: what the opportunity is about, rather than how urgent it is.</summary>
    public string HeaderGlyph => Signal.Id switch
    {
        "NoDevDrive" => "\uEC7A",
        "Reclaimable" => "\uEA99",
        _ when Signal.Id.StartsWith("LowSpace_", StringComparison.Ordinal) => "\uEDA2",
        _ when Signal.RoomTag == "caches" => "\uE7B8",
        _ => Glyph,
    };

    /// <summary>Style key for the leading dot, and for the impact figure when it is a gain.</summary>
    public string KindKey => Signal.Kind switch
    {
        SignalKind.Gain => "gain",
        SignalKind.Warn => "warn",
        SignalKind.Bad => "bad",
        _ => "info",
    };

    /// <summary>
    /// Segoe Fluent glyph inside the dot. Colour alone cannot carry the difference between "you gain
    /// something" and "something is wrong" — in high contrast both dots are the same ink.
    /// </summary>
    public string Glyph => Signal.Kind switch
    {
        SignalKind.Gain => "\uE73E",
        SignalKind.Warn => "\uE7BA",
        SignalKind.Bad => "\uEA39",
        _ => "\uE946",
    };

    public string AutomationId => $"Signal_{Signal.Id}";

    /// <summary>The action button's own id, so a test can press it rather than the card it sits in.</summary>
    public string RoomButtonAutomationId => $"SignalRoom_{Signal.Id}";

    /// <summary>
    /// The button's label is short. Spelled out here so a screen reader announces what pressing it
    /// does and where it goes, not just the verb.
    /// </summary>
    public string RoomButtonAutomationName => $"{ActionText}: open {Signal.RoomLabel} to deal with {Signal.Title}";

    /// <summary>What a screen reader reads for the whole card, in the order a sighted user reads it.</summary>
    public string AutomationName =>
        $"{Signal.Title}. {Signal.Detail}. {Signal.Where}. {Signal.Impact}. Goes to {Signal.RoomLabel}.";
}
