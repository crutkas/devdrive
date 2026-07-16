using System;
using System.IO;
using System.Threading.Tasks;
using DevDriveManager.Services;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The "Settings" page. The redesign deliberately keeps settings tiny — the app follows the OS theme and
/// "just works", so the only real preference is a Light/Dark override. Everything else here is a shortcut to
/// an action that already lives on the shared <see cref="MainPageViewModel"/> (rescan, create, manage in
/// Storage, Windows Security) or a short explainer. Grouped <c>SettingsCard</c>/<c>SettingsExpander</c> rows
/// keep it scannable.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();

        // Reflect the persisted theme without re-triggering SelectionChanged during initialization.
        ThemeSelector.SelectedIndex = ThemeService.Mode switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        _ready = true;
    }

    /// <summary>The shared, already-loaded app view model.</summary>
    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>App name + version line for the About card.</summary>
    public string AppVersionText
    {
        get
        {
            try
            {
                Windows.ApplicationModel.PackageVersion v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                return "Version — (unpackaged)";
            }
        }
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        ElementTheme theme = ThemeSelector.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ThemeService.SetMode(theme);
    }

    /// <summary>
    /// Opens the bundled "what does moving a package cache do" explainer (<c>docs\PackageCacheMoves.md</c>,
    /// copied next to the app) in a scrollable, selectable read-only dialog — the same content surfaced from
    /// the Package caches page.
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
            body = "Couldn't open the package-cache explainer (" + ex.Message + "). Moving a cache "
                + "relocates it to your Dev Drive and repoints the per-user environment variable, leaving "
                + "a reversible receipt so you can move it back at any time.";
        }

        await ShowDocDialogAsync("What moving a package cache does", body, "SettingsLearnAboutCacheMovesDialog");
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
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        AutomationProperties.SetAutomationId(dialog, automationId);
        await dialog.ShowAsync();
    }
}
