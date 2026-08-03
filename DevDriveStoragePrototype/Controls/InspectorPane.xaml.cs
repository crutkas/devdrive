using System.ComponentModel;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveStoragePrototype.Controls;

/// <summary>
/// Describes the selected node. Physical facts come first and always render;
/// provider metadata is additive and degrades to a plain statement when missing.
/// </summary>
public sealed partial class InspectorPane : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty NodeProperty =
        DependencyProperty.Register(
            nameof(Node),
            typeof(StorageNode),
            typeof(InspectorPane),
            new PropertyMetadata(null, OnDetailsChanged));

    public static readonly DependencyProperty ScopeProperty =
        DependencyProperty.Register(
            nameof(Scope),
            typeof(StorageNode),
            typeof(InspectorPane),
            new PropertyMetadata(null, OnDetailsChanged));

    public InspectorPane() => InitializeComponent();

    public event PropertyChangedEventHandler? PropertyChanged;

    public StorageNode? Node
    {
        get => (StorageNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    public StorageNode? Scope
    {
        get => (StorageNode?)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    public bool HasSelection => Node is not null;

    public bool HasNoSelection => Node is null;

    public bool HasProvider => Node?.Provider is not null;

    /// <summary>
    /// True only when apparent size differs from allocated bytes, which is the one
    /// case worth spending vertical space on.
    /// </summary>
    public bool HasLogicalDifference => Node?.HasLogicalDifference == true;

    public string SelectedName => Node?.Name ?? string.Empty;

    public string SelectedPath => Node?.PhysicalPath ?? string.Empty;

    public string SelectedSize => Node?.SizeDisplay ?? "—";

    public string SelectedLogical => Node?.LogicalDisplay ?? "—";

    public string SelectedKind => Node?.KindDisplay ?? "—";

    public string SelectedItems => Node?.ItemCountDisplay ?? "—";

    public string SelectedModified => Node?.ModifiedDisplay ?? "—";

    /// <summary>
    /// Type and item count on one line. Both facts are short, so a labelled grid
    /// cost more vertical space than it bought in clarity.
    /// </summary>
    public string PhysicalIdentity => Node switch
    {
        null => string.Empty,
        { Kind: StorageNodeKind.Folder } node => $"Folder · {node.ItemCountDisplay} items",
        _ => "File",
    };

    public string ModifiedSummary =>
        Node is null ? string.Empty : $"Modified {Node.ModifiedDisplay}";

    public string SelectedShare
    {
        get
        {
            if (Node is null || Scope is null || Scope.SizeBytes <= 0)
            {
                return "—";
            }

            return ((double)Node.SizeBytes / Scope.SizeBytes).ToString("P1");
        }
    }

    public string LogicalExplanation => Node switch
    {
        null => string.Empty,
        { LogicalBytes: null } => "Apparent size matches the bytes allocated.",
        { HasLogicalDifference: false } => "Apparent size matches the bytes allocated.",
        _ => "Sparse or shared, so reclaiming it frees less than it appears.",
    };

    public string ProviderName => Node?.ProviderDisplay ?? "No provider metadata";

    public string ProviderIdentity => Node?.Provider?.LogicalIdentity
        ?? "This item is identified only by its physical path.";

    public string ProviderStatus => Node?.Provider?.AvailabilityDisplay ?? string.Empty;

    public string ProviderEvidence => Node?.Provider?.Evidence ?? string.Empty;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private static void OnDetailsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var pane = (InspectorPane)dependencyObject;

        // A null name tells the binding engine every projected property changed,
        // which is exactly right: all of them are derived from Node and Scope.
        pane.PropertyChanged?.Invoke(pane, new PropertyChangedEventArgs(null));
    }
}
