using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The "Drives" page: the Dev Drive hero (identity, capacity, trust state, Defender performance mode,
/// filter drivers) plus a banded table of every fixed volume on the PC. Binds to the shared
/// <see cref="App.Shared"/> view model so it reflects the same load as every other page.
/// </summary>
public sealed partial class DrivesPage : Page
{
    public MainPageViewModel ViewModel => App.Shared;

    public DrivesPage()
    {
        InitializeComponent();
    }
}
