using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

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
/// Free space is painted in the track colour, not as another hue. Painting "free" as a coloured band
/// would make an empty volume look as busy as a full one. It is its own rectangle, rather than the
/// track's background, only so a gap can separate it from the bands.
/// </para>
/// </remarks>
public sealed partial class CapacityBar : Control
{
    private static readonly AccessibilitySettings Accessibility = new();

    /// <summary>
    /// The gap between the last band and the free track, in DIPs. Used against free can be as low
    /// as 1.35:1, so the edge is drawn by the card showing through rather than by those two colours
    /// meeting. Same idea as <c>DevDriveDiskBar</c>'s segment gap, kept narrower because these bars
    /// are as thin as 5px.
    /// </summary>
    private const double FreeTrackGap = 2d;

    private Grid? _root;
    private Border? _track;
    private Border? _goodProbe;
    private long _goodProbeToken;

    public CapacityBar()
    {
        DefaultStyleKey = typeof(CapacityBar);
        IsTabStop = false;

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
    /// switch. The role is theme-independent; this control turns it into a band style whose
    /// <c>ThemeResource</c> setter follows the bar's own theme.
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

        _goodProbe?.UnregisterPropertyChangedCallback(Border.BackgroundProperty, _goodProbeToken);

        _root = GetTemplateChild("PART_Root") as Grid;
        _track = GetTemplateChild("PART_Track") as Border;
        _goodProbe = GetTemplateChild("PART_GoodProbe") as Border;

        // The probe's ThemeResource re-resolves on a light/dark or high-contrast switch of THIS
        // element's theme, which is the only signal the hatched band needs to be rebuilt. Every
        // other band is styled and re-inks itself.
        if (_goodProbe is not null)
        {
            _goodProbeToken = _goodProbe.RegisterPropertyChangedCallback(
                Border.BackgroundProperty, (_, _) => Rebuild());
        }

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
        if (_root is not { } root)
        {
            return;
        }

        root.ColumnDefinitions.Clear();
        root.Children.Clear();

        VolumeCapacityResult capacity = Capacity;
        bool highContrast = Accessibility.HighContrast;

        int column = 0;
        if (!capacity.Segments.IsDefaultOrEmpty)
        {
            foreach (CapacitySegment segment in capacity.Segments)
            {
                if (segment.Fraction <= 0)
                {
                    continue;
                }

                AddColumn(segment.Fraction);

                if (segment.Kind == CapacitySegmentKind.Freed && !highContrast && FreedHatch() is { } hatch)
                {
                    // The hatch's faded stripes sit over the free channel, exactly as they did when
                    // the track behind every band was the free colour.
                    AddBand(column, "SmCapacityFreeBandStyle");
                    AddBand(column, "SmCapacityBandStyle").Fill = hatch;
                }
                else
                {
                    AddBand(column, StyleKeyFor(segment.Kind));
                }

                column++;
            }
        }

        // An unloaded or unmeasured volume is all track, so an empty bar reads as empty rather than
        // as missing.
        double free = column == 0 ? 1 : capacity.FreeFraction;
        if (free > 0)
        {
            AddColumn(free);
            Rectangle track = AddBand(column, "SmCapacityFreeBandStyle");
            if (column > 0)
            {
                track.Margin = new Thickness(FreeTrackGap, 0, 0, 0);
            }
        }

        void AddColumn(double fraction) =>
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });

        Rectangle AddBand(int at, string styleKey)
        {
            Rectangle band = new();
            if (Application.Current.Resources.TryGetValue(styleKey, out object? value) && value is Style style)
            {
                band.Style = style;
            }

            Grid.SetColumn(band, at);
            root.Children.Add(band);
            return band;
        }
    }

    /// <summary>
    /// Which band style paints a segment.
    /// </summary>
    /// <remarks>
    /// A <see cref="Style"/> key rather than a <see cref="Brush"/>. The brushes used to be read from
    /// <c>Application.Current.Resources</c>, which answers for the app-wide theme rather than this
    /// element's <see cref="FrameworkElement.ActualTheme"/>, so with the in-app theme set against the
    /// Windows one every rebuild painted the other theme's colours. A style is theme-independent
    /// and its <c>ThemeResource</c> setter resolves against the band it is applied to.
    /// </remarks>
    private string StyleKeyFor(CapacitySegmentKind kind) => kind switch
    {
        // Amber reads as "actionable" rather than "consumed", which is what reclaimable space is.
        CapacitySegmentKind.Reclaimable => "SmCapacityReclaimableBandStyle",
        CapacitySegmentKind.Freed => "SmCapacityFreedSolidBandStyle",
        CapacitySegmentKind.Safe => "SmCapacitySafeBandStyle",
        CapacitySegmentKind.Check => "SmCapacityCheckBandStyle",
        CapacitySegmentKind.Careful => "SmCapacityCarefulBandStyle",
        // Same palette as the Create room's disk bar: used space is blue, a Dev Drive is teal. Other
        // volumes used to be slate (SmCategory5), which is nearly the free track's own colour, so
        // used and free could not be told apart.
        _ => AccentRole == VolumeAccentRole.DevDrive ? "SmCapacityDevDriveBandStyle" : "SmCapacityUsedBandStyle",
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
    /// <para>
    /// A gradient cannot be expressed as a <c>ThemeResource</c>, so its colour is read back from the
    /// template's probe, whose own <c>ThemeResource</c> resolves against this bar's theme. Under high
    /// contrast the caller skips this entirely and paints solid Highlight.
    /// </para>
    /// </remarks>
    private LinearGradientBrush? FreedHatch()
    {
        if (_goodProbe?.Background is not SolidColorBrush good)
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
}
