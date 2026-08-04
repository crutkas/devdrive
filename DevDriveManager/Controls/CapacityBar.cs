using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

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

    public CapacityBar()
    {
        DefaultStyleKey = typeof(CapacityBar);
        IsTabStop = false;
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
    /// Colour for the in-use band. Set per volume so C: and G: are told apart at a glance, which is
    /// the whole reason the strip shows two bars rather than one number.
    /// </summary>
    public Brush? UsedBrush
    {
        get => (Brush?)GetValue(UsedBrushProperty);
        set => SetValue(UsedBrushProperty, value);
    }

    public static readonly DependencyProperty UsedBrushProperty = DependencyProperty.Register(
        nameof(UsedBrush), typeof(Brush), typeof(CapacityBar), new PropertyMetadata(null, OnCapacityChanged));

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("PART_Root") as Grid;
        Rebuild();
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
        _ => UsedBrush ?? Resolve("SmCategory0Brush"),
    };

    private static Brush? Resolve(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Brush : null;
}
