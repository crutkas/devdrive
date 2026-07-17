using System.IO;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DevDriveManager.Views;

/// <summary>
/// The "Create a Dev Drive" flow (Treatment&#160;A). The page is thin: it owns the
/// <see cref="CreateDevDriveViewModel"/>, hosts the disk-bar + slider + number-box size control, and
/// translates the view model's events into platform UI (the gating confirmation dialog and the file/
/// folder pickers). All logic and state live in the view model and the unit-tested core.
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> the page never executes anything on its own. <see cref="OnConfirmRequested"/> shows a
/// blocking dialog whose primary button is the only path to <see cref="CreateDevDriveViewModel.ExecuteConfirmedAsync"/>
/// — VHDX creation then crosses UAC once for its complete guarded transaction, while resize first performs
/// a read-only preview and requires a second confirmation before its elevated mutation.
/// </remarks>
public sealed partial class CreateDevDrivePage : Page
{
    private bool _isConfirmOpen;

    public CreateDevDriveViewModel ViewModel { get; } = CreateDevDriveViewModel.CreateDefault();

    public CreateDevDrivePage()
    {
        InitializeComponent();
        ViewModel.ConfirmRequested += OnConfirmRequested;
        ViewModel.BrowseVhdPathRequested += OnBrowseVhdPathRequested;
        ViewModel.NavigateBackRequested += OnNavigateBack;
        Loaded += OnLoaded;
    }

    /// <summary>Shows <see cref="Visibility.Visible"/> when <paramref name="value"/> is true.</summary>
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows <see cref="Visibility.Visible"/> when <paramref name="value"/> is false.</summary>
    public static Visibility InvertBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Boolean negation for x:Bind (e.g. an InfoBar that opens when a flag is false).</summary>
    public static bool Not(bool value) => !value;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnNavigateBack() => GoBack();

    private void GoBack()
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        if (Frame?.CanGoBack == true)
        {
            Frame.GoBack();
        }
    }

    /// <summary>Builds and shows the gating confirmation dialog; only its primary button runs the action.</summary>
    private async void OnConfirmRequested(ConfirmRequest request)
    {
        if (_isConfirmOpen)
        {
            return;
        }

        _isConfirmOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap });

            if (request.Steps.Count > 0)
            {
                var steps = new StackPanel { Spacing = 6 };
                foreach (string step in request.Steps)
                {
                    steps.Children.Add(new TextBlock { Text = "\u2022  " + step, TextWrapping = TextWrapping.Wrap });
                }

                content.Children.Add(steps);
            }

            var dialog = new ContentDialog
            {
                Title = request.Title,
                Content = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 460,
                },
                PrimaryButtonText = request.ConfirmText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close, // safe default: Cancel is highlighted
                XamlRoot = XamlRoot,
            };

            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) && style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            AutomationProperties.SetAutomationId(dialog, "ConfirmCreateDialog");

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.ExecuteConfirmedAsync();
            }
        }
        finally
        {
            _isConfirmOpen = false;
        }
    }

    private async void OnBrowseVhdPathRequested()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.VhdFilePath = Path.Combine(folder.Path, "DevDrive.vhdx");
        }
    }
}
