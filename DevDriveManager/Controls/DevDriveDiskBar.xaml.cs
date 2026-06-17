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
        Loaded += (_, _) => UpdateVisual();
    }

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
        nameof(UsedLabel), typeof(string), typeof(DevDriveDiskBar), new PropertyMetadata("Used + protected"));

    public string UsedLabel
    {
        get => (string)GetValue(UsedLabelProperty);
        set => SetValue(UsedLabelProperty, value);
    }

    /// <summary>Legend caption for the remaining free/shrinkable segment.</summary>
    public static readonly DependencyProperty RemainingLabelProperty = DependencyProperty.Register(
        nameof(RemainingLabel), typeof(string), typeof(DevDriveDiskBar), new PropertyMetadata("Remaining"));

    public string RemainingLabel
    {
        get => (string)GetValue(RemainingLabelProperty);
        set => SetValue(RemainingLabelProperty, value);
    }

    /// <summary>Legend caption for the carved Dev Drive segment.</summary>
    public static readonly DependencyProperty DevDriveLabelProperty = DependencyProperty.Register(
        nameof(DevDriveLabel), typeof(string), typeof(DevDriveDiskBar), new PropertyMetadata("Dev Drive"));

    public string DevDriveLabel
    {
        get => (string)GetValue(DevDriveLabelProperty);
        set => SetValue(DevDriveLabelProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DevDriveDiskBar)d).UpdateVisual();

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

        AutomationProperties.SetName(this, $"Dev Drive size {ByteSizeFormatter.Format(ToBytes(SelectedBytes))}");
    }

    private static ulong ToBytes(double value) => value <= 0d ? 0UL : (ulong)Math.Round(value);
}
