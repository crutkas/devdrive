using System.Globalization;
using System.Numerics;
using DevDriveStorage;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DevDriveManager.Controls;

/// <summary>
/// The XAML drawn over the Composition scene: text labels pinned to their cells, focusable
/// stand-ins for keyboard and automation, and the card that describes the cell under the pointer.
/// </summary>
public sealed partial class TreemapPane
{
    private const int MaxLabels = 320;
    private const float LabelInset = 6;
    private const float LabelLineHeight = 16;
    private const double HeaderLabelMinWidth = 40;
    private const double HeaderSizeMinWidth = 120;
    private const double LeafLabelMinWidth = 44;
    private const double LeafLabelMinHeight = 20;
    private const double LeafSizeMinHeight = 36;
    private const int DwellMilliseconds = 380;

    private readonly Dictionary<Guid, CellLabel> _labels = [];
    private readonly Dictionary<Guid, Button> _accessButtons = [];
    private readonly Dictionary<int, ExpressionAnimation> _anchorTemplates = [];
    private Visual? _labelsVisual;
    private bool _labelsHidden;
    private DispatcherQueueTimer? _dwellTimer;
    private Guid? _cardId;
    private Guid? _dwellId;
    private Guid? _stripScopeId;
    private Point _cardAnchor;
    private bool _cardShown;

    /// <summary>
    /// Brings everything drawn over the visuals in line with the current layer. With a reveal delay
    /// the labels stay hidden until the scene has finished moving; without one this is an in-place
    /// update and whatever is hovered or described stays so.
    /// </summary>
    private void SyncChrome(TimeSpan? reveal)
    {
        if (reveal is not null)
        {
            HideLabels();
        }

        SyncLabels();
        SyncAccessButtons();
        ApplySelection(animate: false);
        RaiseOverlays();
        if (reveal is TimeSpan delay)
        {
            ClearHover();
            RevealLabels(delay);
            return;
        }

        if (_labelsHidden)
        {
            RevealLabels(TimeSpan.Zero);
        }

        RefreshHover();
        RefreshCard();
    }

    private void ShowEmptyState(bool show) =>
        TreemapEmptyState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Titles the map with its scope. Below the root, the parent folder sits in the strip as the
    /// way back out; at the root, a drive glyph takes its place.
    /// </summary>
    private void UpdateScopeStrip(StorageSnapshot? snapshot, Guid? scopeId)
    {
        StorageNode? scope = snapshot is not null && scopeId is Guid id ? snapshot.Find(id) : null;
        ScopeStrip.Opacity = scope is null ? 0 : 1;
        ScopeStrip.IsHitTestVisible = scope is not null;
        if (scope is null)
        {
            _stripScopeId = null;
            return;
        }

        SetText(ScopeSize, scope.SizeDisplay);
        if (_stripScopeId == scope.Id)
        {
            return;
        }

        _stripScopeId = scope.Id;
        SetText(ScopeName, scope.Name);
        ToolTipService.SetToolTip(ScopeName, scope.PhysicalPath);
        AutomationProperties.SetName(ScopeName, $"Showing {scope.Name}");

        StorageNode? parent = scope.ParentId is Guid parentId ? snapshot!.Find(parentId) : null;
        if (parent is null)
        {
            // Collapsing a focused button would drop keyboard focus out of the map entirely.
            bool hadFocus = ParentButton.FocusState != FocusState.Unfocused;
            ParentButton.Visibility = Visibility.Collapsed;
            ScopeGlyph.Glyph = "\uEDA2";
            ScopeGlyph.Margin = new Thickness(5, 0, 0, 0);
            if (hadFocus)
            {
                FocusFirstAccessButton();
            }

            return;
        }

        string upTo = $"Up to {parent.Name}";
        SetText(ParentName, parent.Name);
        AutomationProperties.SetName(ParentButton, upTo);
        ToolTipService.SetToolTip(ParentButton, $"{upTo} (Backspace)");
        ParentButton.Visibility = Visibility.Visible;
        ScopeGlyph.Glyph = "\uE76C";
        ScopeGlyph.Margin = default;
    }

    private static void SetText(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            block.Text = text;
        }
    }

    private void ParentButton_Click(object sender, RoutedEventArgs args) =>
        NavigateUpRequested?.Invoke(this, EventArgs.Empty);

    private void SyncLabels()
    {
        var wanted = new List<CellVisual>();
        if (_layer is Layer layer)
        {
            foreach (NestedTreemapCell cell in layer.Ordered)
            {
                if (IsLabelled(cell) && layer.Cells.TryGetValue(cell.Id, out CellVisual? visual))
                {
                    wanted.Add(visual);
                }
            }

            // Text is the expensive part of the map; past the cap, the outer and larger cells win.
            if (wanted.Count > MaxLabels)
            {
                wanted =
                [
                    .. wanted
                        .OrderBy(static visual => visual.Cell.Depth)
                        .ThenByDescending(static visual => visual.Cell.Width * visual.Cell.Height)
                        .Take(MaxLabels),
                ];
            }
        }

        var keep = new HashSet<Guid>(wanted.Count);
        foreach (CellVisual visual in wanted)
        {
            keep.Add(visual.Cell.Id);
            BindLabel(visual);
        }

        foreach (Guid id in _labels.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            RemoveLabel(id);
        }
    }

    private static bool IsLabelled(NestedTreemapCell cell) => cell.IsContainer
        ? cell.Width >= HeaderLabelMinWidth
        : cell.Width >= LeafLabelMinWidth && cell.Height >= LeafLabelMinHeight;

    private void BindLabel(CellVisual visual)
    {
        NestedTreemapCell cell = visual.Cell;
        if (_labels.TryGetValue(cell.Id, out CellLabel? label) && label.IsHeader != cell.IsContainer)
        {
            RemoveLabel(cell.Id);
            label = null;
        }

        label ??= CreateLabel(cell.Id, cell.IsContainer);
        UpdateLabelContent(label, visual);
        UpdateLabelAnchor(label, visual);
    }

    /// <summary>An open folder is titled in its header strip; anything else in its top-left corner.</summary>
    private CellLabel CreateLabel(Guid id, bool isHeader)
    {
        TextBlock name = CreateLabelText(12);
        TextBlock size = CreateLabelText(11);
        Panel root;
        if (isHeader)
        {
            var grid = new Grid { ColumnSpacing = 8, Height = LabelLineHeight };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(size, 1);
            grid.Children.Add(name);
            grid.Children.Add(size);
            root = grid;
        }
        else
        {
            var panel = new StackPanel();
            panel.Children.Add(name);
            panel.Children.Add(size);
            root = panel;
        }

        LabelCanvas.Children.Add(root);
        ElementCompositionPreview.SetIsTranslationEnabled(root, true);
        var label = new CellLabel(root, name, size, isHeader, ElementCompositionPreview.GetElementVisual(root));
        _labels[id] = label;
        return label;
    }

    /// <summary>
    /// Labels have to fit the geometry of their cell, so they opt out of text scaling; the card and
    /// the stand-ins carry the same text at the user's size.
    /// </summary>
    private static TextBlock CreateLabelText(double fontSize)
    {
        var text = new TextBlock
        {
            FontSize = fontSize,
            IsTextScaleFactorEnabled = false,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        return text;
    }

    private void UpdateLabelContent(CellLabel label, CellVisual visual)
    {
        NestedTreemapCell cell = visual.Cell;
        if (!string.Equals(label.NameText, cell.Name, StringComparison.Ordinal))
        {
            label.NameBlock.Text = cell.Name;
            label.NameText = cell.Name;
        }

        bool showSize = label.IsHeader ? cell.Width >= HeaderSizeMinWidth : cell.Height >= LeafSizeMinHeight;
        string sizeText = showSize ? ByteSizeFormatter.Format(cell.SizeBytes) : string.Empty;
        if (!string.Equals(label.SizeText, sizeText, StringComparison.Ordinal))
        {
            label.SizeBlock.Text = sizeText;
            label.SizeBlock.Visibility = showSize ? Visibility.Visible : Visibility.Collapsed;
            label.SizeText = sizeText;
        }

        double width = Math.Max(0, visual.Snapped.Width - (2 * LabelInset));
        if (label.Width != width)
        {
            label.Root.Width = width;
            label.Width = width;
        }

        bool strong = cell.Depth == 0;
        if (label.Strong != strong)
        {
            label.NameBlock.FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal;
            label.Strong = strong;
        }

        if (label.InkSlot != visual.Slot || label.InkLook != visual.Look)
        {
            label.NameBlock.Foreground = _theme!.Ink(visual.Slot, visual.Look, secondary: false);
            label.SizeBlock.Foreground = _theme.Ink(visual.Slot, visual.Look, secondary: true);
            label.InkSlot = visual.Slot;
            label.InkLook = visual.Look;
        }
    }

    /// <summary>
    /// Pins a label to its cell with an expression over the offsets of the cell and its ancestors,
    /// so the text rides along with every glide the Composition scene makes on its own.
    /// </summary>
    private void UpdateLabelAnchor(CellLabel label, CellVisual visual)
    {
        float top = label.IsHeader
            ? MathF.Max(0, ((float)visual.Cell.HeaderHeight - LabelLineHeight) / 2)
            : 4;
        var local = new Vector3(LabelInset, top, 0);
        if (ReferenceEquals(label.Anchor, visual) && label.Local == local)
        {
            return;
        }

        int length = 0;
        for (CellVisual? step = visual; step is not null; step = step.Parent)
        {
            length++;
        }

        ExpressionAnimation template = AnchorTemplate(length);
        template.ClearAllParameters();
        int index = 0;
        for (CellVisual? step = visual; step is not null; step = step.Parent)
        {
            template.SetReferenceParameter(AnchorParameter(index++), step.Body);
        }

        template.SetVector3Parameter("local", local);
        label.Handoff.StartAnimation("Translation", template);
        label.Anchor = visual;
        label.Local = local;
    }

    private ExpressionAnimation AnchorTemplate(int length)
    {
        if (!_anchorTemplates.TryGetValue(length, out ExpressionAnimation? template))
        {
            IEnumerable<string> terms = Enumerable.Range(0, length).Select(static index => $"{AnchorParameter(index)}.Offset");
            template = _compositor!.CreateExpressionAnimation(string.Join(" + ", terms) + " + local");
            _anchorTemplates[length] = template;
        }

        return template;
    }

    private static string AnchorParameter(int index) => "c" + index.ToString(CultureInfo.InvariantCulture);

    private void RemoveLabel(Guid id)
    {
        if (_labels.Remove(id, out CellLabel? label))
        {
            label.Handoff.StopAnimation("Translation");
            LabelCanvas.Children.Remove(label.Root);
        }
    }

    private void ClearLabels()
    {
        foreach (CellLabel label in _labels.Values)
        {
            label.Handoff.StopAnimation("Translation");
        }

        _labels.Clear();
        LabelCanvas.Children.Clear();
    }

    private void HideLabels()
    {
        _labelsHidden = true;
        if (_labelsVisual is not null)
        {
            _labelsVisual.StopAnimation("Opacity");
            _labelsVisual.Opacity = 0;
        }
    }

    private void RevealLabels(TimeSpan delay)
    {
        _labelsHidden = false;
        if (_labelsVisual is null)
        {
            return;
        }

        _labelsVisual.StopAnimation("Opacity");
        if (!_animationsEnabled || _compositor is null || _linearEasing is null)
        {
            _labelsVisual.Opacity = 1;
            return;
        }

        AnimateScalar(_labelsVisual, "Opacity", [(1f, 1f)], TimeSpan.FromMilliseconds(180), delay);
    }

    /// <summary>
    /// One transparent button per top-level cell. The map has no automation tree of its own, so
    /// these are what Tab, the arrow keys, Narrator and UI tests reach.
    /// </summary>
    private void SyncAccessButtons()
    {
        var keep = new HashSet<Guid>();
        if (_layer is Layer layer)
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

                keep.Add(cell.Id);
                Button button = AccessButtonFor(cell);
                Rect bounds = visual.Snapped;
                Canvas.SetLeft(button, bounds.X);
                Canvas.SetTop(button, bounds.Y);
                button.Width = bounds.Width;
                button.Height = bounds.Height;
                button.TabIndex = cell.Rank;
                AutomationProperties.SetName(button, AccessName(cell));
            }
        }

        foreach ((Guid id, Button button) in _accessButtons.Where(pair => !keep.Contains(pair.Key)).ToList())
        {
            _accessButtons.Remove(id);
            AccessCanvas.Children.Remove(button);
        }
    }

    private Button AccessButtonFor(NestedTreemapCell cell)
    {
        if (_accessButtons.TryGetValue(cell.Id, out Button? button))
        {
            return button;
        }

        button = new Button
        {
            Style = (Style)Resources["TreemapAccessCellStyle"],
            Tag = cell.Id,
            CornerRadius = new CornerRadius(TopLevelRadius),
            ContextFlyout = StorageContextMenu.Create(this, cell.Target),
        };
        AutomationProperties.SetAutomationId(button, $"TreemapItem_{cell.Id:N}");
        button.Click += AccessButton_Click;
        button.GotFocus += AccessButton_GotFocus;
        button.LostFocus += AccessButton_LostFocus;
        AccessCanvas.Children.Add(button);
        _accessButtons[cell.Id] = button;
        return button;
    }

    private string AccessName(NestedTreemapCell cell) =>
        _rowsById.TryGetValue(cell.Id, out StorageRowViewModel? row)
            ? row.AccessibleDescription
            : $"{cell.Name}, {ByteSizeFormatter.Format(cell.SizeBytes)}";

    private void AccessButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: Guid id } && CellOf(id) is NestedTreemapCell cell)
        {
            Invoke(cell);
        }
    }

    private void AccessButton_GotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: Guid id } || VisualOf(id) is not CellVisual visual)
        {
            return;
        }

        SetHover(visual.Cell);
        _dwellTimer?.Stop();
        ShowCard(visual.Cell, KeyboardAnchor(visual));
    }

    private void AccessButton_LostFocus(object sender, RoutedEventArgs args)
    {
        if (_pointer is Point point)
        {
            UpdatePointerHover(point);
        }
        else
        {
            ClearHover();
        }
    }

    private static Point KeyboardAnchor(CellVisual visual) => new(visual.Snapped.X + 8, visual.Snapped.Y + 8);

    private Button? FocusedAccessButton() =>
        XamlRoot is XamlRoot root &&
        FocusManager.GetFocusedElement(root) is Button button &&
        ReferenceEquals(button.Parent, AccessCanvas)
            ? button
            : null;

    private bool AccessButtonHasFocus() => FocusedAccessButton() is not null;

    /// <summary>
    /// Keeps keyboard focus in the map across a change of scope. The new stand-ins were only just
    /// added and cannot take focus until they have been through layout.
    /// </summary>
    private void FocusFirstAccessButton() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_isSetUp && _accessButtons.Values.MinBy(static button => button.TabIndex) is Button first)
            {
                first.Focus(FocusState.Keyboard);
            }
        });

    /// <summary>
    /// Rings the cell under the pointer at once, and describes it once the pointer has rested there;
    /// with the card already up, moving to another cell swaps it straight away.
    /// </summary>
    private void UpdatePointerHover(Point point)
    {
        NestedTreemapCell? cell = HitTest(point);
        SetHover(cell);
        if (cell is null)
        {
            _dwellTimer?.Stop();
            _dwellId = null;
            HideCard();
            return;
        }

        _cardAnchor = point;
        if (_cardShown)
        {
            if (_cardId == cell.Id)
            {
                PositionCard();
            }
            else
            {
                ShowCard(cell, point);
            }

            return;
        }

        // Compared by ID, so the re-renders of a running scan do not keep restarting the wait.
        if (_dwellId != cell.Id && _dwellTimer is not null)
        {
            _dwellId = cell.Id;
            _dwellTimer.Stop();
            _dwellTimer.Interval = TimeSpan.FromMilliseconds(DwellMilliseconds);
            _dwellTimer.Start();
        }
    }

    private void DwellTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isSetUp || _resizing || _pointer is not Point point || _dwellId is not Guid id)
        {
            return;
        }

        if (HitTest(point) is NestedTreemapCell cell && cell.Id == id)
        {
            ShowCard(cell, point);
        }
    }

    private void ShowCard(NestedTreemapCell cell, Point anchor)
    {
        _cardId = cell.Id;
        _dwellId = cell.Id;
        _cardAnchor = anchor;
        FillCard(cell);
        _cardShown = true;
        InfoCard.Opacity = 1;
        PositionCard();
    }

    private void HideCard()
    {
        _cardShown = false;
        _cardId = null;
        InfoCard.Opacity = 0;
    }

    private void FillCard(NestedTreemapCell cell)
    {
        bool synthetic = cell.Kind is TreemapCellKind.Overflow or TreemapCellKind.Unlisted;
        CardGlyph.Glyph = cell.Kind switch
        {
            TreemapCellKind.Folder => "\uE8D5",
            TreemapCellKind.File => "\uE8A5",
            TreemapCellKind.Overflow => "\uE712",
            _ => "\uE7C3",
        };

        Brush accent = synthetic || _theme is null
            ? CardDetail.Foreground
            : _theme.Accent(VisualOf(cell.Id)?.Slot ?? 0);
        CardGlyph.Foreground = accent;
        CardShare.Foreground = accent;
        CardTitle.Text = cell.Name;

        StorageNode? scope = _layer is Layer layer ? Snapshot?.Find(layer.ScopeId) : null;
        double share = scope is { SizeBytes: > 0 }
            ? Math.Clamp(cell.SizeBytes / (double)scope.SizeBytes, 0, 1)
            : 0;
        CardShare.Value = share;
        string size = ByteSizeFormatter.Format(cell.SizeBytes);
        CardSize.Text = scope is null ? size : $"{size} · {FormatShare(share)} of {scope.Name}";
        CardDetail.Text = DetailOf(cell);
        CardPath.Text = cell.Target.PhysicalPath;
    }

    private static string FormatShare(double share) => share switch
    {
        <= 0 => "0%",
        < 0.001 => "<0.1%",
        _ => share.ToString("P1", CultureInfo.CurrentCulture),
    };

    private string DetailOf(NestedTreemapCell cell)
    {
        StorageNode target = cell.Target;
        return cell.Kind switch
        {
            TreemapCellKind.Folder when !target.IsMeasured => "Not measured yet",
            TreemapCellKind.Folder => target.ItemCount == 1
                ? "1 item"
                : string.Format(CultureInfo.CurrentCulture, "{0:N0} items", target.ItemCount),
            TreemapCellKind.File when target.ModifiedAtUtc > DateTimeOffset.UnixEpoch =>
                string.Format(CultureInfo.CurrentCulture, "File · Modified {0:g}", target.ModifiedAtUtc.ToLocalTime()),
            TreemapCellKind.File => "File",
            TreemapCellKind.Overflow => cell.FoldedCount == 1
                ? "1 smaller item grouped together"
                : string.Format(CultureInfo.CurrentCulture, "{0:N0} smaller items grouped together", cell.FoldedCount),
            _ => Snapshot?.Completion == SnapshotCompletion.Partial
                ? "Files counted but not listed yet"
                : "Space not attributed to a listed item",
        };
    }

    /// <summary>Beside the pointer, flipped to the other side rather than running off the pane.</summary>
    private void PositionCard()
    {
        InfoCard.Measure(new Size(InfoCard.MaxWidth, double.PositiveInfinity));
        Size card = InfoCard.DesiredSize;
        double width = CompositionHost.ActualWidth;
        double height = CompositionHost.ActualHeight;
        double x = _cardAnchor.X + 14;
        double y = _cardAnchor.Y + 18;
        if (x + card.Width > width - 4)
        {
            x = _cardAnchor.X - 10 - card.Width;
        }

        if (y + card.Height > height - 4)
        {
            y = _cardAnchor.Y - 10 - card.Height;
        }

        Canvas.SetLeft(InfoCard, Math.Clamp(x, 4, Math.Max(4, width - 4 - card.Width)));
        Canvas.SetTop(InfoCard, Math.Clamp(y, 4, Math.Max(4, height - 4 - card.Height)));
    }

    private void RefreshCard()
    {
        if (!_cardShown || _cardId is not Guid id)
        {
            return;
        }

        if (VisualOf(id) is not CellVisual visual)
        {
            HideCard();
            return;
        }

        if (_pointer is null)
        {
            _cardAnchor = KeyboardAnchor(visual);
        }

        FillCard(visual.Cell);
        PositionCard();
    }

    private void ClearHover()
    {
        _dwellTimer?.Stop();
        _dwellId = null;
        SetHover(null);
        HideCard();
    }

    private void RefreshHover()
    {
        if (_pointer is Point point)
        {
            UpdatePointerHover(point);
            return;
        }

        SetHover(FocusedAccessButton() is { Tag: Guid id } ? CellOf(id) : null);
    }

    private sealed class CellLabel(Panel root, TextBlock nameBlock, TextBlock sizeBlock, bool isHeader, Visual handoff)
    {
        public Panel Root { get; } = root;

        public TextBlock NameBlock { get; } = nameBlock;

        public TextBlock SizeBlock { get; } = sizeBlock;

        public bool IsHeader { get; } = isHeader;

        /// <summary>The label's own hand-off visual, whose Translation follows the cell.</summary>
        public Visual Handoff { get; } = handoff;

        public CellVisual? Anchor { get; set; }

        public Vector3 Local { get; set; }

        public string? NameText { get; set; }

        public string? SizeText { get; set; }

        public double Width { get; set; } = double.NaN;

        public bool Strong { get; set; }

        public int InkSlot { get; set; } = -1;

        public TreemapLook InkLook { get; set; }
    }
}
