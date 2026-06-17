using System.Diagnostics;
using System.IO;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;
using DevDriveManager.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DevDriveManager;

/// <summary>
/// The Dev Drive settings page. Renders the full concept page top-to-bottom — status banner,
/// Performance (real disk speed test + real-world builds, with per-row transparency), Package caches
/// (real detection, gated move) and Drive health — plus the
/// volumes list and trust/filter detail. All data comes from the UI-agnostic <c>DevDriveCore</c>
/// services via <see cref="MainPageViewModel"/>. Mutating affordances are REAL but safe: per-user,
/// reversible (Move back), preview→explicit Confirm, hash-verified, off-thread. Machine-config
/// changes (Create Dev Drive, enable performance mode) keep an explicit confirm + UAC dialog.
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _isPreviewOpen;

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        ViewModel.NavigateToCreateRequested += OnNavigateToCreateRequested;
        ViewModel.LaunchUriRequested += OnLaunchUriRequested;

        // The Defender perf-mode change is now triggered from ONE authoritative place — Drive health ›
        // Trust &amp; filters (the MainPageViewModel deep link). The Workload nudge and Package-cache
        // caveat are demoted to non-actionable captions, so they no longer raise an enable request.
        ViewModel.PerformanceModeEnableRequested += OnPerformanceModeEnableRequested;

        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    // The pill / ratio / drive-glyph brushes are resolved imperatively (OneTime x:Bind), so they don't
    // re-tint on a live OS theme switch. Reloading rebuilds the data rows with brushes for the new theme.
    // (A fresh launch is always correct; this just handles toggling theme while the page is open.)
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (ViewModel.HasLoaded && !ViewModel.IsLoading)
        {
            _ = ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasLoaded)
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private void OnNavigateToCreateRequested() => Frame?.Navigate(typeof(CreateDevDrivePage));

    /// <summary>Launches a shell URI (e.g. <c>ms-settings:storage</c>). Best-effort and safe.</summary>
    private async void OnLaunchUriRequested(string uri)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(uri));
        }
        catch
        {
            // Launching an external URI (Storage settings or Windows Security) is best-effort; never crash the page over it.
        }
    }

    /// <summary>
    /// Turns on Defender performance mode — the dominant factor in the Dev Drive's real-workload
    /// advantage — behind an explicit confirm and UAC elevation. The app NEVER changes Defender config
    /// silently: the user must click the nudge, confirm the dialog, AND approve UAC. The change is
    /// reversible (<c>Set-MpPreference -PerformanceModeStatus Disabled</c>). If elevation is declined or
    /// unavailable, we show the exact manual command instead.
    /// </summary>
    private async void OnPerformanceModeEnableRequested()
    {
        if (_isPreviewOpen)
        {
            return;
        }

        _isPreviewOpen = true;
        try
        {
            // 1. Explicit confirm — spells out the command, that it needs admin, and how to undo it.
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

            // 2. Apply elevated (UAC). The only intentional machine-config change in the app.
            bool enabled = await TryEnablePerformanceModeElevatedAsync();

            if (enabled)
            {
                ViewModel.OnPerformanceModeEnabled();

                // The demoted perf-mode caveat in the Performance suite is no longer relevant once perf
                // mode is on, so hide it directly (it owns no command anymore).
                ViewModel.Suite.DismissPerformanceModeCaption();
                await ShowInfoDialogAsync(
                    "Performance mode is on",
                    "Defender performance mode is now on. Re-run the test to see representative Dev Drive numbers.",
                    "PerformanceModeDoneDialog");
            }
            else
            {
                // 3. Fallback: UAC declined, elevation unavailable, or the cmdlet isn't supported here.
                await ShowInfoDialogAsync(
                    PerformanceModeAdvisor.NudgeTitle,
                    PerformanceModeAdvisor.ManualGuidance,
                    "PerformanceModeGuidanceDialog");
            }
        }
        finally
        {
            _isPreviewOpen = false;
        }
    }

    /// <summary>
    /// Runs the reversible <c>Set-MpPreference -PerformanceModeStatus Enabled</c> in an elevated
    /// PowerShell (UAC). Returns <c>true</c> only when the process exits 0. Any failure — UAC declined
    /// (<see cref="System.ComponentModel.Win32Exception"/>), elevation unavailable, or an unsupported
    /// cmdlet — returns <c>false</c> so the caller can show manual guidance.
    /// </summary>
    private static async Task<bool> TryEnablePerformanceModeElevatedAsync()
    {
        try
        {
            // Fully-qualify powershell.exe (System32\WindowsPowerShell\v1.0) so we never resolve a
            // PATH-injected impostor when launching elevated.
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

            // No timeout: a UAC consent prompt legitimately blocks until the user responds.
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            // UAC declined or elevation unavailable — the caller falls back to manual guidance.
            return false;
        }
    }

    /// <summary>Shows a simple, dismissable info dialog (reuses the app's content-dialog styling).</summary>
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

    /// <summary>
    /// Opens the bundled "what does moving a package cache do" explainer
    /// (<c>docs\PackageCacheMoves.md</c>, copied next to the app) in a scrollable,
    /// selectable read-only dialog. This is the in-app doc affordance ("Learn what this does").
    /// </summary>
    private async void LearnAboutCacheMoves_Click(object sender, RoutedEventArgs e)
    {
        string docPath = Path.Combine(AppContext.BaseDirectory, "docs", "PackageCacheMoves.md");
        string body;
        try
        {
            body = File.Exists(docPath)
                ? await File.ReadAllTextAsync(docPath)
                : "The package-cache explainer couldn't be found in this build. Moving a cache "
                    + "relocates it to your Dev Drive and repoints the per-user environment variable, "
                    + "leaving a reversible receipt so you can move it back at any time.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best-effort doc display from an async-void handler: any read failure (locked-down ACL, AV
            // quarantine, transient IO) falls back to the inline explainer rather than tearing down the app.
            body = "Couldn't open the package-cache explainer (" + ex.Message + "). Moving a cache "
                + "relocates it to your Dev Drive and repoints the per-user environment variable, leaving "
                + "a reversible receipt so you can move it back at any time.";
        }

        await ShowDocDialogAsync("What moving a package cache does", body, "LearnAboutCacheMovesDialog");
    }

    /// <summary>
    /// Lets the user pick a folder for a tool whose cache wasn't auto-detected, and writes the
    /// chosen path back onto the row's <see cref="PackageCacheRowViewModel.MapPath"/> so the
    /// "Map path" / "Move &amp; remap" actions can target it. No mutation happens here — that is
    /// still gated behind the row's preview→confirm flow.
    /// </summary>
    private async void BrowseForMapPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PackageCacheRowViewModel row)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            row.MapPath = folder.Path;
        }
    }

    /// <summary>Shows a long-form, selectable read-only doc in a scrollable content dialog.</summary>
    private async Task ShowDocDialogAsync(string title, string message, string automationId)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
            },
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ApplyDialogStyle(dialog);
        AutomationProperties.SetAutomationId(dialog, automationId);
        await dialog.ShowAsync();
    }

    // ---- x:Bind helper functions (no IValueConverter — see winui-design) -----------------------

    /// <summary>Shows a simple, dismissable info dialog (reuses the app's content-dialog styling).</summary>
    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Inverse of <see cref="BoolToVisibility"/>.</summary>
    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>True when not busy — for enabling a command button while its command runs.</summary>
    public static bool IsNotBusy(bool isBusy) => !isBusy;

    /// <summary>Dims a suite row's label while it is queued (matches the mock's dimmed "Queued" rows).</summary>
    public static double QueuedOpacity(bool isQueued) => isQueued ? 0.5 : 1.0;

    /// <summary>Background brush for a package-cache status pill, keyed by status ("dev"/"system"/"notfound").</summary>
    /// <summary>
    /// Background brush for a package-cache status pill, keyed by status. Dev Drive = the user's bright
    /// base accent; system drive = a darker accent shade (one accent family, never grey) so the Dev Drive
    /// reads as the brighter one; not-found = a quiet neutral.
    /// </summary>
    public static Brush? PillBackground(string statusKind) => Resource(statusKind switch
    {
        "dev" => "PerfDevDriveBarBrush",
        "system" => "PerfBaselineBarBrush",
        _ => "ControlFillColorSecondaryBrush",
    });

    /// <summary>Foreground brush for a package-cache status pill, keyed by status (paired for contrast on each fill).</summary>
    public static Brush? PillForeground(string statusKind) => Resource(statusKind switch
    {
        "dev" => "PerfDevDriveForegroundBrush",
        "system" => "PerfBaselineForegroundBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    /// <summary>
    /// Themed foreground for a volume's header glyph: accent for a Dev Drive, the default text
    /// color for a normal volume. Returns a resolved brush in both cases (a <c>null</c> Foreground
    /// would render the icon transparent rather than inheriting).
    /// </summary>
    public static Brush? DriveGlyphBrush(bool isDevDrive) =>
        Resource(isDevDrive ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    /// <summary>Foreground brush for a speed-test ratio: success green when the Dev Drive wins (or ties), neutral otherwise.</summary>
    public static Brush? RatioBrush(bool isFavorable) =>
        Resource(isFavorable ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");

    private static Brush? Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : null;
}
