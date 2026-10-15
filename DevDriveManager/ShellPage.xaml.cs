using System.Diagnostics;
using System.IO;
using DevDriveCore.Services;
using DevDriveManager.Pages;
using DevDriveManager.ViewModels;
using DevDriveManager.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace DevDriveManager;

/// <summary>
/// The application shell: a <see cref="NavigationView"/> with three rooms — Home, Space, and Dev Drive —
/// plus Settings in the footer. A room with more than one page shows them as tabs under its title
/// (Space: Map | Clean up; Dev Drive: Overview | Package caches). Tasks such as Create Dev Drive open
/// as sub-pages: the rail keeps the room selected and the header becomes a breadcrumb back to it.
/// Every page binds to the one shared <see cref="App.Shared"/> view model, so the expensive volume /
/// Dev Drive / package-cache load runs once and all pages reflect the same live state.
/// </summary>
/// <remarks>
/// The shell owns the genuinely global, dialog-bearing flows that used to live on the old MainPage:
/// the Defender performance-mode confirm + UAC elevation, and best-effort shell-URI launches. Page-local
/// affordances (the package-cache folder picker and the "what moving does" doc) stay with their page.
/// </remarks>
public sealed partial class ShellPage : Page
{
    private bool _isDialogOpen;

    /// <summary>The room or sub-page tag currently shown in <c>ContentFrame</c>.</summary>
    private string? _currentTag;

    /// <summary>The room whose tabs <c>RoomTabs</c> currently holds.</summary>
    private Room? _tabsRoom;

    /// <summary>Set while the shell selects a tab itself, so that selection does not navigate again.</summary>
    private bool _isSyncingTabs;

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>The live shell, so a hosted page can ask the shell to switch tabs (and keep the rail in sync).</summary>
    public static ShellPage? Current { get; private set; }

    public ShellPage()
    {
        InitializeComponent();
        Current = this;

        BuildNavigationItems();

        ViewModel.NavigateToCreateRequested += OnNavigateToCreateRequested;
        ViewModel.LaunchUriRequested += OnLaunchUriRequested;
        ViewModel.PerformanceModeEnableRequested += OnPerformanceModeEnableRequested;

        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>
    /// Builds native navigation items from the route registry. Section boundaries remain separators;
    /// a narrow Store-style rail does not have room for full-width section headers.
    /// </summary>
    private void BuildNavigationItems()
    {
        RoomSection? previous = null;

        foreach (Room room in RoomRegistry.All)
        {
            if (previous is not null && room.Section != previous)
            {
                ShellNavView.MenuItems.Add(new NavigationViewItemSeparator());
            }

            var item = new NavigationViewItem
            {
                Content = room.Title,
                Icon = new FontIcon { Glyph = room.Glyph, FontSize = 20 },
                Style = (Style)Application.Current.Resources["StoreNavigationViewItemStyle"],
                Tag = room.Tag,
            };
            AutomationProperties.SetAutomationId(item, room.AutomationId);
            AutomationProperties.SetName(item, room.Title);
            ToolTipService.SetToolTip(item, room.Title);
            ShellNavView.MenuItems.Add(item);
            previous = room.Section;
        }
    }

    /// <summary>
    /// Open any destination by its tag, updating the rail, the tabs, and the content frame. A room tag
    /// (e.g. "space") opens its first tab; a tab tag (e.g. "caches") opens that tab of its room; a
    /// sub-page tag (e.g. "create") opens that page. The rail always selects the owning room.
    /// </summary>
    public void SelectNavItem(string tag)
    {
        string? railTag = RoomOf(tag);
        NavigationViewItem? item = ShellNavView.MenuItems
            .Concat(ShellNavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag as string == railTag);
        if (item is null) return;

        // All callers (including attention signals and the shared Create request) use the same
        // selection path. Re-selecting the current destination never reconstructs its page.
        // Navigate first, so the SelectionChanged raised for a sub-page's parent room sees the
        // sub-page already open instead of replacing it with the room.
        Navigate(tag);
        ShellNavView.SelectedItem = item;
    }

    /// <summary>
    /// NavigationView owns IsSelected and exposes SelectionItem to assistive technology.
    /// ItemStatus is retained as well for existing automation clients of the old rail.
    /// </summary>
    private void ShellNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        foreach (NavigationViewItem item in sender.MenuItems.Concat(sender.FooterMenuItems).OfType<NavigationViewItem>())
        {
            AutomationProperties.SetItemStatus(item, ReferenceEquals(item, args.SelectedItem) ? "Selected" : string.Empty);
        }
        // SelectNavItem navigates before it selects, so a selection raised for the room that already
        // owns the open page (a tab, or a sub-page) must not replace that page with the room's first tab.
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && RoomOf(_currentTag) != tag)
        {
            Navigate(tag);
        }
    }

    /// <summary>
    /// Invoking the already-selected room raises no SelectionChanged, so this is what takes a
    /// sub-page (Dev Drive › Create Dev Drive) back to its room when the room's rail item is clicked.
    /// </summary>
    private void ShellNavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string tag }
            && RoomRegistry.FindSubPage(_currentTag)?.ParentTag == tag)
        {
            Navigate(tag);
        }
    }

    /// <summary>The first breadcrumb segment is the parent room; the last is the page already open.</summary>
    private void PageBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0 && RoomRegistry.FindSubPage(_currentTag) is { } subPage)
        {
            SelectNavItem(subPage.ParentTag);
        }
    }

    private void RoomTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_isSyncingTabs && sender.SelectedItem is SelectorBarItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    /// <summary>The rail room a tag belongs to: the room itself, the room owning a tab, or a sub-page's parent.</summary>
    private static string? RoomOf(string? tag) => RoomRegistry.Resolve(tag)?.Room.Tag ?? tag;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Land on the default room and kick off the one-time load.
        if (ContentFrame.Content is null)
        {
            SelectNavItem(RoomRegistry.Default.Tag);
        }

        if (!ViewModel.HasLoaded)
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    // Pill / ratio / drive-glyph brushes are resolved imperatively, so they don't re-tint on a live OS
    // theme switch. Reloading rebuilds the data rows with brushes for the new theme.
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (ViewModel.HasLoaded && !ViewModel.IsLoading)
        {
            _ = ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private void Navigate(string tag)
    {
        if (tag == "settings")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            {
                _currentTag = tag;
                ShowTitle("Settings", "SettingsTitle");
                ShowTabs(null, null);
                ContentFrame.Navigate(typeof(SettingsPage), null, new SuppressNavigationTransitionInfo());
            }

            return;
        }

        if (RoomRegistry.Resolve(tag) is not { } route || ContentFrame.CurrentSourcePageType == route.PageType)
        {
            return;
        }

        // Moving between tabs of one room slides the page the way the tab moved; everything else cuts.
        NavigationTransitionInfo transition = new SuppressNavigationTransitionInfo();
        if (route.Tab is { } tab && RoomRegistry.Resolve(_currentTag) is { Tab: { } fromTab } from && from.Room == route.Room)
        {
            var tabs = route.Room.Tabs.ToList();
            transition = new SlideNavigationTransitionInfo
            {
                Effect = tabs.IndexOf(tab) > tabs.IndexOf(fromTab)
                    ? SlideNavigationTransitionEffect.FromRight
                    : SlideNavigationTransitionEffect.FromLeft,
            };
        }

        _currentTag = tag;

        if (route.SubPage is { } subPage)
        {
            PageHeader.Visibility = Visibility.Visible;
            PageBreadcrumb.ItemsSource = new[] { route.Room.Title, subPage.Title };
            PageBreadcrumb.Visibility = Visibility.Visible;
            PageTitle.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(PageTitle, "ShellPageTitle");
            ShowTabs(null, null);
        }
        else if (route.Room.OwnsHeader)
        {
            // Home draws its own hero title; the shell header would only repeat it.
            PageHeader.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(PageTitle, "ShellPageTitle");
            ShowTabs(null, null);
        }
        else
        {
            ShowTitle(route.Room.Title, "PageTitle");
            ShowTabs(route.Room, route.Tab);
        }

        ContentFrame.Navigate(route.PageType, null, transition);
    }

    private void ShowTitle(string title, string automationId)
    {
        PageHeader.Visibility = Visibility.Visible;
        PageBreadcrumb.Visibility = Visibility.Collapsed;
        PageTitle.Text = title;
        PageTitle.Visibility = Visibility.Visible;
        AutomationProperties.SetAutomationId(PageTitle, automationId);
    }

    /// <summary>
    /// Shows the room's tabs with <paramref name="selected"/> selected, or hides the bar for a room
    /// with one page, a sub-page, or Settings. The items are rebuilt only when the room changes.
    /// </summary>
    private void ShowTabs(Room? room, RoomTab? selected)
    {
        _isSyncingTabs = true;
        try
        {
            if (room is not { HasTabs: true })
            {
                RoomTabs.Visibility = Visibility.Collapsed;
                RoomTabs.Items.Clear();
                _tabsRoom = null;
                return;
            }

            if (!ReferenceEquals(_tabsRoom, room))
            {
                RoomTabs.Items.Clear();
                foreach (RoomTab tab in room.Tabs)
                {
                    var item = new SelectorBarItem { Text = tab.Title, Tag = tab.Tag };
                    AutomationProperties.SetAutomationId(item, tab.AutomationId);
                    RoomTabs.Items.Add(item);
                }

                _tabsRoom = room;
            }

            RoomTabs.SelectedItem = RoomTabs.Items.FirstOrDefault(i => i.Tag as string == selected?.Tag);
            RoomTabs.Visibility = Visibility.Visible;
        }
        finally
        {
            _isSyncingTabs = false;
        }
    }

    /// <summary>
    /// A page constructor that throws would otherwise either take the process down or leave an
    /// empty content pane with the rail still highlighting the room, which reads as "this room is
    /// blank" rather than "this room failed to open".
    /// </summary>
    private void OnNavigationFailed(object sender, Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"[navigation] {e.SourcePageType}: {e.Exception}");

        ContentFrame.Content = new TextBlock
        {
            Text = "This section could not be opened. Try another room, or restart the app.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(32),
        };
    }

    /// <summary>"Create Dev Drive" raised from a page (e.g. the Dashboard empty state): open it under Dev Drive.</summary>
    private void OnNavigateToCreateRequested() => SelectNavItem("create");

    /// <summary>Launches a shell URI (e.g. <c>ms-settings:storage</c>). Best-effort and safe.</summary>
    private async void OnLaunchUriRequested(string uri)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(uri));
        }
        catch
        {
            // Launching an external URI (Storage settings or Windows Security) is best-effort; never crash over it.
        }
    }

    /// <summary>
    /// Turns on Defender performance mode — the dominant factor in the Dev Drive's real-workload advantage —
    /// behind an explicit confirm and UAC elevation. The app NEVER changes Defender config silently: the
    /// user must click the nudge, confirm the dialog, AND approve UAC. The change is reversible
    /// (<c>Set-MpPreference -PerformanceModeStatus Disabled</c>). If elevation is declined or unavailable,
    /// we show the exact manual command instead.
    /// </summary>
    private async void OnPerformanceModeEnableRequested()
    {
        if (_isDialogOpen)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var confirm = new ContentDialog
            {
                Title = PerformanceModeAdvisor.NudgeTitle,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = PerformanceModeAdvisor.ConfirmMessage,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 460,
                },
                PrimaryButtonText = "Turn on (admin)",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            ApplyDialogStyle(confirm);
            AutomationProperties.SetAutomationId(confirm, "PerformanceModeConfirmDialog");

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return; // user cancelled — nothing changes
            }

            bool enabled = await TryEnablePerformanceModeElevatedAsync();
            if (enabled)
            {
                ViewModel.OnPerformanceModeEnabled();
                ViewModel.Suite.DismissPerformanceModeCaption();
                await ShowInfoDialogAsync(
                    "Performance mode is on",
                    "Defender performance mode is now on. Re-run the test to see representative Dev Drive numbers.",
                    "PerformanceModeDoneDialog");
            }
            else
            {
                await ShowInfoDialogAsync(
                    PerformanceModeAdvisor.NudgeTitle,
                    PerformanceModeAdvisor.ManualGuidance,
                    "PerformanceModeGuidanceDialog");
            }
        }
            catch (Exception exception)
        {
                // ShowAsync throws if the XamlRoot has gone (page detached) or if another ContentDialog
                // is already up — "only one ContentDialog can be open at a time". The _isDialogOpen flag
                // is local to this handler, so it cannot see a dialog a room opened. In an async void
                // that throw is a process kill, which is a violent end for declining a nudge.
                System.Diagnostics.Debug.WriteLine($"[perf-mode dialog] {exception}");
            }
            finally
            {
                _isDialogOpen = false;
            }
    }

    /// <summary>
    /// Runs the reversible <c>Set-MpPreference -PerformanceModeStatus Enabled</c> in an elevated PowerShell
    /// (UAC). Returns <c>true</c> only when the process exits 0. Any failure — UAC declined, elevation
    /// unavailable, or an unsupported cmdlet — returns <c>false</c> so the caller can show manual guidance.
    /// </summary>
    private static async Task<bool> TryEnablePerformanceModeElevatedAsync()
    {
        try
        {
            string powerShellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            var psi = new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments =
                    "-NoProfile -ExecutionPolicy Bypass -Command " +
                    "\"try { Set-MpPreference -PerformanceModeStatus Enabled -ErrorAction Stop; exit 0 } catch { exit 1 }\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task ShowInfoDialogAsync(string title, string message, string automationId)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460,
            },
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ApplyDialogStyle(dialog);
        AutomationProperties.SetAutomationId(dialog, automationId);
        await dialog.ShowAsync();
    }

    private static void ApplyDialogStyle(ContentDialog dialog)
    {
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
    }
}
