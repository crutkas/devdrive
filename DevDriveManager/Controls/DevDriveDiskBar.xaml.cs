using DevDriveCore;
using DevDriveCore.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Controls;

/// <summary>
/// Treatment A size control: a <b>read-only</b> horizontal disk-bar showing the source's
/// <b>used + protected</b> space, the <b>shrinkable/free</b> space, and the carved <b>Dev Drive</b>
/// chunk. It is a non-interactive visualization — the page's Slider + NumberBox (bound to the same
/// value) are the accessible controls that change the size. Geometry comes from the pure, unit-tested
/// <see cref="DevDriveSizeMath"/>.
/// </summary>
public sealed partial class DevDriveDiskBar : UserControl
{
    public DevDriveDiskBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateVisual();
            UpdateLegend();
        };
    }

    private const double SegmentGap = 3d;

    /// <summary>Total capacity of the source/host volume in bytes (the whole bar).</summary>
    public static readonly DependencyProperty TotalBytesProperty = DependencyProperty.Register(
        nameof(TotalBytes), typeof(double), typeof(DevDriveDiskBar), new PropertyMetadata(0d, OnVisualChanged));

    public double TotalBytes
    {
        get => (double)GetValue(TotalBytesProperty);
        set => SetValue(TotalBytesProperty, value);
    }

    /// <summary>Used + protected bytes on the source/host volume.</summary>
    public static readonly DependencyProperty UsedBytesProperty = DependencyProperty.Register(
        nameof(UsedBytes), typeof(double), typeof(DevDriveDiskBar), new PropertyMetadata(0d, OnVisualChanged));

    public double UsedBytes
    {
        get => (double)GetValue(UsedBytesProperty);
        set => SetValue(UsedBytesProperty, value);
    }

    /// <summary>Maximum selectable Dev Drive size in bytes (the free / shrinkable space).</summary>
    public static readonly DependencyProperty MaximumSelectableBytesProperty = DependencyProperty.Register(
        nameof(MaximumSelectableBytes), typeof(double), typeof(DevDriveDiskBar), new PropertyMetadata(0d, OnVisualChanged));

    public double MaximumSelectableBytes
    {
        get => (double)GetValue(MaximumSelectableBytesProperty);
        set => SetValue(MaximumSelectableBytesProperty, value);
    }

    /// <summary>The chosen Dev Drive size in bytes (drives the carved segment width). Set by the page's Slider/NumberBox.</summary>
    public static readonly DependencyProperty SelectedBytesProperty = DependencyProperty.Register(
        nameof(SelectedBytes), typeof(double), typeof(DevDriveDiskBar), new PropertyMetadata(0d, OnVisualChanged));

    public double SelectedBytes
    {
        get => (double)GetValue(SelectedBytesProperty);
        set => SetValue(SelectedBytesProperty, value);
    }

    /// <summary>Legend caption for the used + protected segment.</summary>
    public static readonly DependencyProperty UsedLabelProperty = DependencyProperty.Register(
        nameof(UsedLabel), typeof(string), typeof(DevDriveDiskBar), new PropertyMetadata("Used + protected", OnLabelChanged));

    public string UsedLabel
    {
        get => (string)GetValue(UsedLabelProperty);
        set => SetValue(UsedLabelProperty, value);
    }

    /// <summary>Legend caption for the remaining free/shrinkable segment.</summary>
    public static readonly DependencyProperty RemainingLabelProperty = DependencyProperty.Register(
        nameof(RemainingLabel), typeof(string), typeof(DevDriveDiskBar), new PropertyMetadata("Remaining", OnLabelChanged));

    public string RemainingLabel
    {
        get => (string)GetValue(RemainingLabelProperty);
        set => SetValue(RemainingLabelProperty, value);
    }

    /// <summary>Legend caption for the carved Dev Drive segment.</summary>
    public static readonly DependencyProperty DevDriveLabelProperty = DependencyProperty.Register(
        nameof(DevDriveLabel), typeof(string), typeof(DevDriveDiskBar), new PropertyMetadata("Dev Drive", OnLabelChanged));

    public string DevDriveLabel
    {
        get => (string)GetValue(DevDriveLabelProperty);
        set => SetValue(DevDriveLabelProperty, value);
    }

    /// <summary>
    /// Share of the source's capacity below which its remaining free space is shown as low.
    /// </summary>
    /// <remarks>
    /// A property rather than a read of the preferences singleton, so the threshold the bar paints
    /// against is the same one Settings advertises and the control stays testable without a store.
    /// </remarks>
    public static readonly DependencyProperty LowFreeFractionProperty = DependencyProperty.Register(
        nameof(LowFreeFraction),
        typeof(double),
        typeof(DevDriveDiskBar),
        new PropertyMetadata(AttentionSignalBuilder.LowFreeFraction, OnVisualChanged));

    public double LowFreeFraction
    {
        get => (double)GetValue(LowFreeFractionProperty);
        set => SetValue(LowFreeFractionProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DevDriveDiskBar)d).UpdateVisual();

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DevDriveDiskBar)d).UpdateLegend();

    /// <summary>
    /// Shows each "Name · Size" caption as a name over a stronger size, so the numbers scan as a row.
    /// </summary>
    private void UpdateLegend()
    {
        Set(UsedLabel, UsedName, UsedValue);
        Set(RemainingLabel, FreeName, FreeValue);
        Set(DevDriveLabel, DevName, DevValue);

        static void Set(string? label, TextBlock name, TextBlock value)
        {
            (string n, string v) = SplitLabel(label);
            name.Text = n;
            value.Text = v;
            value.Visibility = v.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal static (string Name, string Value) SplitLabel(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return (string.Empty, string.Empty);
        }

        int i = label.IndexOf('\u00B7');
        return i < 0
            ? (label.Trim(), string.Empty)
            : (label[..i].Trim(), label[(i + 1)..].Trim());
    }

    /// <summary>
    /// Hides zero-width segments and puts one even gap between each visible neighbour, so an empty
    /// segment never leaves a doubled gap or a stray margin at the bar's edge.
    /// </summary>
    private void ApplyGaps(double usedFraction, double remainingFraction, double devFraction)
    {
        Border[] segments = [UsedSegment, FreeSegment, DevSegment];
        bool[] visible = [usedFraction > 0d, remainingFraction > 0d, devFraction > 0d];

        int first = Array.IndexOf(visible, true);
        int last = Array.LastIndexOf(visible, true);
        double half = SegmentGap / 2d;

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i].Visibility = visible[i] ? Visibility.Visible : Visibility.Collapsed;
            segments[i].Margin = new Thickness(i > first ? half : 0d, 0d, i < last ? half : 0d, 0d);
        }
    }

    private void UpdateVisual()
    {
        double total = TotalBytes;
        double usedFraction;
        double remainingFraction;
        double devFraction;

        if (total <= 0d)
        {
            // Nothing loaded yet — render an empty (all-remaining) bar.
            usedFraction = 0d;
            remainingFraction = 1d;
            devFraction = 0d;
        }
        else
        {
            usedFraction = DevDriveSizeMath.UsedFraction(total, UsedBytes);
            remainingFraction = DevDriveSizeMath.RemainingFraction(total, MaximumSelectableBytes, SelectedBytes);
            devFraction = Math.Max(0d, 1d - usedFraction - remainingFraction);
        }

        UsedColumn.Width = new GridLength(usedFraction, GridUnitType.Star);
        RemainingColumn.Width = new GridLength(remainingFraction, GridUnitType.Star);
        DevColumn.Width = new GridLength(devFraction, GridUnitType.Star);

        ApplyGaps(usedFraction, remainingFraction, devFraction);
        ApplyFreeStyle();

        AutomationProperties.SetName(this, $"Dev Drive size {ByteSizeFormatter.Format(ToBytes(SelectedBytes))}");
    }

    /// <summary>
    /// Colours the free segment by how much of it is left.
    /// </summary>
    /// <remarks>
    /// Amber is the app's "worth your attention" colour, and the size bar is the one place where
    /// crossing the low-free line is still undoable. Saying it here, live as the slider moves,
    /// beats saying it once the drive exists. The amber is paired with a sentence, so the state is
    /// never carried by colour alone.
    /// <para>
    /// This bar has no reclaimable band, so amber is unambiguous on it. The shared
    /// <c>CapacityBar</c> deliberately does <i>not</i> do this: there amber already means
    /// "reclaimable", and an amber free channel beside an amber reclaimable band would read as one
    /// band twice the size.
    /// </para>
    /// <para>
    /// A <see cref="Style"/> is applied rather than a <see cref="Brush"/> assigned. A brush pulled
    /// out of <c>Resources</c> belongs to whichever theme was live when it was read, and assigning
    /// it sets a local value that outranks the style and never re-resolves — so the segment would
    /// keep its dark fill after a switch to Light. A style is theme-independent and its setters
    /// re-resolve, which is the pattern the rest of this app settled on for exactly this seam.
    /// </para>
    /// </remarks>
    private void ApplyFreeStyle()
    {
        bool low = DevDriveSizeMath.LeavesSourceLowOnSpace(
            TotalBytes, MaximumSelectableBytes, SelectedBytes, LowFreeFraction);

        Apply(FreeSegment, low ? "DiskBarFreeSegmentLowStyle" : "DiskBarFreeSegmentStyle");
        Apply(FreeSwatch, low ? "DiskBarFreeSwatchLowStyle" : "DiskBarFreeSwatchStyle");

        LowSpaceWarning.Text = LowSpaceWarningText(LowFreeFraction);
        LowSpaceWarning.Visibility = low ? Visibility.Visible : Visibility.Collapsed;

        void Apply(Border target, object key)
        {
            if (Resources.TryGetValue(key, out object? value) && value is Style style)
            {
                target.Style = style;
            }
        }
    }

    /// <summary>The sentence shown with the amber state, so the warning is not carried by colour alone.</summary>
    internal static string LowSpaceWarningText(double lowFreeFraction) =>
        $"This leaves the volume with less than {Math.Round(lowFreeFraction * 100):0}% free. " +
        "Choose a smaller size to keep room for Windows and apps.";

    private static ulong ToBytes(double value) => value <= 0d ? 0UL : (ulong)Math.Round(value);
}
