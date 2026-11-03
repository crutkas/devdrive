using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Controls;

/// <summary>How strongly a status fact should read.</summary>
public enum StatusEmphasis
{
    /// <summary>Ordinary reporting.</summary>
    None,

    /// <summary>Something went well, or is healthy.</summary>
    Good,

    /// <summary>Something needs a look, but nothing is broken.</summary>
    Warn,

    /// <summary>Something failed.</summary>
    Bad,
}

/// <summary>One fact in the status bar.</summary>
/// <remarks>
/// A class with get-only properties rather than a record: the XAML type-info generator emits a
/// setter for every property it can see on an <c>x:DataType</c>, and a positional record's
/// <c>init</c> accessors fail that generated assignment at compile time.
/// </remarks>
/// <param name="text">The whole fact, already phrased as a sentence fragment.</param>
/// <param name="emphasis">
/// How it should read. This is a meaning, not a colour — the bar picks the colour, so a fact never
/// carries a brush and colour is never the only signal, since emphasised facts also change weight.
/// </param>
public sealed class StatusFact(string text, StatusEmphasis emphasis = StatusEmphasis.None)
{
    public string Text { get; } = text;

    public StatusEmphasis Emphasis { get; } = emphasis;
}

/// <summary>
/// The bar every room ends with: a few live facts on the left, keyboard hints on the right.
/// </summary>
/// <remarks>
/// Facts are a collection rather than a fixed set of slots because rooms genuinely differ — Reclaim
/// ends on found/selected/tier counts, Space on coverage and scan time. Fixed slots would force
/// every room into the shape of whichever room was written first.
/// </remarks>
public sealed partial class RoomStatusBar : UserControl
{
    public RoomStatusBar() => InitializeComponent();

    /// <summary>
    /// The facts, left to right. Never reassigned, so bindings survive updates — callers should
    /// clear and refill rather than replace.
    /// </summary>
    public ObservableCollection<StatusFact> Facts { get; } = [];

    /// <summary>Right-aligned keyboard hints, e.g. "Space select · Enter open".</summary>
    public string Hints
    {
        get => (string)GetValue(HintsProperty);
        set => SetValue(HintsProperty, value);
    }

    public static readonly DependencyProperty HintsProperty = DependencyProperty.Register(
        nameof(Hints), typeof(string), typeof(RoomStatusBar), new PropertyMetadata(string.Empty));
}
