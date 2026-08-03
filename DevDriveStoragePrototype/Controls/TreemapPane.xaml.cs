using System.Collections.Specialized;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDriveStoragePrototype.Controls;

public sealed class StorageNodeInvokedEventArgs(StorageNode node) : EventArgs
{
    public StorageNode Node { get; } = node;
}

/// <summary>
/// Renders the squarified treemap produced by <see cref="TreemapLayout"/>. The
/// control owns no geometry of its own: it positions, colours, and hit-tests the
/// rectangles the reusable library hands it.
/// </summary>
public sealed partial class TreemapPane : UserControl
{
    private const int MaxRectangles = 24;
    private const double MinimumLabelWidth = 72;
    private const double MinimumLabelHeight = 46;
    private const double MinimumCompactLabelHeight = 28;
    private const double Gap = 3;

    private INotifyCollectionChanged? _observableRows;

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(
            nameof(Rows),
            typeof(object),
            typeof(TreemapPane),
            new PropertyMetadata(null, OnRowsChanged));

    public static readonly DependencyProperty SelectedRowProperty =
        DependencyProperty.Register(
            nameof(SelectedRow),
            typeof(StorageRowViewModel),
            typeof(TreemapPane),
            new PropertyMetadata(null, OnSelectedRowChanged));

    public TreemapPane()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Render();
    }

    public event EventHandler<StorageNodeInvokedEventArgs>? NodeInvoked;

    public object? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public StorageRowViewModel? SelectedRow
    {
        get => (StorageRowViewModel?)GetValue(SelectedRowProperty);
        set => SetValue(SelectedRowProperty, value);
    }

    private static void OnRowsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var pane = (TreemapPane)dependencyObject;
        if (pane._observableRows is not null)
        {
            pane._observableRows.CollectionChanged -= pane.Rows_CollectionChanged;
        }

        pane._observableRows = args.NewValue as INotifyCollectionChanged;
        if (pane._observableRows is not null)
        {
            pane._observableRows.CollectionChanged += pane.Rows_CollectionChanged;
        }

        pane.Render();
    }

    private static void OnSelectedRowChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((TreemapPane)dependencyObject).ApplySelection();

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        Render();

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs args) => Render();

    private void Render()
    {
        if (TreemapCanvas is null)
        {
            return;
        }

        TreemapCanvas.Children.Clear();

        StorageRowViewModel[] rows = (Rows as IEnumerable<StorageRowViewModel> ?? [])
            .Where(row => row.SizeBytes > 0)
            .OrderByDescending(row => row.SizeBytes)
            .Take(MaxRectangles)
            .ToArray();

        double width = TreemapCanvas.ActualWidth;
        double height = TreemapCanvas.ActualHeight;
        TreemapEmptyState.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (width <= 0 || height <= 0 || rows.Length == 0)
        {
            return;
        }

        IReadOnlyList<TreemapRectangle> rectangles = TreemapLayout.Calculate(
            rows.Select(row => new TreemapItem(row.Id, row.SizeBytes)),
            width,
            height);

        var rowsById = rows.ToDictionary(row => row.Id);
        foreach (TreemapRectangle rectangle in rectangles)
        {
            if (!rowsById.TryGetValue(rectangle.Id, out StorageRowViewModel? row))
            {
                continue;
            }

            double cellWidth = Math.Max(1, rectangle.Width - Gap);
            double cellHeight = Math.Max(1, rectangle.Height - Gap);
            Button cell = CreateCell(row, cellWidth, cellHeight);
            Canvas.SetLeft(cell, rectangle.X + (Gap / 2));
            Canvas.SetTop(cell, rectangle.Y + (Gap / 2));
            TreemapCanvas.Children.Add(cell);
        }

        ApplySelection();
    }

    private Button CreateCell(StorageRowViewModel row, double width, double height)
    {
        var cell = new Button
        {
            Style = (Style)Application.Current.Resources["TreemapCellStyle"],
            Background = TreemapPalette.Resolve(row.ColorIndex),
            Width = width,
            Height = height,
            Tag = row,
        };

        // Below these sizes any text is just a smear of clipped glyphs; the tooltip
        // and the automation name still describe the rectangle.
        if (width >= MinimumLabelWidth && height >= MinimumCompactLabelHeight)
        {
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(new TextBlock
            {
                Text = row.Name,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrush("TreemapLabelBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });

            // The size only earns its second line once the rectangle can hold it
            // without pushing the text past the bottom edge.
            if (height >= MinimumLabelHeight)
            {
                label.Children.Add(new TextBlock
                {
                    Text = row.SizeDisplay,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = ThemeBrush("TreemapLabelBrush"),
                    Opacity = 0.85,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                });
            }

            cell.Content = label;
        }

        AutomationProperties.SetAutomationId(cell, $"TreemapItem_{row.Id:N}");
        AutomationProperties.SetName(cell, row.AccessibleDescription);
        ToolTipService.SetToolTip(cell, $"{row.Name}\n{row.SizeDisplay} · {row.ShareDisplay} of scope");
        cell.Click += TreemapCell_Click;
        return cell;
    }

    private void ApplySelection()
    {
        if (TreemapCanvas is null)
        {
            return;
        }

        Guid? selectedId = SelectedRow?.Id;
        foreach (Button cell in TreemapCanvas.Children.OfType<Button>())
        {
            bool isSelected = cell.Tag is StorageRowViewModel row && row.Id == selectedId;
            cell.BorderBrush = isSelected
                ? ThemeBrush("TextFillColorPrimaryBrush")
                : ThemeBrush("WorkbenchCardBrush");
            cell.BorderThickness = new Thickness(isSelected ? 2 : 1);
        }
    }

    private Brush ThemeBrush(string key) =>
        this.Resources.TryGetValue(key, out object? local) && local is Brush localBrush
            ? localBrush
            : Application.Current.Resources[key] as Brush
                ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private void TreemapCell_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: StorageRowViewModel row })
        {
            NodeInvoked?.Invoke(this, new StorageNodeInvokedEventArgs(row.Node));
        }
    }
}
