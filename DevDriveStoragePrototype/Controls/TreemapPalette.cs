using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevDriveStoragePrototype.Controls;

/// <summary>
/// Resolves the shared rank-based colour palette. The table dot and the treemap
/// rectangle for a given item must always agree, so both go through here.
/// </summary>
public static class TreemapPalette
{
    private static readonly string[] BrushKeys =
    [
        "TreemapBrush0",
        "TreemapBrush1",
        "TreemapBrush2",
        "TreemapBrush3",
        "TreemapBrush4",
        "TreemapBrush5",
    ];

    public static int SlotCount => BrushKeys.Length;

    public static Brush Resolve(int colorIndex)
    {
        int slot = ((colorIndex % BrushKeys.Length) + BrushKeys.Length) % BrushKeys.Length;
        return Application.Current.Resources[BrushKeys[slot]] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
