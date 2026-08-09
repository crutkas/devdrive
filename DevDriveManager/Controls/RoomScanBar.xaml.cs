using System.Collections;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDriveManager.Controls;

/// <summary>Whether a room's volume chips are something the user picks between.</summary>
public enum VolumeStripSelection
{
    /// <summary>
    /// The room acts on the whole machine, so there is nothing to choose. Chips report, and are
    /// inert: not focusable, not clickable, no hover.
    /// </summary>
    None,

    /// <summary>The room acts on one volume, and the chips are how it is chosen.</summary>
    Single,
}

/// <summary>
/// The bar every room opens with: the volume strip, and the room's one scan control.
/// </summary>
/// <remarks>
/// <para>
/// See the comment block in the XAML for the grammar this exists to enforce. The short version:
/// chips select scope and never start work, the button is the only thing that starts work, and its
/// label names the scope and never changes to "Rescan".
/// </para>
/// <para>
/// Scanning is surfaced as events rather than <c>ICommand</c> properties because the rooms disagree
/// about where the verb lives — Overview and Reclaim have commands on a view model, Space has async
/// methods on the page. Taking commands would have forced one of them to invent a wrapper whose only
/// job is to satisfy this control's taste. Enablement is therefore an explicit
/// <see cref="CanScan"/> rather than an inferred <c>CanExecute</c>, which is also easier to reason
/// about at a glance.
/// </para>
/// </remarks>
public sealed partial class RoomScanBar : UserControl
{
    public RoomScanBar()
    {
        InitializeComponent();

        // Containers do not exist until the strip has been realised, so chip wiring has to wait for
        // a layout pass. Without this the first render is inert even where selection is enabled.
        //
        // LayoutUpdated fires on every layout pass, which is per-frame during a scroll or an
        // animation, so the handler has to be genuinely free once the work is done — see
        // WireChips. This app has three recorded instances of a cheap-looking handler doing a
        // visual-tree walk on every tick.
        PartStrip.LayoutUpdated += (_, _) => WireChips();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>Raised when the user asks for a scan.</summary>
    public event EventHandler? ScanRequested;

    /// <summary>Raised when the user asks to stop the running scan.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>
    /// Raised when the user picks a volume, and only in <see cref="VolumeStripSelection.Single"/>.
    /// Selecting a scope is not the same as asking for a scan, and this control never conflates
    /// them — a room that wants click-to-scan has to say so by calling its own scan path.
    /// </summary>
    public event EventHandler<StorageVolume>? VolumeSelected;

    public IEnumerable? Volumes
    {
        get => (IEnumerable?)GetValue(VolumesProperty);
        set => SetValue(VolumesProperty, value);
    }

    public static readonly DependencyProperty VolumesProperty = DependencyProperty.Register(
        nameof(Volumes), typeof(IEnumerable), typeof(RoomScanBar), new PropertyMetadata(null));

    /// <summary>
    /// What the scan button says. Names the scope, and stays the same before and after a scan.
    /// Whole-machine rooms should all use the identical string, because they do the identical thing.
    /// </summary>
    public string ScanLabel
    {
        get => (string)GetValue(ScanLabelProperty);
        set => SetValue(ScanLabelProperty, value);
    }

    public static readonly DependencyProperty ScanLabelProperty = DependencyProperty.Register(
        nameof(ScanLabel), typeof(string), typeof(RoomScanBar),
        new PropertyMetadata("Scan this PC", OnAnyChanged));

    public bool IsScanning
    {
        get => (bool)GetValue(IsScanningProperty);
        set => SetValue(IsScanningProperty, value);
    }

    public static readonly DependencyProperty IsScanningProperty = DependencyProperty.Register(
        nameof(IsScanning), typeof(bool), typeof(RoomScanBar),
        new PropertyMetadata(false, OnAnyChanged));

    /// <summary>
    /// Whether a scan is possible at all, ignoring whether one is already running. Defaults to true
    /// because the usual answer is yes; a room that needs a scope selected first says so.
    /// </summary>
    public bool CanScan
    {
        get => (bool)GetValue(CanScanProperty);
        set => SetValue(CanScanProperty, value);
    }

    public static readonly DependencyProperty CanScanProperty = DependencyProperty.Register(
        nameof(CanScan), typeof(bool), typeof(RoomScanBar),
        new PropertyMetadata(true, OnAnyChanged));

    /// <summary>Live detail shown only while scanning, for rooms that have any.</summary>
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(RoomScanBar),
        new PropertyMetadata(string.Empty, OnAnyChanged));

    /// <summary>Opts into a determinate bar. Rooms that cannot measure progress leave this false.</summary>
    public bool ShowProgress
    {
        get => (bool)GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public static readonly DependencyProperty ShowProgressProperty = DependencyProperty.Register(
        nameof(ShowProgress), typeof(bool), typeof(RoomScanBar),
        new PropertyMetadata(false, OnAnyChanged));

    /// <summary>Scan progress from 0 to 1. Negative means "running, but unmeasured".</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RoomScanBar),
        new PropertyMetadata(0d, OnAnyChanged));

    public VolumeStripSelection SelectionMode
    {
        get => (VolumeStripSelection)GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register(
        nameof(SelectionMode), typeof(VolumeStripSelection), typeof(RoomScanBar),
        new PropertyMetadata(VolumeStripSelection.None, OnAnyChanged));

    /// <summary>The volume whose chip is ticked, in single-selection rooms.</summary>
    public string? SelectedVolumePath
    {
        get => (string?)GetValue(SelectedVolumePathProperty);
        set => SetValue(SelectedVolumePathProperty, value);
    }

    public static readonly DependencyProperty SelectedVolumePathProperty = DependencyProperty.Register(
        nameof(SelectedVolumePath), typeof(string), typeof(RoomScanBar),
        new PropertyMetadata(null, OnAnyChanged));

    /// <summary>Automation id for the strip, so each room stays individually addressable in tests.</summary>
    public string StripAutomationId
    {
        get => (string)GetValue(StripAutomationIdProperty);
        set => SetValue(StripAutomationIdProperty, value);
    }

    public static readonly DependencyProperty StripAutomationIdProperty = DependencyProperty.Register(
        nameof(StripAutomationId), typeof(string), typeof(RoomScanBar),
        new PropertyMetadata(string.Empty, OnAnyChanged));

    public string StripAutomationName
    {
        get => (string)GetValue(StripAutomationNameProperty);
        set => SetValue(StripAutomationNameProperty, value);
    }

    public static readonly DependencyProperty StripAutomationNameProperty = DependencyProperty.Register(
        nameof(StripAutomationName), typeof(string), typeof(RoomScanBar),
        new PropertyMetadata(string.Empty, OnAnyChanged));

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RoomScanBar)d).Refresh();

    private void Refresh()
    {
        if (PartScanButton is null)
        {
            return;
        }

        PartScanButton.Content = ScanLabel;
        PartScanButton.IsEnabled = CanScan && !IsScanning;

        AutomationProperties.SetAutomationId(PartStrip, StripAutomationId);
        AutomationProperties.SetName(PartStrip, StripAutomationName);

        bool hasStatus = !string.IsNullOrWhiteSpace(StatusText);
        PartStatusText.Text = StatusText;
        PartStatusText.Visibility = Show(IsScanning && hasStatus);

        PartProgressBar.Visibility = Show(IsScanning && ShowProgress);
        PartProgressBar.IsIndeterminate = Progress < 0;
        PartProgressBar.Value = Progress < 0 ? 0 : Progress;

        // The ring is the fallback for rooms with no fraction to show, not a second progress
        // indicator sitting beside the first.
        PartRing.Visibility = Show(IsScanning && !ShowProgress);
        PartRing.IsActive = IsScanning && !ShowProgress;

        PartCancelButton.Visibility = Show(IsScanning);

        WireChips();
    }

    private static Visibility Show(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Applies the room's interaction model to every realised chip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chip that hovers, takes focus and announces itself as a button, but does nothing when
    /// pressed, is a lie told to everyone who tries it — and to a screen reader user it is worse,
    /// because the promise is spoken aloud. In a room with no scope to choose the chip stops being
    /// a control and goes back to being a readout.
    /// </para>
    /// <para>
    /// This runs from <c>LayoutUpdated</c>, which fires on every layout pass — per frame while
    /// anything on screen is animating. So it opens by comparing what it is about to apply against
    /// what it last applied, and returns without touching the visual tree when they match, which is
    /// the overwhelmingly common case. The guard is the point of the method, not housekeeping: the
    /// unguarded version walks every container and re-subscribes every handler at frame rate, which
    /// is the same defect this codebase has already found in the Overview room's <c>Refresh()</c>,
    /// the Create room's volume strip, and the Space room's inspector.
    /// </para>
    /// </remarks>
    private void WireChips()
    {
        bool interactive = SelectionMode == VolumeStripSelection.Single;
        int realised = PartStrip.Items.Count;

        // The chip set changes identity when the strip is rebuilt, and that is not observable from
        // a count alone, so Volumes is part of the key rather than inferred from it.
        (object?, int, bool, string?) state = (Volumes, realised, interactive, SelectedVolumePath);
        if (_wiredState == state)
        {
            return;
        }

        var cards = Chips().ToList();
        if (cards.Count != realised)
        {
            // Mid-realisation. Wire what exists but do not record the state, so the next layout pass
            // finishes the job rather than skipping it.
            ApplyToChips(cards, interactive);
            return;
        }

        ApplyToChips(cards, interactive);
        _wiredState = state;
    }

    private (object?, int, bool, string?) _wiredState = (null, -1, false, null);

    private void ApplyToChips(IReadOnlyList<VolumeCard> cards, bool interactive)
    {
        foreach (VolumeCard card in cards)
        {
            card.IsInteractive = interactive;
            card.IsSelected = interactive &&
                SelectedVolumePath is string selected &&
                string.Equals(card.Volume?.RootPath, selected, StringComparison.OrdinalIgnoreCase);

            // Idempotent: -= on a handler that was never added is a no-op, so repeated layout
            // passes cannot stack duplicate subscriptions and fire the event several times.
            card.Selected -= OnChipSelected;
            if (interactive)
            {
                card.Selected += OnChipSelected;
            }
        }
    }

    private IEnumerable<VolumeCard> Chips()
    {
        for (int i = 0; i < PartStrip.Items.Count; i++)
        {
            if (PartStrip.ContainerFromIndex(i) is ContentPresenter presenter &&
                VisualTreeHelper.GetChildrenCount(presenter) > 0 &&
                VisualTreeHelper.GetChild(presenter, 0) is VolumeCard card)
            {
                yield return card;
            }
        }
    }

    private void OnChipSelected(object? sender, EventArgs args)
    {
        if (sender is not VolumeCard { Volume: StorageVolume volume })
        {
            return;
        }

        SelectedVolumePath = volume.RootPath;
        VolumeSelected?.Invoke(this, volume);
    }

    private void OnScanClick(object sender, RoutedEventArgs args) =>
        ScanRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelClick(object sender, RoutedEventArgs args) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);
}
