using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DevDriveManager.Controls;

/// <summary>How a treemap cell is painted, independent of its colour slot.</summary>
internal enum TreemapLook
{
    Container,
    FolderLeaf,
    FileLeaf,
    Overflow,
    Unlisted,
}

/// <summary>
/// The raw colours a treemap draws with, read from the pane's own theme resources so they follow
/// the pane's theme rather than the application's.
/// </summary>
internal sealed record TreemapColors(
    IReadOnlyList<Color> Hues,
    Color TextPrimary,
    Color TextSecondary,
    Color FocusInner,
    bool IsLight,
    bool IsHighContrast,
    Color Window,
    Color WindowText,
    Color Highlight,
    Color GrayText,
    Color Hotlight);

/// <summary>
/// Composition and XAML brushes derived from one <see cref="TreemapColors"/>. Brushes are shared by
/// every cell of the same slot and look, so a map of two thousand cells still holds a few dozen.
/// </summary>
internal sealed class TreemapTheme : IDisposable
{
    private static readonly Color Black = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkInk = Color.FromArgb(0xFF, 0x10, 0x14, 0x18);
    private static readonly Color Transparent = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

    private readonly Compositor _compositor;
    private readonly Dictionary<(int Slot, TreemapLook Look), CompositionBrush> _bodies = [];
    private readonly Dictionary<int, CompositionBrush> _headers = [];
    private readonly Dictionary<(int Slot, TreemapLook Look, bool Secondary), SolidColorBrush> _inks = [];
    private readonly Dictionary<int, SolidColorBrush> _accents = [];
    private readonly List<CompositionObject> _owned = [];

    public TreemapTheme(Compositor compositor, TreemapColors colors)
    {
        _compositor = compositor;
        Colors = colors;
        bool contrast = colors.IsHighContrast;
        HoverStroke = Own(compositor.CreateColorBrush(
            contrast ? colors.Hotlight : WithAlpha(colors.TextPrimary, 0xA6)));
        HoverFill = Own(compositor.CreateColorBrush(
            contrast ? Transparent : WithAlpha(White, colors.IsLight ? (byte)0x38 : (byte)0x26)));
        SelectionOuter = Own(compositor.CreateColorBrush(contrast ? colors.Highlight : colors.TextPrimary));
        SelectionInner = Own(compositor.CreateColorBrush(contrast ? colors.Window : colors.FocusInner));
        Inner = contrast ? Own(compositor.CreateColorBrush(colors.Window)) : null;
    }

    public TreemapColors Colors { get; }

    public bool IsHighContrast => Colors.IsHighContrast;

    public float HoverStrokeThickness => IsHighContrast ? 2f : 1.5f;

    public CompositionColorBrush HoverStroke { get; }

    public CompositionColorBrush HoverFill { get; }

    public CompositionColorBrush SelectionOuter { get; }

    public CompositionColorBrush SelectionInner { get; }

    /// <summary>High contrast only: the window-coloured face drawn inside each cell's one-pixel frame.</summary>
    public CompositionColorBrush? Inner { get; }

    public static bool SameAs(TreemapColors? left, TreemapColors right) =>
        left is not null &&
        left.Hues.SequenceEqual(right.Hues) &&
        left.TextPrimary == right.TextPrimary &&
        left.TextSecondary == right.TextSecondary &&
        left.FocusInner == right.FocusInner &&
        left.IsLight == right.IsLight &&
        left.IsHighContrast == right.IsHighContrast &&
        left.Window == right.Window &&
        left.WindowText == right.WindowText &&
        left.Highlight == right.Highlight &&
        left.GrayText == right.GrayText &&
        left.Hotlight == right.Hotlight;

    public Color Hue(int slot) => Colors.Hues[Wrap(slot)];

    public CompositionBrush Body(int slot, TreemapLook look)
    {
        slot = Wrap(slot);
        if (_bodies.TryGetValue((slot, look), out CompositionBrush? cached))
        {
            return cached;
        }

        CompositionBrush brush;
        if (IsHighContrast)
        {
            // In high contrast every cell is a one-pixel frame in its category colour around a
            // window-coloured face; the face is a child sprite inset by the frame.
            brush = Own(_compositor.CreateColorBrush(
                look is TreemapLook.Overflow or TreemapLook.Unlisted ? Colors.GrayText : Hue(slot)));
        }
        else
        {
            Color hue = Hue(slot);
            Color neutral = Colors.IsLight ? Black : White;
            brush = look switch
            {
                TreemapLook.Container => Own(_compositor.CreateColorBrush(
                    WithAlpha(hue, Colors.IsLight ? (byte)0x14 : (byte)0x1F))),
                TreemapLook.Overflow => Own(_compositor.CreateColorBrush(
                    WithAlpha(neutral, Colors.IsLight ? (byte)0x14 : (byte)0x21))),
                TreemapLook.Unlisted => Own(_compositor.CreateColorBrush(
                    WithAlpha(neutral, Colors.IsLight ? (byte)0x0A : (byte)0x12))),
                _ => LeafGradient(LeafBase(hue, look)),
            };
        }

        _bodies[(slot, look)] = brush;
        return brush;
    }

    /// <summary>The title strip across the top of an open folder. Not drawn in high contrast.</summary>
    public CompositionBrush Header(int slot)
    {
        slot = Wrap(slot);
        if (!_headers.TryGetValue(slot, out CompositionBrush? brush))
        {
            brush = Own(_compositor.CreateColorBrush(
                WithAlpha(Hue(slot), Colors.IsLight ? (byte)0x26 : (byte)0x33)));
            _headers[slot] = brush;
        }

        return brush;
    }

    /// <summary>Text colour for a label drawn on a cell of this slot and look.</summary>
    public SolidColorBrush Ink(int slot, TreemapLook look, bool secondary)
    {
        slot = Wrap(slot);
        if (_inks.TryGetValue((slot, look, secondary), out SolidColorBrush? cached))
        {
            return cached;
        }

        Color color;
        if (IsHighContrast)
        {
            color = look is TreemapLook.Overflow or TreemapLook.Unlisted ? Colors.GrayText : Colors.WindowText;
        }
        else if (look == TreemapLook.Container)
        {
            color = secondary ? Colors.TextSecondary : Colors.TextPrimary;
        }
        else if (look is TreemapLook.Overflow or TreemapLook.Unlisted)
        {
            color = Colors.TextSecondary;
        }
        else
        {
            // Pick whichever ink reads better on this fill, rather than one ink per theme: the
            // pastel dark-theme hues want dark text, the saturated light-theme hues want white.
            double fill = Luminance(LeafBase(Hue(slot), look));
            Color ink = Contrast(fill, Luminance(DarkInk)) >= Contrast(fill, 1) ? DarkInk : White;
            color = secondary ? WithAlpha(ink, 0xB8) : ink;
        }

        var brush = new SolidColorBrush(color);
        _inks[(slot, look, secondary)] = brush;
        return brush;
    }

    /// <summary>The slot's colour as a XAML brush, for the info card's glyph and share bar.</summary>
    public SolidColorBrush Accent(int slot)
    {
        slot = Wrap(slot);
        if (!_accents.TryGetValue(slot, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(IsHighContrast ? Colors.WindowText : Hue(slot));
            _accents[slot] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Drops the theme's references without closing the brushes: cells being torn down may still be
    /// painted with them, and closing a composition object the compositor still uses frees it under
    /// its .NET wrapper, which the GC finalizer later releases again and crashes. See
    /// <c>TreemapPane.ReleaseAnimationTemplates</c> for the same rule.
    /// </summary>
    public void Dispose()
    {
        _owned.Clear();
        _bodies.Clear();
        _headers.Clear();
    }

    private CompositionBrush LeafGradient(Color fill)
    {
        CompositionLinearGradientBrush brush = Own(_compositor.CreateLinearGradientBrush());
        brush.StartPoint = Vector2.Zero;
        brush.EndPoint = new Vector2(0, 1);
        brush.ColorStops.Add(Own(_compositor.CreateColorGradientStop(
            0, Lerp(fill, White, Colors.IsLight ? 0.08 : 0.10))));
        brush.ColorStops.Add(Own(_compositor.CreateColorGradientStop(
            1, Lerp(fill, Black, Colors.IsLight ? 0.05 : 0.06))));
        return brush;
    }

    private Color LeafBase(Color hue, TreemapLook look) => look == TreemapLook.FolderLeaf
        ? Lerp(hue, Black, Colors.IsLight ? 0.14 : 0.16)
        : hue;

    private T Own<T>(T value)
        where T : CompositionObject
    {
        _owned.Add(value);
        return value;
    }

    private int Wrap(int slot) => ((slot % Colors.Hues.Count) + Colors.Hues.Count) % Colors.Hues.Count;

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Lerp(Color from, Color to, double amount) => Color.FromArgb(
        Mix(from.A, to.A, amount),
        Mix(from.R, to.R, amount),
        Mix(from.G, to.G, amount),
        Mix(from.B, to.B, amount));

    private static byte Mix(byte from, byte to, double amount) =>
        (byte)Math.Round(from + ((to - from) * amount));

    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double Contrast(double left, double right) =>
        (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
}
