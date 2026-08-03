using DevDriveStorage;
using DevDriveStoragePrototype.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DevDriveStoragePrototype;

public sealed partial class MainPage : Page
{
    private readonly MockStorageScenarioCatalog _catalog =
        MockStorageScenarioCatalog.CreateDefault();
    private bool _isLoaded;
    private string _layoutOverride = "Auto";

    public MainPage()
    {
        var mock = new MockStorageSnapshotSource(_catalog, TimeSpan.FromMilliseconds(140));
        ViewModel = new StorageExplorerViewModel(
            new RoutingStorageSnapshotSource(mock, new LiveStorageSnapshotSource()));
        ScenarioOptions = BuildScenarioChoices();
        InitializeComponent();
    }

    public StorageExplorerViewModel ViewModel { get; }

    public IReadOnlyList<ScenarioChoice> ScenarioOptions { get; }

    /// <summary>
    /// Builds the scenario picker: every mock scenario first (unchanged display names and
    /// order, so the UI suite keeps selecting them by name), then one live entry per fixed
    /// volume. Mock stays the default because index 0 is still the first mock scenario.
    /// </summary>
    private IReadOnlyList<ScenarioChoice> BuildScenarioChoices()
    {
        var choices = new List<ScenarioChoice>();
        foreach (MockStorageScenario scenario in _catalog.Scenarios)
        {
            choices.Add(new ScenarioChoice(scenario.DisplayName, scenario.Id));
        }

        try
        {
            foreach (StorageVolume volume in new SystemVolumeProvider().GetFixedVolumes())
            {
                choices.Add(new ScenarioChoice(
                    $"Live: {volume.DisplayName}",
                    RoutingStorageSnapshotSource.LiveScenarioId(volume.RootPath)));
            }
        }
        catch (Exception)
        {
            // Volume discovery is best-effort; a probe failure must never break the mock
            // picker that the rest of the prototype (and the UI suite) depends on.
        }

        return choices;
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static string KindGlyph(bool isFolder) => isFolder ? "\uE8B7" : "\uE8A5";

    /// <summary>
    /// Resolves the rank-based palette slot a row shares with its treemap rectangle.
    /// </summary>
    public static Brush PaletteBrush(int colorIndex) =>
        TreemapPalette.Resolve(colorIndex);

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        ApplyLayoutState();
        await ViewModel.LoadScenarioAsync("baseline");
    }

    private async void ScenarioComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_isLoaded || ScenarioComboBox.SelectedItem is not ScenarioChoice choice)
        {
            return;
        }

        FoldersModeItem.IsSelected = true;
        await ViewModel.LoadScenarioAsync(choice.ScenarioId);
    }

    private void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        _layoutOverride = (LayoutComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";

        // ComboBox raises SelectionChanged while the page is still being parsed, long
        // before the elements declared after it have been assigned to their fields.
        if (!_isLoaded)
        {
            return;
        }

        if (_layoutOverride == "Full")
        {
            (App.Window as MainWindow)?.ResizeLogical(1600, 900);
        }
        else if (_layoutOverride == "Compact")
        {
            (App.Window as MainWindow)?.ResizeLogical(1280, 800);
        }

        ApplyLayoutState();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // The theme has to be set on the window's content root. Setting it on a nested
        // element leaves every ancestor - including the page background - on the old theme.
        if (App.Window?.Content is not FrameworkElement root)
        {
            return;
        }

        string theme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_layoutOverride == "Auto")
        {
            ApplyLayoutState();
        }
    }

    private void ApplyLayoutState()
    {
        if (InspectorSplitView is null)
        {
            return;
        }

        bool compact = _layoutOverride == "Compact" ||
            (_layoutOverride == "Auto" && ActualWidth > 0 && ActualWidth < 1400);
        VisualStateManager.GoToState(this, compact ? "CompactLayout" : "FullLayout", false);
        if (!compact)
        {
            InspectorSplitView.IsPaneOpen = false;
        }
    }

    private void ViewModeSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Mode = sender.SelectedItem == LargestFilesModeItem
            ? ExplorerMode.LargestFiles
            : ExplorerMode.Folders;

    private void FolderTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is StorageTreeItemViewModel item)
        {
            ViewModel.NavigateTo(item.Id);
        }
    }

    private void ScopeBreadcrumbBar_ItemClicked(
        BreadcrumbBar sender,
        BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is StorageNode node)
        {
            ViewModel.NavigateTo(node.Id);
        }
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (ItemsList.SelectedItem is StorageRowViewModel { IsFolder: true } row)
        {
            ViewModel.NavigateTo(row.Id);
        }
    }

    private void StorageTreemap_NodeInvoked(object sender, StorageNodeInvokedEventArgs args)
    {
        ViewModel.Select(args.Node.Id);
        if (ViewModel.SelectedRow is StorageRowViewModel row)
        {
            ItemsList.ScrollIntoView(row);
        }
    }

    private void UpButton_Click(object sender, RoutedEventArgs args) => ViewModel.NavigateUp();

    private void RefreshButton_Click(object sender, RoutedEventArgs args) =>
        _ = ViewModel.RefreshAsync();

    private void RetryScanButton_Click(object sender, RoutedEventArgs args) =>
        _ = ViewModel.RetryAsync();

    private void CancelScanButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.CancelScan();

    private void SortNameButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SortBy(ExplorerSortColumn.Name);

    private void SortSizeButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SortBy(ExplorerSortColumn.Size);

    private void SortLogicalButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SortBy(ExplorerSortColumn.Logical);

    private void SortItemsButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SortBy(ExplorerSortColumn.ItemCount);

    private void SortContextButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SortBy(ExplorerSortColumn.Context);

    private void InspectorDrawerButton_Click(object sender, RoutedEventArgs args) =>
        InspectorSplitView.IsPaneOpen = true;

    private void CloseInspectorButton_Click(object sender, RoutedEventArgs args) =>
        InspectorSplitView.IsPaneOpen = false;

    private void ApplySearchTestButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SearchText = "vhdx";

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        bool menu = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (control && args.Key == VirtualKey.F)
        {
            SearchBox.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.F5)
        {
            _ = ViewModel.RefreshAsync();
            args.Handled = true;
        }
        else if (menu && args.Key == VirtualKey.Up)
        {
            ViewModel.NavigateUp();
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape && InspectorSplitView.IsPaneOpen)
        {
            InspectorSplitView.IsPaneOpen = false;
            args.Handled = true;
        }
    }
}

/// <summary>
/// Prototype-only picker item pairing a display label with the scenario id the
/// <see cref="StorageExplorerViewModel"/> forwards to its source. Mock scenarios carry their
/// catalogue id; live volumes carry a <c>live:&lt;root&gt;</c> id understood by
/// <see cref="RoutingStorageSnapshotSource"/>.
/// </summary>
public sealed record ScenarioChoice(string DisplayName, string ScenarioId);
