using System.Diagnostics;
using System.IO;
using DevDriveCore.Services;
using DevDriveManager.Pages;
using DevDriveManager.ViewModels;
using DevDriveManager.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace DevDriveManager;

/// <summary>
/// The application shell: a <see cref="NavigationView"/> that splits the (formerly single, endless-scroll)
/// experience into focused pages — Dashboard, Package caches, Benchmarks, Drives, Create Dev Drive, and
/// Settings (footer). Every page binds to the one shared <see cref="App.Shared"/> view model, so the
/// expensive volume / Dev Drive / package-cache load runs once and all pages reflect the same live state.
/// </summary>
/// <remarks>
/// The shell owns the genuinely global, dialog-bearing flows that used to live on the old MainPage:
/// the Defender performance-mode confirm + UAC elevation, and best-effort shell-URI launches. Page-local
/// affordances (the package-cache folder picker and the "what moving does" doc) stay with their page.
/// </remarks>
public sealed partial class ShellPage : Page
{
    private bool _isDialogOpen;

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>The live shell, so a hosted page can ask the shell to switch tabs (and keep the rail in sync).</summary>
    public static ShellPage? Current { get; private set; }

    public ShellPage()
    {
        InitializeComponent();
        Current = this;

        BuildRail();

        ViewModel.NavigateToCreateRequested += OnNavigateToCreateRequested;
        ViewModel.LaunchUriRequested += OnLaunchUriRequested;
        ViewModel.PerformanceModeEnableRequested += OnPerformanceModeEnableRequested;

        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>
    /// Builds the rail from <see cref="RoomRegistry"/>, inserting a separator wherever the section
    /// changes. The section break is load-bearing: crossing it is what tells the user they have moved
    /// between subsystems rather than just to another list of files.
    /// </summary>
    private void BuildRail()
    {
        RoomSection? previous = null;

        foreach (Room room in RoomRegistry.All)
        {
            if (previous is not null && room.Section != previous)
            {
                NavView.MenuItems.Add(new NavigationViewItemSeparator());
            }

            var item = new NavigationViewItem
            {
                Content = room.Title,
                Tag = room.Tag,
                Icon = new FontIcon { Glyph = room.Glyph },
            };
            AutomationProperties.SetAutomationId(item, room.AutomationId);

            NavView.MenuItems.Add(item);
            previous = room.Section;
        }
    }

    /// <summary>Select a top-level nav item by its tag (e.g. "caches"), updating the rail and the content frame.</summary>
    public void SelectNavItem(string tag)
    {
        foreach (object item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Land on the Dashboard and kick off the one-time load.
        if (NavView.MenuItems.Count > 0 && NavView.SelectedItem is null)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
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

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        Type? target = RoomRegistry.Find(tag)?.PageType;

        if (target is not null && ContentFrame.CurrentSourcePageType != target)
        {
            ContentFrame.Navigate(target, null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
        }
    }

    /// <summary>"Create Dev Drive" raised from a page (e.g. the Dashboard empty state): select the nav item.</summary>
    private void OnNavigateToCreateRequested()
    {
        foreach (object item in NavView.MenuItems)
        {
            if (item is NavigationViewItem { Tag: "create" } createItem)
            {
                NavView.SelectedItem = createItem;
                return;
            }
        }
    }

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
