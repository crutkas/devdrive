using DevDriveCore.Models;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One row of the Overview room's "Needs attention" table.
/// </summary>
/// <remarks>
/// A thin wrapper over <see cref="AttentionSignal"/> that adds only what XAML needs and the model has
/// no business knowing: an automation id, a style key, and the glyph. The ranking and the wording
/// stay in <c>DevDriveCore</c>, where they are tested without a UI thread.
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

    /// <summary>The room chip's own id, so a test can press it rather than the row it sits in.</summary>
    public string RoomButtonAutomationId => $"SignalRoom_{Signal.Id}";

    /// <summary>
    /// The chip says only the room name, which on its own is not an instruction. Spelled out here so
    /// a screen reader announces what pressing it does, not just where it goes.
    /// </summary>
    public string RoomButtonAutomationName => $"Open {Signal.RoomLabel} to deal with {Signal.Title}";

    /// <summary>What a screen reader reads for the whole row, in the order a sighted user reads it.</summary>
    public string AutomationName =>
        $"{Signal.Title}. {Signal.Detail}. {Signal.Where}. {Signal.Impact}. Goes to {Signal.RoomLabel}.";
}
