using DevDriveManager.ViewModels;
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
public sealed partial class ReclaimPage : Page
{
    public ReclaimViewModel ViewModel { get; } = App.SharedReclaim;

    public ReclaimPage() => InitializeComponent();
}
