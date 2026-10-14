using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// Native settings cards in a single responsive, scrollable column.
/// </summary>
/// <remarks>
/// <para>
/// Every row here changes a decision the app actually makes. The comp draws twenty-two settings, most
/// of which describe features this build does not have — a delete path, receipts, worktree scanning,
/// a hosts file. A toggle wired to nothing is worse than a missing toggle, because it teaches the
/// reader that the rest of the page is decoration too, so the grammar is the comp's and the contents
/// are only what <see cref="AppPreferences"/> can honour.
/// </para>
/// <para>
/// Preferences are written straight through to <see cref="PreferencesService"/> on change rather than
/// behind an apply button: each one is independent, reversible, and cheap to undo, so a commit step
/// would only add a way to lose an edit.
/// </para>
/// </remarks>
public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly IFreeSpaceHistoryStore _history = new JsonFreeSpaceHistoryStore();

    // Set once the constructor has finished seeding the controls. Every SelectionChanged handler
    // gates on it, because assigning SelectedIndex raises the same event a user click does and would
    // otherwise persist the value we only just read.
    private bool _isReady;

    // Guards ContentDialog re-entrancy: WinUI allows one dialog at a time and a second ShowAsync
    // throws, which a double-click on any of this page's explainer buttons would otherwise reach.
    private bool _isDialogOpen;

    private int _historyCount;
    private DateTimeOffset? _historyOldest;

    public SettingsPage()
    {
        InitializeComponent();

        AppPreferences prefs = PreferencesService.Current;

        ThemeSelector.SelectedIndex = ThemeService.Mode switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };

        SelectByTag(LowFreeCombo, prefs.LowFreePercent);
        SelectByTag(RollupCombo, prefs.CacheRollupThreshold);
        SelectByTag(RetentionCombo, prefs.HistoryRetentionDays);
        CreationMethodCombo.SelectedIndex = prefs.PreferResizeOverVhdx ? 0 : 1;

        _isReady = true;

        ReadHistory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>App name + version line.</summary>
    public string AppVersionText
    {
        get
        {
            try
            {
                Windows.ApplicationModel.PackageVersion v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (InvalidOperationException)
            {
                return "Version — (unpackaged)";
            }
        }
    }

    // ---- Two-way toggles ---------------------------------------------------------------------

    /// <summary>Whether Overview raises a signal for caches sitting off the Dev Drive.</summary>
    public bool WatchCachesOffDevDrive
    {
        get => PreferencesService.Current.WatchCachesOffDevDrive;
        set
        {
            if (value == PreferencesService.Current.WatchCachesOffDevDrive)
            {
                return;
            }

            PreferencesService.Update(p => p with { WatchCachesOffDevDrive = value });
        }
    }

    /// <summary>Whether free-space readings are appended on every Overview load.</summary>
    public bool RecordFreeSpaceHistory
    {
        get => PreferencesService.Current.RecordFreeSpaceHistory;
        set
        {
            if (value == PreferencesService.Current.RecordFreeSpaceHistory)
            {
                return;
            }

            PreferencesService.Update(p => p with { RecordFreeSpaceHistory = value });
        }
    }

    // ---- Free-space history --------------------------------------------------------------------

    /// <summary>True when there is anything to clear.</summary>
    public bool HasHistory => _historyCount > 0;

    /// <summary>
    /// What is on disk right now. A count alone does not answer "is this worth keeping", so the date
    /// of the oldest reading comes with it — that is the number the retention setting acts on.
    /// </summary>
    public string HistorySummaryText => _historyCount == 0
        ? "No readings recorded yet"
        : _historyOldest is { } oldest
            ? $"{_historyCount} {(_historyCount == 1 ? "reading" : "readings")}, oldest {oldest.ToLocalTime():d MMM yyyy}"
            : $"{_historyCount} {(_historyCount == 1 ? "reading" : "readings")}";

    // ---- Handlers ------------------------------------------------------------------------------

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        ThemeService.SetMode(ThemeSelector.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });
    }

    private void LowFreeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReady && TagOf(LowFreeCombo) is { } percent)
        {
            PreferencesService.Update(p => p with { LowFreePercent = percent });
        }
    }

    private void RollupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReady && TagOf(RollupCombo) is { } threshold)
        {
            PreferencesService.Update(p => p with { CacheRollupThreshold = threshold });
        }
    }

    private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReady && TagOf(RetentionCombo) is { } days)
        {
            PreferencesService.Update(p => p with { HistoryRetentionDays = days });
        }
    }

    private void CreationMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReady)
        {
            bool resize = CreationMethodCombo.SelectedIndex == 0;
            PreferencesService.Update(p => p with { PreferResizeOverVhdx = resize });
        }
    }

    /// <summary>
    /// Deletes saved readings only after explicit confirmation. Past readings cannot be recreated.
    /// </summary>
    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Clear free-space history?",
            $"{HistorySummaryText}. All saved readings will be permanently deleted. New readings can still be recorded.",
            "Clear history", "ClearHistoryConfirmDialog"))
        {
            return;
        }

        _history.Clear();
        ReadHistory();
    }

    /// <summary>
    /// Puts every preference back to its shipped default, and re-seeds the controls to match — the
    /// combos are the source of truth for what the user sees, and a reset that left them showing the
    /// old values would look like it had failed.
    /// </summary>
    private async void ResetPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Reset app preferences?",
            "Restore warning thresholds, cache signals, history recording and retention, and the default creation method. Your theme and saved history will not change.",
            "Reset", "ResetPreferencesConfirmDialog"))
        {
            return;
        }

        PreferencesService.Reset();

        AppPreferences prefs = PreferencesService.Current;
        _isReady = false;
        SelectByTag(LowFreeCombo, prefs.LowFreePercent);
        SelectByTag(RollupCombo, prefs.CacheRollupThreshold);
        SelectByTag(RetentionCombo, prefs.HistoryRetentionDays);
        CreationMethodCombo.SelectedIndex = prefs.PreferResizeOverVhdx ? 0 : 1;
        _isReady = true;

        // The theme is persisted by ThemeService rather than AppPreferences, so it is deliberately
        // left alone: someone who pinned the app to Light did not ask for that to be undone by a
        // button labelled "reset to defaults" sitting under a list of signal thresholds.
        Raise(nameof(WatchCachesOffDevDrive));
        Raise(nameof(RecordFreeSpaceHistory));
    }

    private async void Source_Click(object sender, RoutedEventArgs e)
    {
        const string repositoryUrl = "https://github.com/crutkas/devdrive";
        string message;
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(new Uri(repositoryUrl)))
            {
                return;
            }

            message = $"Windows could not open your browser. Visit {repositoryUrl}.";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            message = $"Windows could not open your browser: {ex.Message}\n\nVisit {repositoryUrl}.";
        }

        await ShowInfoDialogAsync("Couldn't open GitHub", message, "SourceLaunchErrorDialog");
    }

    // ---- Plumbing ------------------------------------------------------------------------------

    private void ReadHistory()
    {
        IReadOnlyList<FreeSpaceSample> samples = _history.Load();
        _historyCount = samples.Count;
        _historyOldest = samples.Count == 0 ? null : samples.Min(s => s.TakenAtUtc);
        Raise(nameof(HasHistory));
        Raise(nameof(HistorySummaryText));
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action, string automationId)
    {
        if (_isDialogOpen) return false;
        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            };
            AutomationProperties.SetAutomationId(dialog, automationId);
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    /// <summary>Points a combo at the item whose <c>Tag</c> is this number.</summary>
    private static void SelectByTag(ComboBox combo, int value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && int.TryParse(item.Tag as string, out int tag)
                && tag == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        // A stored value with no matching item means a future build wrote something this one does
        // not offer. Showing nothing selected is the honest answer; silently rewriting the store to
        // whatever happens to be first would lose the newer setting.
        combo.SelectedIndex = -1;
    }

    private static int? TagOf(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag as string, out int tag)
            ? tag
            : null;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Shows a selectable message in a scrollable content dialog.</summary>
    /// <remarks>
    /// Guarded because WinUI allows exactly one <see cref="ContentDialog"/> at a time and a second
    /// <c>ShowAsync</c> throws. A double-click on the button is the obvious way to reach that.
    /// </remarks>
    private async Task ShowInfoDialogAsync(string title, string message, string automationId)
    {
        if (_isDialogOpen)
        {
            return;
        }

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

        _isDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }
    }
}
