using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// Two byte formatters exist: DevDriveStorage's is base-1000, DevDriveCore's is base-1024. The strip
// sits directly beside the reclaim totals, which already use the core one, and a bar reading
// "148.7 GB free" next to a figure computed in different units is the kind of quiet inconsistency
// that costs a delete tool its credibility. Aliased rather than qualified inline so the choice is
// stated once, here, instead of being re-decided at each call site.
using ByteSize = DevDriveCore.ByteSizeFormatter;

namespace DevDriveManager.Controls;

/// <summary>
/// One volume in the context strip: letter tile, name, caption, free space, and capacity bar.
/// </summary>
/// <remarks>
/// The card takes a <see cref="StorageVolume"/> rather than pre-formatted strings so a caller cannot
/// caption a volume wrongly — the caption and the bands both come from <see cref="VolumeCapacity"/>,
/// which is where that logic is unit tested.
/// <para>
/// Text is assigned in code rather than bound. The displayed values are all derived from
/// <see cref="Volume"/>, so binding them would mean either duplicating each as a dependency property
/// or making the control observable purely to talk to itself. A single <c>Refresh</c> is the smaller
/// mechanism and keeps the derivation in one readable place.
/// </para>
/// </remarks>
public sealed partial class VolumeCard : UserControl
{
    public VolumeCard()
    {
        InitializeComponent();

        // The selection ring lives in the button's template, so it does not exist until the template
        // is applied. Refreshing on Loaded is what makes a card that starts out selected actually
        // render selected rather than only picking it up on the next change.
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised when the card is chosen. Selection itself stays with the caller.</summary>
    public event EventHandler? Selected;

    /// <summary>The volume to describe. Null renders an empty card rather than throwing.</summary>
    public StorageVolume? Volume
    {
        get => (StorageVolume?)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(
        nameof(Volume), typeof(StorageVolume), typeof(VolumeCard), new PropertyMetadata(null, OnAnyChanged));

    /// <summary>Whether this is the volume Windows booted from, which the caption calls out.</summary>
    public bool IsSystemVolume
    {
        get => (bool)GetValue(IsSystemVolumeProperty);
        set => SetValue(IsSystemVolumeProperty, value);
    }

    public static readonly DependencyProperty IsSystemVolumeProperty = DependencyProperty.Register(
        nameof(IsSystemVolume), typeof(bool), typeof(VolumeCard), new PropertyMetadata(false, OnAnyChanged));

    /// <summary>Bytes a reclaim scan found here, carved out of the used band rather than added to it.</summary>
    public long ReclaimableBytes
    {
        get => (long)GetValue(ReclaimableBytesProperty);
        set => SetValue(ReclaimableBytesProperty, value);
    }

    public static readonly DependencyProperty ReclaimableBytesProperty = DependencyProperty.Register(
        nameof(ReclaimableBytes), typeof(long), typeof(VolumeCard), new PropertyMetadata(0L, OnAnyChanged));

    /// <summary>
    /// Which colour family the in-use band uses, so two volumes are told apart at a glance.
    /// Passed through to <see cref="CapacityBar"/> as a role rather than a resolved brush, so it
    /// survives a theme change.
    /// </summary>
    public VolumeAccentRole AccentRole
    {
        get => (VolumeAccentRole)GetValue(AccentRoleProperty);
        set => SetValue(AccentRoleProperty, value);
    }

    public static readonly DependencyProperty AccentRoleProperty = DependencyProperty.Register(
        nameof(AccentRole),
        typeof(VolumeAccentRole),
        typeof(VolumeCard),
        new PropertyMetadata(VolumeAccentRole.Other, OnAnyChanged));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(VolumeCard), new PropertyMetadata(false, OnAnyChanged));

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((VolumeCard)d).Refresh();

    private void Refresh()
    {
        if (PartButton is null)
        {
            return;
        }

        StorageVolume? volume = Volume;
        if (volume is null)
        {
            PartLetter.Text = string.Empty;
            PartName.Text = string.Empty;
            PartCaption.Text = string.Empty;
            PartFree.Text = string.Empty;
            PartBar.Capacity = default;
            return;
        }

        // DriveLetter is "C:" — the tile shows the bare letter, and automation ids drop the colon so
        // they stay usable as plain identifiers in test selectors.
        string letter = volume.DriveLetter.TrimEnd(':');

        PartLetter.Text = letter;
        PartName.Text = volume.DisplayName;
        PartCaption.Text = VolumeCapacity.CaptionFor(volume, IsSystemVolume);

        string free = ByteSize.Format(volume.FreeBytes <= 0 ? 0UL : (ulong)volume.FreeBytes);
        PartFree.Text = $"{free} free";

        PartBar.AccentRole = AccentRole;
        PartBar.Capacity = VolumeCapacity.ForVolume(volume, ReclaimableBytes);

        VisualStateManager.GoToState(PartButton, IsSelected ? "Selected" : "Unselected", false);

        // Stable across launches, and stable across relabelling, because tests address volumes by letter.
        AutomationProperties.SetAutomationId(PartButton, $"VolumeCard_{letter}");

        // Announced as one sentence: the card is a single control to a screen reader, so the caption
        // and free space have to travel with the name rather than sit in unread sibling TextBlocks.
        AutomationProperties.SetName(
            PartButton,
            $"{volume.DisplayName}, {PartCaption.Text}, {free} free");
    }

    private void OnCardClick(object sender, RoutedEventArgs e) => Selected?.Invoke(this, EventArgs.Empty);
}
