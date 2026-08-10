using System.ComponentModel;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveReclaim;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The Reclaim room: what can be deleted, why it is or is not safe, and what the machine looks like
/// afterwards.
/// </summary>
/// <remarks>
/// Scanning is not started automatically. A full scan is I/O bound and takes minutes on a real
/// machine, so it is something the user asks for rather than something that begins the moment they
/// glance at the room.
/// </remarks>
public sealed partial class ReclaimPage : Page, INotifyPropertyChanged
{
    private double _nameColumnWidth = 220;

    public ReclaimViewModel ViewModel { get; } = App.SharedReclaim;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// An explicit width for the candidate name column, shared by the header and every row.
    /// <para>
    /// Same reasoning as the Space room: the fixed columns plus gaps and padding claim a known
    /// number of DIPs, so computing the remainder makes the floor explicit and keeps the header
    /// aligned with the rows for free, because both read this one number.
    /// </para>
    /// </summary>
    public GridLength NameColumnWidth => new(_nameColumnWidth);

    public ReclaimPage()
    {
        InitializeComponent();

        // The status bar is rebuilt from the ViewModel rather than bound fact by fact: the facts are
        // a list whose length changes with state, so per-slot bindings would need a slot for every
        // possible fact plus a visibility rule for each.
        //
        // Subscribed on Loaded and released on Unloaded because the ViewModel is app-lifetime and
        // shared. A page that stayed subscribed would be held alive by it for the life of the app,
        // still updating a status bar nobody is looking at.
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.ConfirmationRequested = ConfirmReclaimAsync;
            AttachItemsSources();
            UpdateStatusBar();
        };

        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            DetachItemsSources();

            // Released with the page. The ViewModel outlives it, and a hook holding a XamlRoot from
            // an unloaded page would show the confirmation on a window that is no longer there.
            if (ViewModel.ConfirmationRequested == ConfirmReclaimAsync)
            {
                ViewModel.ConfirmationRequested = null;
            }
        };
    }

    /// <summary>
    /// Drops every list this page pointed at a collection on the shared ViewModel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rule the handlers above follow, applied to the subscriptions XAML makes on this
    /// page's behalf. An <c>ItemsSource</c> binding to a collection on <see cref="App.SharedReclaim"/>
    /// registers a <em>native</em> listener that outlives the page, and NavigationCacheMode is left
    /// at its Disabled default, so each visit to the room built a new list and left the previous
    /// one subscribed. Measured on a live app: three handlers on both <c>Categories</c> and
    /// <c>VolumeImpacts</c> with the room unloaded, growing with visits.
    /// </para>
    /// <para>
    /// Dispatching a collection change to a torn-down control fail-fasts the process from native
    /// code, which nothing here can catch. <c>SpacePage</c> carries the crash dumps that proved it.
    /// </para>
    /// </remarks>
    private void DetachItemsSources()
    {
        CategoryList.ItemsSource = null;
        RowList.ItemsSource = null;
        VolumeImpactsList.ItemsSource = null;
    }

    /// <summary>
    /// Points the lists back at the shared ViewModel for as long as this page is on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CategoryList</c> and <c>VolumeImpactsList</c> restore what <see cref="DetachItemsSources"/>
    /// dropped; those bindings are <c>x:Bind</c>'s implicit OneTime, so nothing re-pushes a source
    /// behind us.
    /// </para>
    /// <para>
    /// <c>RowList</c> is assigned from here instead of bound because it followed a <em>changing</em>
    /// path, <c>SelectedCategory.Rows</c>, in OneWay mode. A OneWay binding stays live on an
    /// unloaded page — that is the leak, not a side effect of it — so it would re-attach the moment
    /// the selected category changed, undoing the release. A scan does exactly that, and Reclaim
    /// scans run for minutes while the user is in another room. Driving it from
    /// <see cref="OnViewModelPropertyChanged"/>, which is itself subscribed only between Loaded and
    /// Unloaded, is what makes the release hold.
    /// </para>
    /// </remarks>
    private void AttachItemsSources()
    {
        CategoryList.ItemsSource = ViewModel.Categories;
        VolumeImpactsList.ItemsSource = ViewModel.VolumeImpacts;
        UpdateRowList();
    }

    private void UpdateRowList() => RowList.ItemsSource = ViewModel.SelectedCategory?.Rows;

    /// <summary>
    /// Records a category the user picked, and ignores the list emptying its own selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both selections in this room used to be TwoWay <c>x:Bind</c>s. A <c>ListView</c> drops
    /// <c>SelectedItem</c> when its items go away, so the release in
    /// <see cref="DetachItemsSources"/> pushed that null straight back into the app-lifetime
    /// ViewModel and the room came back empty. Measured across a real scan: 30 candidate rows
    /// before leaving the room, 0 on return, against 30 on the build without the release.
    /// </para>
    /// <para>
    /// Capturing the selection around the detach and restoring it was tried first and did not hold —
    /// the list clears itself again after the restore. Reading one way and writing only on a real
    /// pick is immune to <em>when</em> the clear lands, which is the property that matters here.
    /// <c>DrivesPage</c> reached the same conclusion from the other direction, by refusing null in
    /// its setter.
    /// </para>
    /// <para>
    /// An empty <c>AddedItems</c> is therefore never written through. The ViewModel can still clear
    /// its own selection — the OneWay read carries that to the list — but the list cannot clear the
    /// ViewModel's.
    /// </para>
    /// </remarks>
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count > 0 && args.AddedItems[0] is ReclaimCategoryViewModel category)
        {
            ViewModel.SelectedCategory = category;
        }
    }

    /// <inheritdoc cref="CategoryList_SelectionChanged"/>
    private void RowList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count > 0 && args.AddedItems[0] is ReclaimRowViewModel row)
        {
            ViewModel.SelectedRow = row;
        }
    }

    /// <summary>
    /// The last honest moment before anything is deleted.
    /// </summary>
    /// <remarks>
    /// Three things are stated in a fixed order, because the order is the argument: what is about to
    /// happen, what of it cannot be undone, and how bad the worst item in the pile is. The amount
    /// freed is deliberately not the largest text on the screen — this is a confirmation, and a
    /// confirmation that leads with the reward is a nudge.
    /// <para>
    /// A <see cref="ReclaimRisk.Careful"/> item requires typing DELETE. Anything gated behind a
    /// single click gets the same reflexive click as everything else, and the Careful tier exists
    /// precisely for the items where that reflex is expensive.
    /// </para>
    /// </remarks>
    private async System.Threading.Tasks.Task<bool> ConfirmReclaimAsync(ReclaimConfirmationRequest request)
    {
        if (_dialogOpen || XamlRoot is null)
        {
            return false;
        }

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            Text = request.RiskText,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        var recovery = new TextBlock
        {
            Text = request.RecoveryText,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(recovery, "ReclaimConfirmRecovery");
        body.Children.Add(recovery);

        if (request.VolumeSplitText.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Space comes back: " + request.VolumeSplitText,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

        TextBox? typed = null;
        if (request.RequiresTypedConfirmation)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Type DELETE to confirm.",
                TextWrapping = TextWrapping.Wrap,
            });

            typed = new TextBox { PlaceholderText = "DELETE" };
            AutomationProperties.SetAutomationId(typed, "ReclaimConfirmTypedInput");
            AutomationProperties.SetName(typed, "Type DELETE to confirm");
            body.Children.Add(typed);
        }

        var dialog = new ContentDialog
        {
            Title = request.Headline,
            Content = body,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",

            // Cancel is the default so that Enter, and a dialog that appears under a moving cursor,
            // both do the harmless thing.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        ApplyDialogStyle(dialog);
        AutomationProperties.SetAutomationId(dialog, "ReclaimConfirmDialog");

        if (typed is not null)
        {
            dialog.IsPrimaryButtonEnabled = false;
            typed.TextChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled =
                    string.Equals(typed.Text.Trim(), "DELETE", StringComparison.Ordinal);
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>Guarded because WinUI allows exactly one <see cref="ContentDialog"/> at a time.</summary>
    private bool _dialogOpen;

    private static void ApplyDialogStyle(ContentDialog dialog)
    {
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style)
            && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
    }

    /// <summary>
    /// Everything in a candidate row that is not the name, in DIPs: the RISK and SIZE columns
    /// (76 + 84), the two 12px gaps, the row's own 14px horizontal padding, and the card's 1px
    /// border on each side.
    /// </summary>
    private const double TableFixedColumnsWidth = 76 + 84 + (12 * 2) + 28 + 2;

    /// <summary>
    /// Measures the table card, not the header grid. Measuring the header would feed back on
    /// itself: a wider name column grows the header's desired width, the grid is arranged at that
    /// desired width, and the next measurement reports the inflated number. The card's width comes
    /// from the page's star column and does not depend on anything inside it.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // NewSize is in DIPs, which is the same unit the ColumnDefinition widths are in. Do not
        // sanity-check this against UI-automation rectangles — those are physical pixels, and the
        // two only agree at 100% scale.
        double available = Math.Max(140, args.NewSize.Width - TableFixedColumnsWidth);
        if (Math.Abs(available - _nameColumnWidth) < 0.5)
        {
            return;
        }

        _nameColumnWidth = available;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameColumnWidth)));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReclaimViewModel.SelectedCategory))
        {
            UpdateRowList();
        }

        UpdateStatusBar();
    }

    private void ScanBar_ScanRequested(object? sender, EventArgs e)
    {
        if (ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
        }
    }

    private void ScanBar_CancelRequested(object? sender, EventArgs e)
    {
        if (ViewModel.CancelScanCommand.CanExecute(null))
        {
            ViewModel.CancelScanCommand.Execute(null);
        }
    }

    private void UpdateStatusBar()
    {
        StatusBar.Facts.Clear();

        if (ViewModel.IsScanning)
        {
            StatusBar.Facts.Add(new StatusFact("Scanning"));
        }
        else if (ViewModel.ScanError is not null)
        {
            StatusBar.Facts.Add(new StatusFact("Scan failed", StatusEmphasis.Bad));
        }
        else if (!ViewModel.HasScanned)
        {
            StatusBar.Facts.Add(new StatusFact("Not scanned yet"));
        }
        else
        {
            StatusBar.Facts.Add(new StatusFact($"{ViewModel.FoundBytesText} found", StatusEmphasis.Good));
        }

        // Selection is shown whenever anything is ticked, including mid-scan: the number the user is
        // about to act on should never be the one that is hidden.
        if (ViewModel.SelectedBytes > 0)
        {
            StatusBar.Facts.Add(
                new StatusFact($"{ViewModel.SelectedBytesText} selected", StatusEmphasis.Warn));
        }
    }
}
