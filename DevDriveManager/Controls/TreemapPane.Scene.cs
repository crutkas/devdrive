using System.Numerics;
using DevDriveStorage;
using Microsoft.UI.Composition;
using Windows.Foundation;

namespace DevDriveManager.Controls;

/// <summary>
/// The Composition half of the treemap: one sprite per cell, nested so that a folder's children move
/// with it; implicit animations that glide cells into each new layout; and whole-map transitions
/// between scopes.
/// </summary>
public sealed partial class TreemapPane
{
    private const double MoveMilliseconds = 420;
    private const double ZoomMilliseconds = 480;
    private const double FadeInMilliseconds = 260;
    private const double FadeOutMilliseconds = 160;
    private const float TopLevelRadius = 4;
    private const float NestedRadius = 3;
    private const float MinimumZoomSide = 32;

    private readonly List<Layer> _retiringLayers = [];
    private readonly List<IDisposable> _pendingDisposals = [];
    private Compositor? _compositor;
    private ContainerVisual? _root;
    private TreemapTheme? _theme;
    private Layer? _layer;
    private bool _animationsEnabled;
    private bool _resizing;
    private long _transitionStartedAt = long.MinValue / 2;

    private CompositionEasingFunction? _moveEasing;
    private CompositionEasingFunction? _zoomEasing;
    private CompositionEasingFunction? _linearEasing;
    private ImplicitAnimationCollection? _cellImplicit;
    private ImplicitAnimationCollection? _clipImplicit;
    private ScalarKeyFrameAnimation? _fadeIn;
    private ScalarKeyFrameAnimation? _fadeOut;
    private ExpressionAnimation? _insetSize;

    private ShapeVisual? _hoverVisual;
    private CompositionSpriteShape? _hoverShape;
    private CompositionRoundedRectangleGeometry? _hoverGeometry;
    private CellVisual? _hoverHost;

    private ShapeVisual? _selectionVisual;
    private CompositionSpriteShape? _selectionOuter;
    private CompositionSpriteShape? _selectionInner;
    private CompositionRoundedRectangleGeometry? _selectionOuterGeometry;
    private CompositionRoundedRectangleGeometry? _selectionInnerGeometry;
    private CellVisual? _selectionHost;
    private bool _selectionExact;

    private CellVisual? _pressed;

    private enum SceneTransition
    {
        Cut,
        Entrance,
        ZoomIn,
        ZoomOut,
        Crossfade,
    }

    private void CreateAnimationTemplates()
    {
        Compositor compositor = _compositor!;
        TimeSpan move = TimeSpan.FromMilliseconds(MoveMilliseconds);
        _moveEasing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        _zoomEasing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        _linearEasing = compositor.CreateLinearEasingFunction();

        // Implicit animations turn every layout change into motion without the renderer tracking
        // who moved: a cell whose offset or size is set simply glides from wherever it was.
        Vector3KeyFrameAnimation offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1f, "this.FinalValue", _moveEasing);
        offset.Duration = move;
        Vector2KeyFrameAnimation size = compositor.CreateVector2KeyFrameAnimation();
        size.Target = "Size";
        size.InsertExpressionKeyFrame(1f, "this.FinalValue", _moveEasing);
        size.Duration = move;
        _cellImplicit = compositor.CreateImplicitAnimationCollection();
        _cellImplicit["Offset"] = offset;
        _cellImplicit["Size"] = size;

        // The rounded clip has to follow its cell's size, or a shrinking cell shows square corners
        // until the animation ends.
        ScalarKeyFrameAnimation right = compositor.CreateScalarKeyFrameAnimation();
        right.Target = "Right";
        right.InsertExpressionKeyFrame(1f, "this.FinalValue", _moveEasing);
        right.Duration = move;
        ScalarKeyFrameAnimation bottom = compositor.CreateScalarKeyFrameAnimation();
        bottom.Target = "Bottom";
        bottom.InsertExpressionKeyFrame(1f, "this.FinalValue", _moveEasing);
        bottom.Duration = move;
        _clipImplicit = compositor.CreateImplicitAnimationCollection();
        _clipImplicit["Right"] = right;
        _clipImplicit["Bottom"] = bottom;

        _fadeIn = compositor.CreateScalarKeyFrameAnimation();
        _fadeIn.InsertKeyFrame(0f, 0f);
        _fadeIn.InsertKeyFrame(1f, 1f, _linearEasing);
        _fadeIn.Duration = TimeSpan.FromMilliseconds(FadeInMilliseconds);
        _fadeOut = compositor.CreateScalarKeyFrameAnimation();
        _fadeOut.InsertKeyFrame(1f, 0f, _linearEasing);
        _fadeOut.Duration = TimeSpan.FromMilliseconds(FadeOutMilliseconds);

        // Keeps a ring's geometry inset from the cell it sits on while that cell's size animates.
        _insetSize = compositor.CreateExpressionAnimation(
            "Vector2(Max(host.Size.X - inset, 0), Max(host.Size.Y - inset, 0))");
    }

    private void ReleaseAnimationTemplates()
    {
        // The compositor may still be using shared templates when XAML unloads the pane.
        // Closing them here can crash native teardown; release our references instead.
        _anchorTemplates.Clear();
        _cellImplicit = null;
        _clipImplicit = null;
        _fadeIn = null;
        _fadeOut = null;
        _insetSize = null;
        _moveEasing = null;
        _zoomEasing = null;
        _linearEasing = null;
    }

    private void Render()
    {
        _throttleTimer?.Stop();
        _resizeTimer?.Stop();
        _lastRenderAt = Environment.TickCount64;
        if (!_isSetUp || _compositor is null || _root is null || _theme is null)
        {
            return;
        }

        bool rebuild = _rebuildRequested;
        bool resizing = _resizing;
        _rebuildRequested = false;
        _resizing = false;

        Vector2 size = HostSize();
        _root.Size = size;
        RefreshRows();
        StorageSnapshot? snapshot = Snapshot;
        Guid? scopeId = ResolveScopeId(snapshot);
        UpdateScopeStrip(snapshot, scopeId);
        bool hasRoom = size.X >= 1 && size.Y >= 1;
        IReadOnlyList<NestedTreemapCell> cells = snapshot is not null && scopeId is Guid id && hasRoom
            ? NestedTreemapLayout.Calculate(snapshot, id, size.X, size.Y)
            : [];
        ShowEmptyState(snapshot is not null && hasRoom && cells.Count == 0);

        if (snapshot is null || scopeId is not Guid scope || cells.Count == 0)
        {
            if (_layer is Layer stale)
            {
                _layer = null;
                RetireLayer(stale, animate: _animationsEnabled && !rebuild);
            }

            SyncChrome(reveal: null);
            FlushDisposals();
            return;
        }

        Layer? previous = _layer;
        if (!rebuild && previous is not null && previous.ScopeId == scope)
        {
            if (resizing)
            {
                if (_animationsEnabled)
                {
                    BakeResizePreview(previous, size);
                }

                previous.Container.Scale = Vector3.One;
            }

            UpdateLayer(previous, cells, size, animate: _animationsEnabled);
            SyncChrome(reveal: resizing ? TimeSpan.FromMilliseconds(MoveMilliseconds * 0.75) : null);
            FlushDisposals();
            return;
        }

        bool refocus = AccessButtonHasFocus();
        var next = new Layer(_compositor.CreateContainerVisual(), scope);
        UpdateLayer(next, cells, size, animate: false);
        _layer = next;
        SceneTransition transition = ChooseTransition(previous, next, snapshot, rebuild, size, out Rect focus);
        RunTransition(transition, previous, next, focus, size);
        SyncChrome(reveal: RevealDelay(transition));
        if (refocus)
        {
            FocusFirstAccessButton();
        }

        FlushDisposals();
    }

    private static TimeSpan RevealDelay(SceneTransition transition) => TimeSpan.FromMilliseconds(transition switch
    {
        SceneTransition.Cut => 0,
        SceneTransition.Entrance => 250,
        SceneTransition.Crossfade => 200,
        _ => ZoomMilliseconds * 0.7,
    });

    /// <summary>
    /// Brings a layer's visuals in line with a fresh layout. A cell keeps its visual — and so
    /// animates rather than pops — as long as it keeps its ID and its parent; everything else is
    /// created or retired.
    /// </summary>
    private void UpdateLayer(Layer layer, IReadOnlyList<NestedTreemapCell> cells, Vector2 size, bool animate)
    {
        layer.Container.Size = size;
        layer.LayoutSize = size;
        var kept = new Dictionary<Guid, CellVisual>(cells.Count);
        var fresh = new HashSet<CellVisual>();
        var appeared = new List<CellVisual>();
        foreach (NestedTreemapCell cell in cells)
        {
            CellVisual? parent = null;
            if (cell.ParentId is Guid parentId && !kept.TryGetValue(parentId, out parent))
            {
                continue;
            }

            if (layer.Cells.TryGetValue(cell.Id, out CellVisual? existing) &&
                !existing.Retired &&
                ReferenceEquals(existing.Parent, parent))
            {
                UpdateCell(existing, cell);
                kept[cell.Id] = existing;
                continue;
            }

            CellVisual created = CreateCell(layer, cell, parent);
            kept[cell.Id] = created;

            // Only the outermost new cell fades in; its children arrive with it.
            if (parent is null || !fresh.Contains(parent))
            {
                appeared.Add(created);
            }

            fresh.Add(created);
        }

        var retired = new List<CellVisual>();
        foreach (CellVisual visual in layer.Cells.Values)
        {
            if (!kept.TryGetValue(visual.Cell.Id, out CellVisual? survivor) || !ReferenceEquals(survivor, visual))
            {
                retired.Add(visual);
            }
        }

        layer.Cells = kept;
        layer.Ordered = cells;
        RetireCells(layer, retired, animate);
        if (animate && _fadeIn is not null)
        {
            foreach (CellVisual visual in appeared)
            {
                visual.Body.Opacity = 0;
                visual.Body.StartAnimation("Opacity", _fadeIn);
            }
        }
    }

    private CellVisual CreateCell(Layer layer, NestedTreemapCell cell, CellVisual? parent)
    {
        Compositor compositor = _compositor!;
        var visual = new CellVisual(layer, compositor.CreateSpriteVisual(), cell, parent)
        {
            Slot = parent?.Slot ?? TopLevelSlot(cell),
        };

        if (_theme!.Inner is CompositionColorBrush face)
        {
            float frame = SnapLength(1);
            SpriteVisual inner = compositor.CreateSpriteVisual();
            inner.Brush = face;
            inner.Offset = new Vector3(frame, frame, 0);
            inner.Size = new Vector2(-2 * frame, -2 * frame);
            inner.RelativeSizeAdjustment = Vector2.One;
            visual.Body.Children.InsertAtBottom(inner);
            visual.Inner = inner;
        }

        ApplyGeometry(visual, cell, force: true);
        ApplyLook(visual, cell);

        // Attached only once the first values are in, so a new cell appears where it belongs
        // instead of growing out of the corner.
        if (_animationsEnabled)
        {
            visual.Body.ImplicitAnimations = _cellImplicit;
        }

        (parent?.Body.Children ?? layer.Container.Children).InsertAtTop(visual.Body);
        return visual;
    }

    private void UpdateCell(CellVisual visual, NestedTreemapCell cell)
    {
        visual.Cell = cell;
        visual.Slot = visual.Parent?.Slot ?? TopLevelSlot(cell);
        ApplyGeometry(visual, cell, force: false);
        ApplyLook(visual, cell);
    }

    private void ApplyGeometry(CellVisual visual, NestedTreemapCell cell, bool force)
    {
        Rect snapped = Snap(cell);
        Rect origin = visual.Parent?.Snapped ?? default;
        var offset = new Vector3((float)(snapped.X - origin.X), (float)(snapped.Y - origin.Y), 0);
        var size = new Vector2((float)snapped.Width, (float)snapped.Height);
        if (force || visual.Offset != offset)
        {
            visual.Body.Offset = offset;
            visual.Offset = offset;
        }

        if (force || visual.Size != size)
        {
            visual.Body.Size = size;
            visual.Size = size;
        }

        visual.Snapped = snapped;
        ApplyClip(visual, cell.Depth, size);
        ApplyHeader(visual, cell);
    }

    private void ApplyClip(CellVisual visual, int depth, Vector2 size)
    {
        float radius = Math.Min(depth == 0 ? TopLevelRadius : NestedRadius, Math.Min(size.X, size.Y) / 4);
        if (radius < 1.5f)
        {
            if (visual.Clip is RectangleClip stale)
            {
                visual.Body.Clip = null;
                stale.Dispose();
                visual.Clip = null;
            }

            visual.Radius = 0;
            return;
        }

        var corner = new Vector2(radius);
        if (visual.Clip is not RectangleClip clip)
        {
            clip = _compositor!.CreateRectangleClip(0, 0, size.X, size.Y, corner, corner, corner, corner);
            if (_animationsEnabled)
            {
                clip.ImplicitAnimations = _clipImplicit;
            }

            visual.Body.Clip = clip;
            visual.Clip = clip;
        }
        else
        {
            if (visual.ClipSize != size)
            {
                clip.Right = size.X;
                clip.Bottom = size.Y;
            }

            if (visual.Radius != radius)
            {
                clip.TopLeftRadius = corner;
                clip.TopRightRadius = corner;
                clip.BottomRightRadius = corner;
                clip.BottomLeftRadius = corner;
            }
        }

        visual.ClipSize = size;
        visual.Radius = radius;
    }

    /// <summary>The title strip of an open folder. High contrast draws no fills, so no strip.</summary>
    private void ApplyHeader(CellVisual visual, NestedTreemapCell cell)
    {
        if (!cell.IsContainer || _theme!.IsHighContrast)
        {
            if (visual.Header is SpriteVisual stale)
            {
                visual.Body.Children.Remove(stale);
                stale.Dispose();
                visual.Header = null;
                visual.HeaderSlot = -1;
            }

            return;
        }

        float height = SnapLength(cell.HeaderHeight);
        if (visual.Header is not SpriteVisual header)
        {
            header = _compositor!.CreateSpriteVisual();
            header.RelativeSizeAdjustment = new Vector2(1, 0);
            header.Size = new Vector2(0, height);
            header.Brush = _theme.Header(visual.Slot);
            visual.Body.Children.InsertAtBottom(header);
            visual.Header = header;
            visual.HeaderSlot = visual.Slot;
            visual.HeaderHeight = height;
            return;
        }

        if (visual.HeaderHeight != height)
        {
            header.Size = new Vector2(0, height);
            visual.HeaderHeight = height;
        }

        if (visual.HeaderSlot != visual.Slot)
        {
            header.Brush = _theme.Header(visual.Slot);
            visual.HeaderSlot = visual.Slot;
        }
    }

    private void ApplyLook(CellVisual visual, NestedTreemapCell cell)
    {
        TreemapLook look = LookOf(cell);
        if (visual.BrushSlot == visual.Slot && visual.Look == look)
        {
            return;
        }

        visual.Body.Brush = _theme!.Body(visual.Slot, look);
        visual.Look = look;
        visual.BrushSlot = visual.Slot;
    }

    private static TreemapLook LookOf(NestedTreemapCell cell) => cell.Kind switch
    {
        TreemapCellKind.Overflow => TreemapLook.Overflow,
        TreemapCellKind.Unlisted => TreemapLook.Unlisted,
        TreemapCellKind.Folder => cell.IsContainer ? TreemapLook.Container : TreemapLook.FolderLeaf,
        _ => TreemapLook.FileLeaf,
    };

    /// <summary>
    /// A top-level cell takes its row's colour, so the rectangle matches the swatch in the tree; a
    /// nested cell inherits the colour of the branch it sits in.
    /// </summary>
    private int TopLevelSlot(NestedTreemapCell cell) =>
        _rowsById.TryGetValue(cell.Id, out StorageRowViewModel? row)
            ? row.ColorIndex
            : cell.Rank % StorageRowViewModel.ColorSlots;

    /// <summary>Rounds a cell to whole device pixels so neighbouring edges stay crisp at any scale.</summary>
    private Rect Snap(NestedTreemapCell cell)
    {
        double scale = _rasterizationScale > 0 ? _rasterizationScale : 1;
        double left = Math.Round(cell.X * scale) / scale;
        double top = Math.Round(cell.Y * scale) / scale;
        double right = Math.Max(left + (1 / scale), Math.Round(cell.Right * scale) / scale);
        double bottom = Math.Max(top + (1 / scale), Math.Round(cell.Bottom * scale) / scale);
        return new Rect(left, top, right - left, bottom - top);
    }

    private float SnapLength(double length)
    {
        double scale = _rasterizationScale > 0 ? _rasterizationScale : 1;
        return (float)(Math.Max(1, Math.Round(length * scale)) / scale);
    }

    private void RetireCells(Layer layer, List<CellVisual> retired, bool animate)
    {
        if (retired.Count == 0)
        {
            return;
        }

        foreach (CellVisual visual in retired)
        {
            visual.Retired = true;
        }

        DetachOverlaysIf(static visual => visual.Retired);
        List<CellVisual> outermost = retired.FindAll(visual => visual.Parent is not CellVisual parent || !parent.Retired);
        if (!animate || _compositor is null || _fadeOut is null)
        {
            foreach (CellVisual visual in outermost)
            {
                Detach(visual);
            }

            _pendingDisposals.AddRange(retired);
            return;
        }

        foreach (CellVisual visual in retired)
        {
            layer.Retiring.Add(visual);
        }

        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (CellVisual visual in outermost)
        {
            visual.Body.StartAnimation("Opacity", _fadeOut);
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            foreach (CellVisual visual in outermost)
            {
                Detach(visual);
            }

            foreach (CellVisual visual in retired)
            {
                layer.Retiring.Remove(visual);
                visual.Dispose();
            }

            batch.Dispose();
        };
    }

    private static void Detach(CellVisual visual)
    {
        if (visual.Disposed || visual.Layer.Disposed || visual.Parent?.Disposed == true)
        {
            return;
        }

        RemoveFromParent(visual.Body);
    }

    private static void RemoveFromParent(Visual visual)
    {
        if (visual.Parent is ContainerVisual parent)
        {
            parent.Children.Remove(visual);
        }
    }

    private void RetireLayer(Layer layer, bool animate)
    {
        DetachOverlaysIf(visual => ReferenceEquals(visual.Layer, layer));
        if (!animate || _compositor is null)
        {
            RemoveFromParent(layer.Container);
            _pendingDisposals.Add(layer);
            return;
        }

        _retiringLayers.Add(layer);
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        AnimateScalar(layer.Container, "Opacity", [(1f, 0f)], TimeSpan.FromMilliseconds(180));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (_retiringLayers.Remove(layer))
            {
                DisposeLayer(layer);
            }

            batch.Dispose();
        };
    }

    private void DisposeLayer(Layer layer)
    {
        if (layer.Disposed)
        {
            return;
        }

        DetachOverlaysIf(visual => ReferenceEquals(visual.Layer, layer));
        RemoveFromParent(layer.Container);
        layer.Dispose();
    }

    /// <summary>
    /// Disposal waits for the end of a render: label expressions are only re-pointed at the new
    /// visuals once the chrome has been synchronised, and must never reference a closed one.
    /// </summary>
    private void FlushDisposals()
    {
        foreach (IDisposable disposable in _pendingDisposals)
        {
            disposable.Dispose();
        }

        _pendingDisposals.Clear();
    }

    private void DisposeScene()
    {
        _pressed = null;
        ClearHover();
        DetachSelection();
        ClearLabels();
        foreach (Layer layer in _retiringLayers)
        {
            DisposeLayer(layer);
        }

        _retiringLayers.Clear();
        if (_layer is Layer current)
        {
            _layer = null;
            DisposeLayer(current);
        }

        FlushDisposals();
        _hoverVisual?.Dispose();
        _hoverShape?.Dispose();
        _hoverGeometry?.Dispose();
        _selectionVisual?.Dispose();
        _selectionOuter?.Dispose();
        _selectionInner?.Dispose();
        _selectionOuterGeometry?.Dispose();
        _selectionInnerGeometry?.Dispose();
        _hoverVisual = null;
        _hoverShape = null;
        _hoverGeometry = null;
        _selectionVisual = null;
        _selectionOuter = null;
        _selectionInner = null;
        _selectionOuterGeometry = null;
        _selectionInnerGeometry = null;
    }

    /// <summary>
    /// Throws the visuals away and draws the scope again from scratch: brushes, implicit animations
    /// and pixel snapping are all baked into the visuals when they are created.
    /// </summary>
    private void RebuildScene(TreemapTheme? theme)
    {
        DisposeScene();
        if (theme is not null)
        {
            _theme?.Dispose();
            _theme = theme;
        }

        _rebuildRequested = true;
        Render();
    }

    private SceneTransition ChooseTransition(
        Layer? previous,
        Layer next,
        StorageSnapshot snapshot,
        bool forced,
        Vector2 size,
        out Rect focus)
    {
        focus = default;
        if (!_animationsEnabled || forced)
        {
            return SceneTransition.Cut;
        }

        if (previous is null)
        {
            return SceneTransition.Entrance;
        }

        // Drilling in: the old map already shows where the new scope lives, so zoom into that spot.
        IReadOnlyList<StorageNode> nextChain = snapshot.AncestorsAndSelf(next.ScopeId);
        if (IndexOf(nextChain, previous.ScopeId) >= 0)
        {
            if (DeepestDrawn(previous, nextChain) is CellVisual target)
            {
                focus = Widen(target.Snapped, size);
                return SceneTransition.ZoomIn;
            }

            return SceneTransition.Crossfade;
        }

        // Backing out: the new map shows where the old scope lives, so shrink back into it.
        IReadOnlyList<StorageNode> previousChain = snapshot.AncestorsAndSelf(previous.ScopeId);
        if (IndexOf(previousChain, next.ScopeId) >= 0 && DeepestDrawn(next, previousChain) is CellVisual source)
        {
            focus = Widen(source.Snapped, size);
            return SceneTransition.ZoomOut;
        }

        return SceneTransition.Crossfade;
    }

    private static CellVisual? DeepestDrawn(Layer layer, IReadOnlyList<StorageNode> chain)
    {
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            if (layer.Cells.TryGetValue(chain[index].Id, out CellVisual? visual))
            {
                return visual;
            }
        }

        return null;
    }

    private static Rect Widen(Rect focus, Vector2 size)
    {
        double width = Math.Max(focus.Width, Math.Min(MinimumZoomSide, size.X));
        double height = Math.Max(focus.Height, Math.Min(MinimumZoomSide, size.Y));
        double x = Math.Clamp(focus.X + ((focus.Width - width) / 2), 0, Math.Max(0, size.X - width));
        double y = Math.Clamp(focus.Y + ((focus.Height - height) / 2), 0, Math.Max(0, size.Y - height));
        return new Rect(x, y, width, height);
    }

    private static Vector2 ZoomFactor(Rect focus, Vector2 size) =>
        new(size.X / (float)Math.Max(1, focus.Width), size.Y / (float)Math.Max(1, focus.Height));

    private void RunTransition(SceneTransition transition, Layer? previous, Layer next, Rect focus, Vector2 size)
    {
        ContainerVisual root = _root!;
        foreach (Layer stale in _retiringLayers)
        {
            DisposeLayer(stale);
        }

        _retiringLayers.Clear();
        ReleasePress();
        ClearHover();
        _transitionStartedAt = Environment.TickCount64;

        if (previous is null || transition is SceneTransition.Cut or SceneTransition.Entrance)
        {
            root.Children.InsertAtTop(next.Container);
            if (previous is not null)
            {
                DetachOverlaysIf(visual => ReferenceEquals(visual.Layer, previous));
                RemoveFromParent(previous.Container);
                _pendingDisposals.Add(previous);
            }

            if (transition == SceneTransition.Entrance)
            {
                PlayEntrance(next);
            }

            return;
        }

        DetachOverlaysIf(visual => ReferenceEquals(visual.Layer, previous));
        CompositionScopedBatch batch = _compositor!.CreateScopedBatch(CompositionBatchTypes.Animation);
        switch (transition)
        {
            case SceneTransition.ZoomIn:
                ZoomIn(previous, next, focus, size);
                break;
            case SceneTransition.ZoomOut:
                ZoomOut(previous, next, focus, size);
                break;
            default:
                Crossfade(previous, next, size);
                break;
        }

        batch.End();
        _retiringLayers.Add(previous);
        batch.Completed += (_, _) =>
        {
            if (_retiringLayers.Remove(previous))
            {
                DisposeLayer(previous);
            }

            batch.Dispose();
        };
    }

    /// <summary>
    /// The old map scales up until the chosen cell fills the pane while the new map, which is that
    /// cell's contents, grows out of the same rectangle — so the eye never loses its place.
    /// </summary>
    private void ZoomIn(Layer previous, Layer next, Rect focus, Vector2 size)
    {
        _root!.Children.InsertAtTop(next.Container);
        Vector2 zoom = ZoomFactor(focus, size);
        TimeSpan duration = TimeSpan.FromMilliseconds(ZoomMilliseconds);
        var focusOffset = new Vector3((float)focus.X, (float)focus.Y, 0);

        previous.Container.CenterPoint = Vector3.Zero;
        AnimateVector3(previous.Container, "Scale", new Vector3(zoom, 1), duration, _zoomEasing!);
        AnimateVector3(previous.Container, "Offset", -focusOffset * new Vector3(zoom, 1), duration, _zoomEasing!);
        AnimateScalar(previous.Container, "Opacity", [(0.2f, 1f), (0.6f, 0f)], duration);

        next.Container.CenterPoint = Vector3.Zero;
        next.Container.Scale = new Vector3(1 / zoom.X, 1 / zoom.Y, 1);
        next.Container.Offset = focusOffset;
        next.Container.Opacity = 0;
        AnimateVector3(next.Container, "Scale", Vector3.One, duration, _zoomEasing!);
        AnimateVector3(next.Container, "Offset", Vector3.Zero, duration, _zoomEasing!);
        AnimateScalar(next.Container, "Opacity", [(0.45f, 1f)], duration);
    }

    /// <summary>The reverse of <see cref="ZoomIn"/>: the old map shrinks back into its own cell.</summary>
    private void ZoomOut(Layer previous, Layer next, Rect focus, Vector2 size)
    {
        _root!.Children.InsertBelow(next.Container, previous.Container);
        Vector2 zoom = ZoomFactor(focus, size);
        TimeSpan duration = TimeSpan.FromMilliseconds(ZoomMilliseconds);
        var focusOffset = new Vector3((float)focus.X, (float)focus.Y, 0);

        next.Container.CenterPoint = Vector3.Zero;
        next.Container.Scale = new Vector3(zoom, 1);
        next.Container.Offset = -focusOffset * new Vector3(zoom, 1);
        next.Container.Opacity = 0;
        AnimateVector3(next.Container, "Scale", Vector3.One, duration, _zoomEasing!);
        AnimateVector3(next.Container, "Offset", Vector3.Zero, duration, _zoomEasing!);
        AnimateScalar(next.Container, "Opacity", [(0.35f, 1f)], duration);

        previous.Container.CenterPoint = Vector3.Zero;
        AnimateVector3(previous.Container, "Scale", new Vector3(1 / zoom.X, 1 / zoom.Y, 1), duration, _zoomEasing!);
        AnimateVector3(previous.Container, "Offset", focusOffset, duration, _zoomEasing!);
        AnimateScalar(previous.Container, "Opacity", [(0.4f, 1f), (1f, 0f)], duration);
    }

    private void Crossfade(Layer previous, Layer next, Vector2 size)
    {
        _root!.Children.InsertAtTop(next.Container);
        var center = new Vector3(size / 2, 0);
        previous.Container.CenterPoint = center;
        AnimateScalar(previous.Container, "Opacity", [(1f, 0f)], TimeSpan.FromMilliseconds(180));
        AnimateVector3(previous.Container, "Scale", new Vector3(0.985f, 0.985f, 1), TimeSpan.FromMilliseconds(180), _moveEasing!);

        next.Container.CenterPoint = center;
        next.Container.Opacity = 0;
        next.Container.Scale = new Vector3(1.015f, 1.015f, 1);
        AnimateScalar(next.Container, "Opacity", [(1f, 1f)], TimeSpan.FromMilliseconds(280), TimeSpan.FromMilliseconds(60));
        AnimateVector3(next.Container, "Scale", Vector3.One, TimeSpan.FromMilliseconds(380), _moveEasing!);
    }

    /// <summary>The first map of a scan or a visit settles in largest block first.</summary>
    private void PlayEntrance(Layer layer)
    {
        foreach (NestedTreemapCell cell in layer.Ordered)
        {
            if (cell.Depth > 0)
            {
                break;
            }

            if (!layer.Cells.TryGetValue(cell.Id, out CellVisual? visual))
            {
                continue;
            }

            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(cell.Rank * 16, 240));
            visual.Body.CenterPoint = new Vector3(visual.Size / 2, 0);
            visual.Body.Opacity = 0;
            visual.Body.Scale = new Vector3(0.92f, 0.92f, 1);
            AnimateScalar(visual.Body, "Opacity", [(1f, 1f)], TimeSpan.FromMilliseconds(300), delay);
            AnimateVector3(visual.Body, "Scale", Vector3.One, TimeSpan.FromMilliseconds(MoveMilliseconds), _moveEasing!, delay);
        }
    }

    private void BeginResizePreview(Layer layer, Vector2 size)
    {
        foreach (Layer retiring in _retiringLayers)
        {
            DisposeLayer(retiring);
        }

        _retiringLayers.Clear();
        ContainerVisual container = layer.Container;
        container.StopAnimation("Scale");
        container.StopAnimation("Offset");
        container.StopAnimation("Opacity");
        container.CenterPoint = Vector3.Zero;
        container.Offset = Vector3.Zero;
        container.Opacity = 1;
        container.Scale = new Vector3(size.X / layer.LayoutSize.X, size.Y / layer.LayoutSize.Y, 1);
        _resizing = true;
        ReleasePress();
        ClearHover();
        HideLabels();
    }

    /// <summary>
    /// Writes the stretched preview into the cells themselves, with their implicit animations
    /// briefly detached, so dropping the container's stretch changes nothing on screen and each
    /// cell then glides from exactly where it appears to its place in the proper layout.
    /// </summary>
    private void BakeResizePreview(Layer layer, Vector2 size)
    {
        if (layer.LayoutSize.X <= 0 || layer.LayoutSize.Y <= 0)
        {
            return;
        }

        var stretch = new Vector2(size.X / layer.LayoutSize.X, size.Y / layer.LayoutSize.Y);
        foreach (CellVisual visual in layer.Cells.Values)
        {
            visual.Body.ImplicitAnimations = null;
            visual.Offset = new Vector3(visual.Offset.X * stretch.X, visual.Offset.Y * stretch.Y, 0);
            visual.Size *= stretch;
            visual.Body.Offset = visual.Offset;
            visual.Body.Size = visual.Size;
            visual.Body.ImplicitAnimations = _cellImplicit;
            if (visual.Clip is RectangleClip clip)
            {
                clip.ImplicitAnimations = null;
                clip.Right = visual.Size.X;
                clip.Bottom = visual.Size.Y;
                clip.ImplicitAnimations = _clipImplicit;
                visual.ClipSize = visual.Size;
            }
        }
    }

    /// <summary>The deepest cell under a point, in pane coordinates.</summary>
    private NestedTreemapCell? HitTest(Point point)
    {
        if (_resizing || _layer is not Layer layer)
        {
            return null;
        }

        // Cells are ordered parents first, so the last match is the most specific one.
        IReadOnlyList<NestedTreemapCell> cells = layer.Ordered;
        for (int index = cells.Count - 1; index >= 0; index--)
        {
            NestedTreemapCell cell = cells[index];
            if (cell.Contains(point.X, point.Y) && layer.Cells.ContainsKey(cell.Id))
            {
                return cell;
            }
        }

        return null;
    }

    private CellVisual? VisualOf(Guid id) =>
        _layer is Layer layer && layer.Cells.TryGetValue(id, out CellVisual? visual) ? visual : null;

    private NestedTreemapCell? CellOf(Guid id) => VisualOf(id)?.Cell;

    private void Press(NestedTreemapCell? cell)
    {
        ReleasePress();
        if (!_animationsEnabled || cell is null || VisualOf(cell.Id) is not CellVisual visual)
        {
            return;
        }

        // Deeper for small cells, so the press reads at every size without jolting a large block.
        _pressed = visual;
        float side = Math.Max(1, Math.Min(visual.Size.X, visual.Size.Y));
        float scale = Math.Clamp(1 - (3 / side), 0.94f, 0.985f);
        visual.Body.CenterPoint = new Vector3(visual.Size / 2, 0);
        AnimateVector3(visual.Body, "Scale", new Vector3(scale, scale, 1), TimeSpan.FromMilliseconds(80), _moveEasing!);
    }

    private void ReleasePress()
    {
        if (_pressed is not CellVisual visual)
        {
            return;
        }

        _pressed = null;
        if (!visual.Disposed && _moveEasing is not null)
        {
            AnimateVector3(visual.Body, "Scale", Vector3.One, TimeSpan.FromMilliseconds(160), _moveEasing);
        }
    }

    /// <summary>
    /// Moves the one hover ring onto a cell. The ring is a child of the cell it marks, so it follows
    /// that cell through every re-layout without being told.
    /// </summary>
    private void SetHover(NestedTreemapCell? cell)
    {
        CellVisual? host = cell is null ? null : VisualOf(cell.Id);
        if (ReferenceEquals(host, _hoverHost))
        {
            return;
        }

        DetachHover();
        if (host is null || _compositor is null || _theme is null || _insetSize is null)
        {
            return;
        }

        if (_hoverVisual is null)
        {
            _hoverGeometry = _compositor.CreateRoundedRectangleGeometry();
            _hoverShape = _compositor.CreateSpriteShape(_hoverGeometry);
            _hoverShape.StrokeBrush = _theme.HoverStroke;
            _hoverShape.StrokeThickness = _theme.HoverStrokeThickness;
            _hoverVisual = _compositor.CreateShapeVisual();
            _hoverVisual.RelativeSizeAdjustment = Vector2.One;
            _hoverVisual.Shapes.Add(_hoverShape);
        }

        float thickness = _theme.HoverStrokeThickness;
        _hoverShape!.FillBrush = host.Cell.IsContainer || _theme.IsHighContrast ? null : _theme.HoverFill;
        _hoverGeometry!.Offset = new Vector2(thickness / 2);
        _hoverGeometry.CornerRadius = new Vector2(Math.Max(0, host.Radius - (thickness / 2)));
        _insetSize.SetReferenceParameter("host", host.Body);
        _insetSize.SetScalarParameter("inset", thickness);
        _hoverGeometry.StartAnimation("Size", _insetSize);
        if (ReferenceEquals(_selectionHost, host) && _selectionVisual is not null)
        {
            host.Body.Children.InsertBelow(_hoverVisual, _selectionVisual);
        }
        else
        {
            host.Body.Children.InsertAtTop(_hoverVisual);
        }

        _hoverHost = host;
        if (_animationsEnabled)
        {
            _hoverVisual.Opacity = 0;
            AnimateScalar(_hoverVisual, "Opacity", [(1f, 1f)], TimeSpan.FromMilliseconds(100));
        }
        else
        {
            _hoverVisual.Opacity = 1;
        }
    }

    private void DetachHover()
    {
        if (_hoverHost is CellVisual host && _hoverVisual is not null)
        {
            _hoverGeometry?.StopAnimation("Size");
            _hoverVisual.StopAnimation("Opacity");
            if (!host.Disposed)
            {
                host.Body.Children.Remove(_hoverVisual);
            }
        }

        _hoverHost = null;
    }

    /// <summary>
    /// Rings the selected item. When the selection itself is not drawn — too small, or folded away —
    /// the ring goes on the deepest drawn cell that contains it, dashed to say so.
    /// </summary>
    private void ApplySelection(bool animate)
    {
        if (!_isSetUp || _compositor is null || _theme is null || _insetSize is null)
        {
            return;
        }

        (CellVisual? host, bool exact) = ResolveSelection();
        if (host is null)
        {
            DetachSelection();
            return;
        }

        if (ReferenceEquals(host, _selectionHost) && exact == _selectionExact)
        {
            return;
        }

        DetachSelection();
        if (_selectionVisual is null)
        {
            _selectionOuterGeometry = _compositor.CreateRoundedRectangleGeometry();
            _selectionOuterGeometry.Offset = new Vector2(1);
            _selectionOuter = _compositor.CreateSpriteShape(_selectionOuterGeometry);
            _selectionOuter.StrokeBrush = _theme.SelectionOuter;
            _selectionOuter.StrokeThickness = 2;
            _selectionInnerGeometry = _compositor.CreateRoundedRectangleGeometry();
            _selectionInnerGeometry.Offset = new Vector2(2.5f);
            _selectionInner = _compositor.CreateSpriteShape(_selectionInnerGeometry);
            _selectionInner.StrokeBrush = _theme.SelectionInner;
            _selectionInner.StrokeThickness = 1;
            _selectionVisual = _compositor.CreateShapeVisual();
            _selectionVisual.RelativeSizeAdjustment = Vector2.One;
            _selectionVisual.Shapes.Add(_selectionOuter);
            _selectionVisual.Shapes.Add(_selectionInner);
        }

        _selectionOuter!.StrokeDashArray.Clear();
        if (!exact)
        {
            _selectionOuter.StrokeDashArray.Add(2f);
            _selectionOuter.StrokeDashArray.Add(1.5f);
        }

        _selectionOuterGeometry!.CornerRadius = new Vector2(Math.Max(0, host.Radius - 1));
        _selectionInnerGeometry!.CornerRadius = new Vector2(Math.Max(0, host.Radius - 2.5f));
        _insetSize.SetReferenceParameter("host", host.Body);
        _insetSize.SetScalarParameter("inset", 2);
        _selectionOuterGeometry.StartAnimation("Size", _insetSize);
        _insetSize.SetScalarParameter("inset", 5);
        _selectionInnerGeometry.StartAnimation("Size", _insetSize);
        host.Body.Children.InsertAtTop(_selectionVisual);
        _selectionHost = host;
        _selectionExact = exact;

        _selectionVisual.StopAnimation("Scale");
        _selectionVisual.StopAnimation("Opacity");
        if (animate && _animationsEnabled && _moveEasing is not null)
        {
            _selectionVisual.CenterPoint = new Vector3(host.Size / 2, 0);
            _selectionVisual.Scale = new Vector3(0.97f, 0.97f, 1);
            _selectionVisual.Opacity = 0;
            AnimateVector3(_selectionVisual, "Scale", Vector3.One, TimeSpan.FromMilliseconds(180), _moveEasing);
            AnimateScalar(_selectionVisual, "Opacity", [(1f, 1f)], TimeSpan.FromMilliseconds(180));
        }
        else
        {
            _selectionVisual.Scale = Vector3.One;
            _selectionVisual.Opacity = 1;
        }
    }

    private (CellVisual? Host, bool Exact) ResolveSelection()
    {
        if (_layer is not Layer layer || SelectedRow?.Id is not Guid selected)
        {
            return (null, false);
        }

        if (layer.Cells.TryGetValue(selected, out CellVisual? exact))
        {
            return (exact, true);
        }

        if (selected == layer.ScopeId || Snapshot is not StorageSnapshot snapshot)
        {
            return (null, false);
        }

        IReadOnlyList<StorageNode> chain = snapshot.AncestorsAndSelf(selected);
        int start = IndexOf(chain, layer.ScopeId);
        if (start < 0)
        {
            return (null, false);
        }

        CellVisual? host = null;
        for (int index = start + 1; index < chain.Count; index++)
        {
            if (layer.Cells.TryGetValue(chain[index].Id, out CellVisual? step))
            {
                host = step;
                continue;
            }

            // The next step down is not drawn; if its folder folded it away, mark that block.
            if (layer.Cells.TryGetValue(NestedTreemapLayout.OverflowIdOf(chain[index - 1].Id), out CellVisual? overflow))
            {
                host = overflow;
            }

            break;
        }

        return (host, false);
    }

    private void DetachSelection()
    {
        if (_selectionHost is CellVisual host && _selectionVisual is not null)
        {
            _selectionOuterGeometry?.StopAnimation("Size");
            _selectionInnerGeometry?.StopAnimation("Size");
            if (!host.Disposed)
            {
                host.Body.Children.Remove(_selectionVisual);
            }
        }

        _selectionHost = null;
    }

    /// <summary>Cells added under a ringed cell land on top of its rings; put the rings back above.</summary>
    private void RaiseOverlays()
    {
        if (_hoverHost is CellVisual hover && _hoverVisual is not null && !hover.Disposed)
        {
            hover.Body.Children.Remove(_hoverVisual);
            hover.Body.Children.InsertAtTop(_hoverVisual);
        }

        if (_selectionHost is CellVisual selection && _selectionVisual is not null && !selection.Disposed)
        {
            selection.Body.Children.Remove(_selectionVisual);
            selection.Body.Children.InsertAtTop(_selectionVisual);
        }
    }

    /// <summary>Overlays must leave a cell before it is disposed, or they go down with it.</summary>
    private void DetachOverlaysIf(Func<CellVisual, bool> predicate)
    {
        if (_hoverHost is CellVisual hover && predicate(hover))
        {
            DetachHover();
        }

        if (_selectionHost is CellVisual selection && predicate(selection))
        {
            DetachSelection();
        }

        if (_pressed is CellVisual pressed && predicate(pressed))
        {
            _pressed = null;
        }
    }

    private void AnimateVector3(
        CompositionObject target,
        string property,
        Vector3 value,
        TimeSpan duration,
        CompositionEasingFunction easing,
        TimeSpan delay = default)
    {
        Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, value, easing);
        animation.Duration = duration;
        if (delay > TimeSpan.Zero)
        {
            animation.DelayTime = delay;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        target.StartAnimation(property, animation);
    }

    private void AnimateScalar(
        CompositionObject target,
        string property,
        ReadOnlySpan<(float Progress, float Value)> frames,
        TimeSpan duration,
        TimeSpan delay = default)
    {
        ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
        foreach ((float progress, float value) in frames)
        {
            animation.InsertKeyFrame(progress, value, _linearEasing!);
        }

        animation.Duration = duration;
        if (delay > TimeSpan.Zero)
        {
            animation.DelayTime = delay;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        target.StartAnimation(property, animation);
    }

    private static int IndexOf(IReadOnlyList<StorageNode> chain, Guid id)
    {
        for (int index = 0; index < chain.Count; index++)
        {
            if (chain[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>One scope's cells under a single container, so the whole map can zoom or fade as one.</summary>
    private sealed class Layer(ContainerVisual container, Guid scopeId) : IDisposable
    {
        public ContainerVisual Container { get; } = container;

        public Guid ScopeId { get; } = scopeId;

        public Dictionary<Guid, CellVisual> Cells { get; set; } = [];

        public IReadOnlyList<NestedTreemapCell> Ordered { get; set; } = [];

        /// <summary>Cells fading out; still in the tree, but no longer part of the layout.</summary>
        public HashSet<CellVisual> Retiring { get; } = [];

        public Vector2 LayoutSize { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            foreach (CellVisual cell in Retiring)
            {
                cell.Dispose();
            }

            foreach (CellVisual cell in Cells.Values)
            {
                cell.Dispose();
            }

            Retiring.Clear();
            Cells.Clear();
            Container.Dispose();
        }
    }

    private sealed class CellVisual(Layer layer, SpriteVisual body, NestedTreemapCell cell, CellVisual? parent)
        : IDisposable
    {
        public Layer Layer { get; } = layer;

        public SpriteVisual Body { get; } = body;

        public CellVisual? Parent { get; } = parent;

        public NestedTreemapCell Cell { get; set; } = cell;

        public SpriteVisual? Header { get; set; }

        /// <summary>High contrast only: the window-coloured face inside the one-pixel frame.</summary>
        public SpriteVisual? Inner { get; set; }

        public RectangleClip? Clip { get; set; }

        /// <summary>The pixel-snapped rectangle in pane coordinates.</summary>
        public Rect Snapped { get; set; }

        /// <summary>The offset last written to <see cref="Body"/>, relative to its parent.</summary>
        public Vector3 Offset { get; set; }

        /// <summary>The size last written to <see cref="Body"/>.</summary>
        public Vector2 Size { get; set; }

        public Vector2 ClipSize { get; set; }

        public float Radius { get; set; }

        public float HeaderHeight { get; set; }

        public int HeaderSlot { get; set; } = -1;

        public int Slot { get; set; }

        public int BrushSlot { get; set; } = -1;

        public TreemapLook Look { get; set; }

        public bool Retired { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            Clip?.Dispose();
            Header?.Dispose();
            Inner?.Dispose();
            Body.Dispose();
        }
    }
}
