using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace DevDriveManager.Controls;

/// <summary>
/// The segmented capacity bar behind every volume in the context strip.
/// </summary>
/// <remarks>
/// Columns are proportional (star-sized) rather than pixel-measured, so the bar stays correct when
/// the strip is resized and never needs a layout pass to recompute. The geometry itself comes from
/// <see cref="VolumeCapacity"/> in the storage library, which is where it is unit tested; this class
/// only paints what it is handed.
/// <para>
/// Free space is drawn as the track, not as a segment. Painting "free" as a band would make an empty
/// volume look as busy as a full one.
/// </para>
/// </remarks>
public sealed partial class CapacityBar : Control
{
    private Grid? _root;
    private Border? _track;

    public CapacityBar()
    {
        DefaultStyleKey = typeof(CapacityBar);
        IsTabStop = false;

        // Every band is an imperatively created Rectangle with a resolved Fill, so nothing on this
        // bar re-resolves on its own. Without this the bar keeps its old-theme colours through a
        // light/dark or high-contrast switch -- and high contrast is precisely the case where a
        // stale palette stops being cosmetic.
        ActualThemeChanged += (_, _) => Rebuild();

        // The radius can only be resolved once the bar has been measured, and it has to be redone
        // whenever either the request or the size changes.
        SizeChanged += (_, _) => ApplyCornerRadius();
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => ApplyCornerRadius());
    }

    /// <summary>The bands to draw.</summary>
    public VolumeCapacityResult Capacity
    {
        get => (VolumeCapacityResult)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity),
        typeof(VolumeCapacityResult),
        typeof(CapacityBar),
        new PropertyMetadata(default(VolumeCapacityResult), OnCapacityChanged));

    /// <summary>
    /// Which colour family the in-use band uses. Set per volume so C: and G: are told apart at a
    /// glance, which is the whole reason the strip shows two bars rather than one number.
    /// </summary>
    /// <remarks>
    /// This is deliberately the <i>role</i>, not a <see cref="Brush"/>. A brush handed in from
    /// outside is resolved once against whichever theme was live at the time and can never
    /// re-resolve, so the bar kept its old-theme colours across a light/dark or high-contrast
    /// switch. The role is theme-independent; this control turns it into a colour, and re-runs that
    /// translation on <see cref="FrameworkElement.ActualThemeChanged"/>.
    /// </remarks>
    public VolumeAccentRole AccentRole
    {
        get => (VolumeAccentRole)GetValue(AccentRoleProperty);
        set => SetValue(AccentRoleProperty, value);
    }

    public static readonly DependencyProperty AccentRoleProperty = DependencyProperty.Register(
        nameof(AccentRole),
        typeof(VolumeAccentRole),
        typeof(CapacityBar),
        new PropertyMetadata(VolumeAccentRole.Other, OnCapacityChanged));

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("PART_Root") as Grid;
        _track = GetTemplateChild("PART_Track") as Border;
        ApplyCornerRadius();
        Rebuild();
    }

    /// <summary>
    /// Rounds the ends by as much as the bar's own height allows, and no more.
    /// </summary>
    /// <remarks>
    /// A pill's radius is <i>half its height</i> — a relationship, not a number — so one constant
    /// cannot serve bars that are 5, 8, 9 and 12 px tall. <c>SmPillCornerRadius</c> is 10, which is
    /// exactly right for the 20 px status pills it was written for and four times too large for a
    /// 5 px strip.
    /// <para>
    /// An impossible radius is not refused. The corner is fitted to the height while staying as wide
    /// as it was asked to be, so the cap is drawn as a flattened ellipse and both ends of the bar
    /// read as stretched — which is precisely what they did at 5, 8 and 9 px, while the one bar that
    /// asked for a radius it could afford (12 px tall, radius 3) looked right.
    /// </para>
    /// <para>
    /// Clamping here rather than at each call site keeps the token meaning "as round as this bar can
    /// be" at any height, and means a bar added later cannot bring the defect back by forgetting to
    /// override it.
    /// </para>
    /// </remarks>
    private void ApplyCornerRadius()
    {
        if (_track is null)
        {
            return;
        }

        // Before the first layout pass there is no size to clamp against, and SizeChanged will call
        // back the moment there is.
        double width = ActualWidth > 0 ? ActualWidth : double.MaxValue;
        double height = ActualHeight > 0 ? ActualHeight : double.MaxValue;
        double limit = Math.Min(width, height) / 2;

        CornerRadius asked = CornerRadius;
        _track.CornerRadius = new CornerRadius(
            Math.Min(asked.TopLeft, limit),
            Math.Min(asked.TopRight, limit),
            Math.Min(asked.BottomRight, limit),
            Math.Min(asked.BottomLeft, limit));
    }

    private static void OnCapacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CapacityBar)d).Rebuild();

    private void Rebuild()
    {
        if (_root is null)
        {
            return;
        }

        _root.ColumnDefinitions.Clear();
        _root.Children.Clear();

        VolumeCapacityResult capacity = Capacity;
        if (capacity.Segments.IsDefaultOrEmpty)
        {
            return;
        }

        int column = 0;
        foreach (CapacitySegment segment in capacity.Segments)
        {
            if (segment.Fraction <= 0)
            {
                continue;
            }

            _root.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(segment.Fraction, GridUnitType.Star) });

            Rectangle band = new() { Fill = BrushFor(segment.Kind) };
            Grid.SetColumn(band, column++);
            _root.Children.Add(band);
        }

        // The remainder is the untouched track, so it gets a column but no rectangle.
        if (capacity.FreeFraction > 0)
        {
            _root.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(capacity.FreeFraction, GridUnitType.Star) });
        }
    }

    private Brush? BrushFor(CapacitySegmentKind kind) => kind switch
    {
        // Amber reads as "actionable" rather than "consumed", which is what reclaimable space is.
        CapacitySegmentKind.Reclaimable => Resolve("SmCategory3Brush"),
        CapacitySegmentKind.Freed => FreedBrush(),
        CapacitySegmentKind.Safe => Resolve("SmGoodBrush"),
        CapacitySegmentKind.Check => Resolve("SmWarnBrush"),
        CapacitySegmentKind.Careful => Resolve("SmBadBrush"),
        _ => Resolve(AccentRole switch
        {
            VolumeAccentRole.DevDrive => "SmCategory2Brush",
            VolumeAccentRole.System => "SmCategory0Brush",
            _ => "SmCategory5Brush",
        }),
    };

    /// <summary>
    /// The freed band, hatched rather than solid.
    /// </summary>
    /// <remarks>
    /// Freed space is the only band on the bar that does not exist yet — it is a promise about what
    /// happens if the user presses the button. A hatch says "pending" in a way no flat colour can,
    /// and it keeps the band legible next to the solid good-coloured text around it.
    /// <para>
    /// <c>MappingMode.Absolute</c> with <c>SpreadMethod.Repeat</c> is what makes this a repeating
    /// stripe rather than a single sweep: the gradient is defined over an 8x8 DIP square and tiled,
    /// which is the direct equivalent of the design's
    /// <c>repeating-linear-gradient(135deg, good 0 4px, good/55% 4px 8px)</c>. Duplicated stops at
    /// 0.5 give the hard edge; a smooth ramp would read as a gradient fill, not a hatch.
    /// </para>
    /// </remarks>
    private Brush? FreedBrush()
    {
        if (Resolve("SmGoodBrush") is not SolidColorBrush good)
        {
            return null;
        }

        Color solid = good.Color;
        Color faded = Color.FromArgb((byte)(solid.A * 0.55), solid.R, solid.G, solid.B);

        return new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(8, 8),
            GradientStops =
            {
                new GradientStop { Color = solid, Offset = 0 },
                new GradientStop { Color = solid, Offset = 0.5 },
                new GradientStop { Color = faded, Offset = 0.5 },
                new GradientStop { Color = faded, Offset = 1 },
            },
        };
    }

    private static Brush? Resolve(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Brush : null;
}
