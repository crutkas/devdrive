using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DevDriveStorage;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be repopulated in one shot.
/// </summary>
/// <remarks>
/// Clearing and re-adding item by item raises one change notification per item, and
/// list controls respond by re-measuring on every notification. A scope with a few
/// thousand files is enough to stall the UI thread for seconds. <see cref="ReplaceAll"/>
/// mutates the backing list quietly and then raises a single reset, which list
/// controls handle with one virtualized layout pass.
/// </remarks>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    /// <summary>
    /// Replaces the entire contents of the collection, raising a single reset
    /// notification instead of one notification per item.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (T item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnPropertyChanged(e);
        }
    }
}
