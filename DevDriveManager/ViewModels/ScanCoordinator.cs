using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevDriveStorage;

namespace DevDriveManager.ViewModels;

/// <summary>
/// The one scan, seen from any room.
/// </summary>
/// <remarks>
/// <para>
/// Every room's Scan button starts the same pair of scans — Reclaim's search for removable space and
/// Space's walk of a volume (see <c>App.ScanEverything</c>). Each room used to report and cancel only
/// its own half: Space's Cancel stopped the walk and left the Reclaim scan running, Reclaim's Cancel
/// did the reverse, and Overview showed a Cancel button wired to nothing. One button started two
/// things and no button stopped both.
/// </para>
/// <para>
/// This is the shared answer. <see cref="IsScanning"/> is true while either half runs,
/// <see cref="StatusText"/> says which, and <see cref="Cancel"/> stops both, so "Scan this PC" and its
/// Cancel mean the same thing in every room.
/// </para>
/// </remarks>
public sealed class ScanCoordinator : ObservableObject
{
    private readonly ReclaimViewModel _reclaim;
    private StorageExplorerViewModel _space;

    public ScanCoordinator(ReclaimViewModel reclaim, StorageExplorerViewModel space)
    {
        _reclaim = reclaim;
        _space = space;
        _reclaim.PropertyChanged += OnPartChanged;
        _space.PropertyChanged += OnPartChanged;
    }

    /// <summary>True while the Reclaim scan or the Space walk is running.</summary>
    public bool IsScanning => _reclaim.IsScanning || _space.IsScanning;

    /// <summary>Which half is still running, for the scan bar. Empty when nothing is.</summary>
    public string StatusText => Describe(_reclaim.IsScanning, _space.IsScanning, _space.ScenarioId);

    /// <summary>Points the coordinator at a replacement Space view model (the UI-test fixture swaps it).</summary>
    public void Attach(StorageExplorerViewModel space)
    {
        ArgumentNullException.ThrowIfNull(space);

        if (ReferenceEquals(space, _space))
        {
            return;
        }

        _space.PropertyChanged -= OnPartChanged;
        _space = space;
        _space.PropertyChanged += OnPartChanged;
        Raise();
    }

    /// <summary>Stops both halves. Whatever each has already found is kept, exactly as with its own Cancel.</summary>
    public void Cancel()
    {
        if (_reclaim.CancelScanCommand.CanExecute(null))
        {
            _reclaim.CancelScanCommand.Execute(null);
        }

        _space.CancelScan();
    }

    internal static string Describe(bool reclaim, bool space, string? spaceRoot)
    {
        string volume = string.IsNullOrEmpty(spaceRoot) ? "a volume" : spaceRoot.TrimEnd('\\');

        return (reclaim, space) switch
        {
            (true, true) => $"Finding reclaimable space · mapping {volume}",
            (true, false) => "Finding reclaimable space",
            (false, true) => $"Mapping {volume} for Space",
            _ => string.Empty,
        };
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReclaimViewModel.IsScanning)
            or nameof(StorageExplorerViewModel.IsScanning)
            or nameof(StorageExplorerViewModel.ScenarioId)
            or null
            or "")
        {
            Raise();
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(StatusText));
    }
}
