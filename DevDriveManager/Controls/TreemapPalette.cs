using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevDriveManager.Controls;

/// <summary>
/// Resolves the shared rank-based colour palette. The table swatch and the treemap rectangle for a
/// given row must always agree, so both go through here.
/// </summary>
/// <remarks>
/// Ported from the prototype's <c>TreemapPalette</c>, retargeted from the <c>Treemap*</c> keys onto
/// the Storage Manager <c>SmCategory*</c> tokens. The two palettes happen to be byte-identical, so
/// this is a rename rather than a re-colour — but going through the shared token means a future
/// palette change lands in one dictionary instead of two.
/// </remarks>
public static class TreemapPalette
{
    private static readonly string[] BrushKeys =
    [
        "SmCategory0Brush",
        "SmCategory1Brush",
        "SmCategory2Brush",
        "SmCategory3Brush",
        "SmCategory4Brush",
        "SmCategory5Brush",
    ];

    public static int SlotCount => BrushKeys.Length;

    public static Brush Resolve(int colorIndex)
    {
        int slot = ((colorIndex % BrushKeys.Length) + BrushKeys.Length) % BrushKeys.Length;
        return Application.Current.Resources[BrushKeys[slot]] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
