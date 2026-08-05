using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DevDriveManager.Views;

/// <summary>
/// The Create room: pick a method, a source, a size and a name, then commit once.
/// </summary>
/// <remarks>
/// <para>
/// The comp draws this as a modal dialog and it stays a room here. The rail already has a Create
/// entry that six other rooms' primary action points at; this build's form carries three fields the
/// comp's does not (the VHDX path, the disk type, the unit); and a dialog cannot carry the room
/// grammar's status bar, which is where the "about two minutes, no reboot" promise belongs.
/// </para>
/// <para>
/// The page stays thin. It owns the <see cref="CreateDevDriveViewModel"/>, hosts the strip and the
/// status bar, and translates the view model's events into platform UI — the gating confirmation
/// dialog and the folder picker. Every decision lives in the view model and the unit-tested core.
/// </para>
/// <para>
/// <b>SAFETY:</b> VHDX and resize creation start only from the blocking confirmation dialog. The
/// resize helper verifies and binds live disk state before mutation, in the same elevated invocation.
/// </para>
/// </remarks>
public sealed partial class CreateDevDrivePage : Page, INotifyPropertyChanged
{
    private readonly ObservableCollection<VolumeStripEntry> _volumeStripEntries = [];
    private readonly IVolumeProvider _volumeProvider = new SystemVolumeProvider();

    private bool _isConfirmOpen;
    private bool _isPickerOpen;
    private bool _isSubscribed;

    public CreateDevDrivePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The form's state, shared across visits.
    /// </summary>
    /// <remarks>
    /// On <c>App</c> rather than on the page because WinUI rebuilds a page on every navigation, and a
    /// half-filled create form is exactly the kind of state a user expects to find where they left
    /// it. It is also the only way the completion surface survives the trip to Caches and back.
    /// </remarks>
    public CreateDevDriveViewModel ViewModel => App.SharedCreate;

    /// <summary>The volumes this room is about to change. Same strip as every other room.</summary>
    public IReadOnlyList<VolumeStripEntry> VolumeStripEntries => _volumeStripEntries;

    /// <summary>
    /// The right-hand slider tick. Names the ceiling and why it is the ceiling, because "141 GB" on
    /// its own does not say whether the number came from the disk or from this app.
    /// </summary>
    public string MaximumTickText => ViewModel.IsResize
        ? $"{ViewModel.SliderMaximumGb:N0} GB \u2014 all shrinkable space"
        : $"{ViewModel.SliderMaximumGb:N0} GB \u2014 free space on the host volume";

    /// <summary>Where the finished drive will appear, spelled the way the shell will show it.</summary>
    public string MountPointText =>
        $"Mounts as {ViewModel.SelectedDriveLetter}\\ \u2014 labelled \u201C{(string.IsNullOrWhiteSpace(ViewModel.Label) ? "DevDrive" : ViewModel.Label)}\u201D";

    /// <summary>
    /// The reclaim tie-in, in the states it can honestly be in.
    /// </summary>
    /// <remarks>
    /// A shrink can only use free space at the <i>end</i> of the volume, so clearing space before
    /// repartitioning is one of the few orderings in this app that changes the outcome rather than
    /// just the wait. The wording never claims a specific new maximum: whether freed bytes actually
    /// raise the shrink ceiling depends on where they sat on the volume, which no scan can say.
    /// </remarks>
    public string ReclaimTieInText
    {
        get
        {
            ReclaimViewModel reclaim = App.SharedReclaim;

            if (!ViewModel.IsResize)
            {
                return "A virtual disk repartitions nothing, but every byte it grows into comes out of "
                    + "the host volume's free space \u2014 so the ceiling above is the host's free "
                    + "space, and clearing space raises it directly.";
            }

            if (!reclaim.HasScanned)
            {
                return "Reclaim hasn't run yet. A shrink can only use free space at the end of the "
                    + "volume, so clearing space first often raises the maximum above \u2014 and it is "
                    + "space you keep either way.";
            }

            if (reclaim.FoundRiskMix.SafeBytes <= 0)
            {
                return "Reclaim found nothing safe to delete, so the maximum above is what the volume "
                    + "can give as it stands.";
            }

            return $"Reclaim found {reclaim.FoundRiskMix.SafeText} that is safe to delete \u2014 "
                + "regenerable files, nothing lost. Freeing it before you shrink can raise the maximum "
                + "above, because a shrink only uses free space at the end of the volume.";
        }
    }

    /// <summary>The tie-in card's title, which names the constraint rather than the room.</summary>
    public string ReclaimTieInHead => ViewModel.IsResize ? "Free space first" : "Room on the host";

    /// <summary>The tie-in's verb. Names the prize when there is one, the errand when there is not.</summary>
    public string ReclaimTieInAction
    {
        get
        {
            ReclaimViewModel reclaim = App.SharedReclaim;
            return reclaim.HasScanned && reclaim.FoundRiskMix.SafeBytes > 0
                ? $"Reclaim {reclaim.FoundRiskMix.SafeText}"
                : "Find space first";
        }
    }

    /// <summary>Shows <see cref="Visibility.Visible"/> when <paramref name="value"/> is true.</summary>
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows <see cref="Visibility.Visible"/> when <paramref name="value"/> is false.</summary>
    public static Visibility InvertBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Boolean negation for x:Bind.</summary>
    public static bool Not(bool value) => !value;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        RebuildVolumeStrip();
        UpdateStatus();
        await ViewModel.LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    /// <summary>
    /// Binds the page to the shared view model. Paired with <see cref="Unsubscribe"/> on
    /// <c>Unloaded</c> rather than done once in the constructor: a page can leave and re-enter the
    /// visual tree without being reconstructed, and a constructor-subscribe / Unloaded-unsubscribe
    /// pair leaves the second visit silently dead — the form would render but never update.
    /// </summary>
    private void Subscribe()
    {
        if (_isSubscribed)
        {
            return;
        }

        _isSubscribed = true;
        ViewModel.ConfirmRequested += OnConfirmRequested;
        ViewModel.BrowseVhdPathRequested += OnBrowseVhdPathRequested;
        ViewModel.DevDriveCreated += RefreshAfterCreationAsync;
        ViewModel.NavigateBackRequested += OnNavigateBack;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        App.SharedReclaim.PropertyChanged += OnReclaimPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (!_isSubscribed)
        {
            return;
        }

        _isSubscribed = false;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        App.SharedReclaim.PropertyChanged -= OnReclaimPropertyChanged;
        ViewModel.ConfirmRequested -= OnConfirmRequested;
        ViewModel.BrowseVhdPathRequested -= OnBrowseVhdPathRequested;
        ViewModel.DevDriveCreated -= RefreshAfterCreationAsync;
        ViewModel.NavigateBackRequested -= OnNavigateBack;
    }

    // ---- Method choice ---------------------------------------------------------------------------

    // The two radio buttons write the index rather than binding to it two-way: IsChecked is a
    // nullable bool and SourceIndex is an int, so a two-way binding would need a converter per
    // option and would still fire once for the button being cleared.
    private void MethodResize_Checked(object sender, RoutedEventArgs e) => SetSource(1);

    private void MethodVhdx_Checked(object sender, RoutedEventArgs e) => SetSource(0);

    private void SetSource(int index)
    {
        if (ViewModel.SourceIndex != index)
        {
            ViewModel.SourceIndex = index;
        }
    }

    // ---- Navigation ------------------------------------------------------------------------------

    private void FindSpaceFirst_Click(object sender, RoutedEventArgs e) => Navigate("reclaim");

    private void GoToCaches_Click(object sender, RoutedEventArgs e) => Navigate("caches");

    private void GoToBenchmarks_Click(object sender, RoutedEventArgs e) => Navigate("benchmarks");

    private void Navigate(string roomTag) => ShellPage.Current?.SelectNavItem(roomTag);

    private void OnNavigateBack() => Navigate("overview");

    // ---- Live state ------------------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CreateDevDriveViewModel.SliderMaximumGb):
            case nameof(CreateDevDriveViewModel.IsResize):
            case nameof(CreateDevDriveViewModel.SourceIndex):
                Raise(nameof(MaximumTickText));
                Raise(nameof(ReclaimTieInText));
                Raise(nameof(ReclaimTieInHead));
                UpdateStatus();
                break;
            case nameof(CreateDevDriveViewModel.SelectedDriveLetter):
            case nameof(CreateDevDriveViewModel.Label):
                Raise(nameof(MountPointText));
                break;

            // The strip is the machine's volumes, not a preview of the drive being configured, so it
            // only moves when a volume is actually created. Rebuilding it on DevDriveSizeText — which
            // changes on every slider tick — meant a CreateFile plus a DeviceIoControl per volume,
            // synchronously on the UI thread, for every pixel of a drag.
            case nameof(CreateDevDriveViewModel.IsComplete):
                RebuildVolumeStrip();
                UpdateStatus();
                break;
            case nameof(CreateDevDriveViewModel.IsBusy):
            case nameof(CreateDevDriveViewModel.HasSizeError):
            case nameof(CreateDevDriveViewModel.DevDriveSizeText):
                UpdateStatus();
                break;
        }
    }

    private void OnReclaimPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReclaimViewModel.HasScanned) or nameof(ReclaimViewModel.FoundRiskMix))
        {
            Raise(nameof(ReclaimTieInText));
            Raise(nameof(ReclaimTieInAction));
        }
    }

    private void RebuildVolumeStrip()
    {
        IReadOnlyList<StorageVolume> volumes;
        try
        {
            volumes = _volumeProvider.GetFixedVolumes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _volumeStripEntries.Clear();
            return;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        _volumeStripEntries.Clear();
        foreach (VolumeStripEntry entry in VolumeStrip.Build(volumes, systemRoot, new Dictionary<string, long>()))
        {
            _volumeStripEntries.Add(entry);
        }
    }

    /// <summary>
    /// The room's closing edge. Carries the comp's dialog footer — how long it takes, whether the
    /// machine reboots, and whether the source volume stays online — because those are the three
    /// things a reader wants confirmed with a finger over the Create button.
    /// </summary>
    private void UpdateStatus()
    {
        StatusBar.Facts.Clear();

        if (ViewModel.IsComplete)
        {
            StatusBar.Facts.Add(new StatusFact("Done", StatusEmphasis.Good));
            StatusBar.Facts.Add(new StatusFact(ViewModel.CompletionTitle));
            return;
        }

        if (ViewModel.IsBusy)
        {
            StatusBar.Facts.Add(new StatusFact("Working", StatusEmphasis.Warn));
            StatusBar.Facts.Add(new StatusFact("Do not power off the machine"));
            return;
        }

        StatusBar.Facts.Add(ViewModel.IsResize
            ? new StatusFact("Resizing a volume")
            : new StatusFact("Creating a virtual disk"));

        StatusBar.Facts.Add(ViewModel.HasSizeError
            ? new StatusFact(ViewModel.SizeErrorText, StatusEmphasis.Bad)
            : new StatusFact(ViewModel.DevDriveSizeText, StatusEmphasis.Good));

        StatusBar.Facts.Add(ViewModel.IsResize
            ? new StatusFact("About 2 minutes \u00B7 no reboot \u00B7 the source volume stays online")
            : new StatusFact("About 1 minute \u00B7 no reboot \u00B7 nothing is repartitioned"));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- Platform UI -----------------------------------------------------------------------------

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
                DefaultButton = ContentDialogButton.Primary,
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

    // All top-level pages bind to App.Shared, so this is the same reload as the Overview refresh and
    // updates drives, caches, benchmarks and health.
    private static Task RefreshAfterCreationAsync() => App.Shared.LoadCommand.ExecuteAsync(null);

    private async void OnBrowseVhdPathRequested()
    {
        // Same reason the confirmation dialog is guarded: a second picker on top of a live one throws,
        // and an unhandled exception on an async void handler takes the process with it.
        if (_isPickerOpen)
        {
            return;
        }

        _isPickerOpen = true;
        try
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
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Choosing no folder is a normal outcome; the path already in the box stays.
        }
        finally
        {
            _isPickerOpen = false;
        }
    }
}
