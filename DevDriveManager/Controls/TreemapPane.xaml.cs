using System.Collections.Specialized;
using System.Numerics;
using DevDriveStorage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace DevDriveManager.Controls;

public sealed class StorageNodeInvokedEventArgs(StorageNode node) : EventArgs
{
    public StorageNode Node { get; } = node;
}

/// <summary>
/// A nested, SpaceSniffer-style treemap of the current scope. <see cref="NestedTreemapLayout"/>
/// owns the geometry; this control turns it into Microsoft.UI.Composition visuals, so a couple of
/// thousand rectangles can glide into place while a scan streams in and zoom between scopes at
/// display rate. Thin XAML layers above the visuals carry what Composition cannot: the labels, the
/// keyboard and automation surface, and the hover card.
/// </summary>
/// <remarks>
/// Split by concern: this file owns the public surface, lifetime, scheduling and input;
/// <c>TreemapPane.Scene.cs</c> owns the visual tree and its animations; and
/// <c>TreemapPane.Chrome.cs</c> owns everything drawn in XAML on top of it.
/// </remarks>
public sealed partial class TreemapPane : UserControl
{
    private const int ThrottleMilliseconds = 250;
    private const int ResizeSettleMilliseconds = 140;
    private const int TapGuardMilliseconds = 300;

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(
            nameof(Rows),
            typeof(object),
            typeof(TreemapPane),
            new PropertyMetadata(null, OnRowsChanged));

    /// <summary>
    /// A counter the host bumps whenever the rows have been brought up to date.
    /// </summary>
    /// <remarks>
    /// Collection notifications alone are not sufficient: the host edits its projection in place,
    /// so a tick that only grows the numbers moves no rows at all, and a tick that re-ranks them
    /// raises several notifications rather than one reset.
    /// </remarks>
    public static readonly DependencyProperty RevisionProperty =
        DependencyProperty.Register(
            nameof(Revision),
            typeof(int),
            typeof(TreemapPane),
            new PropertyMetadata(0, OnInputChanged));

    public static readonly DependencyProperty SelectedRowProperty =
        DependencyProperty.Register(
            nameof(SelectedRow),
            typeof(StorageRowViewModel),
            typeof(TreemapPane),
            new PropertyMetadata(null, OnSelectedRowChanged));

    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(
            nameof(Snapshot),
            typeof(StorageSnapshot),
            typeof(TreemapPane),
            new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty ScopeProperty =
        DependencyProperty.Register(
            nameof(Scope),
            typeof(StorageNode),
            typeof(TreemapPane),
            new PropertyMetadata(null, OnInputChanged));

    private readonly UISettings _uiSettings = new();
    private readonly List<(Border Probe, long Token)> _probeTokens = [];
    private readonly Dictionary<Guid, StorageRowViewModel> _rowsById = [];
    private INotifyCollectionChanged? _observableRows;
    private DispatcherQueueTimer? _throttleTimer;
    private DispatcherQueueTimer? _resizeTimer;
    private XamlRoot? _xamlRoot;
    private double _rasterizationScale = 1;
    private bool _isSetUp;
    private bool _renderQueued;
    private bool _themeRefreshQueued;
    private bool _rebuildRequested;
    private long _lastRenderAt = long.MinValue / 2;
    private Point? _pointer;

    public TreemapPane()
    {
        InitializeComponent();

        // Rows is bound once to an app-lifetime collection, so the unhook in OnRowsChanged alone
        // would never run: without this pair every visit to the Space page would leave another pane
        // (and, through the XAML parent chain, its whole page) subscribed to that collection. The
        // Composition scene follows the same lifetime: its visuals, brushes and timers are only worth
        // holding while the pane is on screen.
        Loaded += Pane_Loaded;
        Unloaded += Pane_Unloaded;
        ActualThemeChanged += (_, _) => QueueThemeRefresh();
    }

    /// <summary>Raised when a cell is activated by a click, a tap, or Enter or Space on its stand-in.</summary>
    public event EventHandler<StorageNodeInvokedEventArgs>? NodeInvoked;

    /// <summary>Raised for Backspace or the mouse's back button: the map asking for the parent scope.</summary>
    public event EventHandler? NavigateUpRequested;

    public object? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public StorageRowViewModel? SelectedRow
    {
        get => (StorageRowViewModel?)GetValue(SelectedRowProperty);
        set => SetValue(SelectedRowProperty, value);
    }

    /// <summary>The snapshot the map is drawn from.</summary>
    public StorageSnapshot? Snapshot
    {
        get => (StorageSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    /// <summary>The folder the map fills the pane with; the snapshot root when unset.</summary>
    public StorageNode? Scope
    {
        get => (StorageNode?)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    private static void OnRowsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var pane = (TreemapPane)dependencyObject;
        pane.UnhookRows();
        pane.HookRows();
        pane.QueueRender();
    }

    private static void OnInputChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((TreemapPane)dependencyObject).QueueRender();

    private static void OnSelectedRowChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        // A render already on its way applies the selection to the layout it produces; applying it
        // now as well would flash the ring on a map that is about to be replaced.
        var pane = (TreemapPane)dependencyObject;
        if (!pane._renderQueued)
        {
            pane.ApplySelection(animate: true);
        }
    }

    /// <summary>
    /// Idempotent by construction: the handler is removed before it is added, so a pane that leaves
    /// and re-enters the visual tree — which WinUI allows without reconstructing it — attaches
    /// exactly once rather than once per visit.
    /// </summary>
    private void HookRows()
    {
        _observableRows = Rows as INotifyCollectionChanged;
        if (_observableRows is not null)
        {
            _observableRows.CollectionChanged -= Rows_CollectionChanged;
            _observableRows.CollectionChanged += Rows_CollectionChanged;
        }
    }

    private void UnhookRows()
    {
        if (_observableRows is not null)
        {
            _observableRows.CollectionChanged -= Rows_CollectionChanged;
            _observableRows = null;
        }
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        QueueRender();

    private void Pane_Loaded(object sender, RoutedEventArgs args)
    {
        HookRows();
        SetUp();
        QueueRender();
    }

    private void Pane_Unloaded(object sender, RoutedEventArgs args)
    {
        // WinUI can raise Unloaded for an element that has already been re-parented and loaded
        // again; tearing down then would leave a loaded pane without a scene.
        if (IsLoaded)
        {
            return;
        }

        UnhookRows();
        TearDown();
    }

    private void SetUp()
    {
        if (_isSetUp)
        {
            return;
        }

        _isSetUp = true;
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _compositor = ElementCompositionPreview.GetElementVisual(CompositionHost).Compositor;
        _root = _compositor.CreateContainerVisual();
        _root.Clip = _compositor.CreateInsetClip();
        _root.Size = HostSize();
        ElementCompositionPreview.SetElementChildVisual(CompositionHost, _root);
        _labelsVisual = ElementCompositionPreview.GetElementVisual(LabelCanvas);
        CreateAnimationTemplates();
        _theme = new TreemapTheme(_compositor, ReadColors());

        _throttleTimer ??= CreateTimer(ThrottleTimer_Tick);
        _resizeTimer ??= CreateTimer(ResizeTimer_Tick);
        _dwellTimer ??= CreateTimer(DwellTimer_Tick);

        foreach (Border probe in ThemeProbe.Children.OfType<Border>())
        {
            _probeTokens.Add((probe, probe.RegisterPropertyChangedCallback(
                Border.BackgroundProperty,
                Probe_BackgroundChanged)));
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged += UISettings_AnimationsEnabledChanged;
        }

        _xamlRoot = XamlRoot;
        if (_xamlRoot is not null)
        {
            _rasterizationScale = _xamlRoot.RasterizationScale;
            _xamlRoot.Changed += XamlRoot_Changed;
        }
    }

    private void TearDown()
    {
        if (!_isSetUp)
        {
            return;
        }

        _isSetUp = false;
        _renderQueued = false;
        _themeRefreshQueued = false;
        _rebuildRequested = false;
        _resizing = false;
        _pointer = null;
        _stripScopeId = null;
        _throttleTimer?.Stop();
        _resizeTimer?.Stop();
        _dwellTimer?.Stop();

        foreach ((Border probe, long token) in _probeTokens)
        {
            probe.UnregisterPropertyChangedCallback(Border.BackgroundProperty, token);
        }

        _probeTokens.Clear();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= UISettings_AnimationsEnabledChanged;
        }

        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= XamlRoot_Changed;
            _xamlRoot = null;
        }

        DisposeScene();
        ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
        if (_labelsVisual is not null)
        {
            _labelsVisual.StopAnimation("Opacity");
            _labelsVisual.Opacity = 1;
            _labelsVisual = null;
        }

        ReleaseAnimationTemplates();
        _theme?.Dispose();
        _theme = null;
        _root?.Dispose();
        _root = null;
        _compositor = null;
        TreemapEmptyState.Visibility = Visibility.Collapsed;
    }

    private DispatcherQueueTimer CreateTimer(TypedEventHandler<DispatcherQueueTimer, object> tick)
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += tick;
        return timer;
    }

    /// <summary>
    /// Collapses every reason to redraw that arrives in one turn of the UI thread into one pass. A
    /// streaming scan can move rows, bump the revision and swap the snapshot in the same tick.
    /// </summary>
    private void QueueRender()
    {
        if (!_isSetUp || _renderQueued)
        {
            return;
        }

        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(RenderQueued))
        {
            _renderQueued = false;
        }
    }

    private void RenderQueued()
    {
        _renderQueued = false;
        if (!_isSetUp)
        {
            return;
        }

        // A scan refreshes several times a second. Re-flowing the same scope more often than the
        // move animation can show only restarts it, so in-place updates are paced; a new scope, a
        // rebuild or a resize is never made to wait.
        if (!_rebuildRequested && !_resizing && IsInPlaceUpdate())
        {
            long wait = ThrottleMilliseconds - (Environment.TickCount64 - _lastRenderAt);
            if (wait > 0)
            {
                if (_throttleTimer is { IsRunning: false } timer)
                {
                    timer.Interval = TimeSpan.FromMilliseconds(wait);
                    timer.Start();
                }

                return;
            }
        }

        Render();
    }

    private bool IsInPlaceUpdate() =>
        _layer is Layer layer && ResolveScopeId(Snapshot) is Guid scopeId && layer.ScopeId == scopeId;

    private Guid? ResolveScopeId(StorageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return Scope is StorageNode scope && snapshot.Find(scope.Id) is not null ? scope.Id : snapshot.RootId;
    }

    private void ThrottleTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isSetUp)
        {
            Render();
        }
    }

    private void ResizeTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isSetUp)
        {
            Render();
        }
    }

    private void CompositionHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_isSetUp || _root is null)
        {
            return;
        }

        Vector2 size = HostSize();
        _root.Size = size;
        if (_layer is not Layer layer || layer.LayoutSize.X <= 0 || layer.LayoutSize.Y <= 0 ||
            size.X < 1 || size.Y < 1)
        {
            QueueRender();
            return;
        }

        // Re-running the layout for every step of a window drag would shuffle the whole map dozens
        // of times a second. Stretch the map that is already drawn instead, and lay it out properly
        // once the size has settled.
        BeginResizePreview(layer, size);
        _resizeTimer!.Stop();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(ResizeSettleMilliseconds);
        _resizeTimer.Start();
    }

    private Vector2 HostSize() =>
        new((float)CompositionHost.ActualWidth, (float)CompositionHost.ActualHeight);

    private void RefreshRows()
    {
        _rowsById.Clear();
        if (Rows is IEnumerable<StorageRowViewModel> rows)
        {
            foreach (StorageRowViewModel row in rows)
            {
                _rowsById[row.Id] = row;
            }
        }
    }

    private void QueueThemeRefresh()
    {
        if (!_isSetUp || _themeRefreshQueued)
        {
            return;
        }

        // Theme brushes swap one probe at a time; waiting a turn reads them all at once.
        _themeRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(RefreshTheme))
        {
            _themeRefreshQueued = false;
        }
    }

    private void RefreshTheme()
    {
        _themeRefreshQueued = false;
        if (!_isSetUp || _compositor is null)
        {
            return;
        }

        TreemapColors colors = ReadColors();
        if (!TreemapTheme.SameAs(_theme?.Colors, colors))
        {
            RebuildScene(new TreemapTheme(_compositor, colors));
        }
    }

    private void Probe_BackgroundChanged(DependencyObject sender, DependencyProperty property) =>
        QueueThemeRefresh();

    private TreemapColors ReadColors()
    {
        static Windows.UI.Color Read(Border probe) =>
            probe.Background is SolidColorBrush brush ? brush.Color : Microsoft.UI.Colors.Gray;

        return new TreemapColors(
            [
                Read(ProbeCategory0),
                Read(ProbeCategory1),
                Read(ProbeCategory2),
                Read(ProbeCategory3),
                Read(ProbeCategory4),
                Read(ProbeCategory5),
            ],
            Read(ProbeTextPrimary),
            Read(ProbeTextSecondary),
            Read(ProbeFocusInner),
            ActualTheme == ElementTheme.Light,
            new AccessibilitySettings().HighContrast,
            Read(ProbeWindow),
            Read(ProbeWindowText),
            Read(ProbeHighlight),
            Read(ProbeGrayText),
            Read(ProbeHotlight));
    }

    // The event args type only exists from Windows 10 2004, so it stays out of the signature.
    private void UISettings_AnimationsEnabledChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isSetUp && _uiSettings.AnimationsEnabled != _animationsEnabled)
            {
                _animationsEnabled = _uiSettings.AnimationsEnabled;
                RebuildScene(null);
            }
        });

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double scale = sender.RasterizationScale;
        if (scale > 0 && scale != _rasterizationScale)
        {
            // Cells are snapped to device pixels, so a new scale moves every edge a fraction.
            _rasterizationScale = scale;
            QueueRender();
        }
    }

    private void CompositionHost_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        Point point = args.GetCurrentPoint(CompositionHost).Position;
        _pointer = point;
        UpdatePointerHover(point);
    }

    private void CompositionHost_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointer = null;
        ReleasePress();
        ClearHover();
    }

    private void CompositionHost_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        PointerPoint point = args.GetCurrentPoint(CompositionHost);
        if (point.Properties.IsXButton1Pressed)
        {
            NavigateUpRequested?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            HideCard();
            Press(HitTest(point.Position));
        }
    }

    private void CompositionHost_PointerReleased(object sender, PointerRoutedEventArgs args) =>
        ReleasePress();

    private void CompositionHost_Tapped(object sender, TappedRoutedEventArgs args)
    {
        // Right after a zoom the pointer rests on a map the user has not seen settle yet; the second
        // half of a double-click must not drill into whatever happened to land under it.
        if (Environment.TickCount64 - _transitionStartedAt < TapGuardMilliseconds)
        {
            return;
        }

        Invoke(HitTest(args.GetPosition(CompositionHost)));
    }

    private void CompositionHost_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        Point point = args.GetPosition(CompositionHost);
        if (HitTest(point) is not NestedTreemapCell cell)
        {
            return;
        }

        args.Handled = true;
        HideCard();
        StorageContextMenu.Create(this, cell.Target).ShowAt(
            CompositionHost,
            new FlyoutShowOptions { Position = point });
    }

    private void Pane_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Back)
        {
            NavigateUpRequested?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
        }
    }

    private void Invoke(NestedTreemapCell? cell)
    {
        if (cell is null)
        {
            return;
        }

        // An overflow or unlisted block acts for the folder that owns it; when that folder is
        // already the scope there is nowhere further to go.
        if (cell.Kind is TreemapCellKind.Overflow or TreemapCellKind.Unlisted &&
            cell.Target.Id == _layer?.ScopeId)
        {
            return;
        }

        NodeInvoked?.Invoke(this, new StorageNodeInvokedEventArgs(cell.Target));
    }
}
