using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace DevDriveManager.Controls;

/// <summary>Maps a <see cref="StatusEmphasis"/> to the brush that expresses it.</summary>
/// <remarks>
/// The mapping lives here rather than on <see cref="StatusFact"/> so the fact stays a meaning and
/// the theme keeps ownership of colour — including high contrast, where these tokens resolve to the
/// user's system colours.
/// </remarks>
public sealed partial class StatusEmphasisToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is StatusEmphasis emphasis
            ? emphasis switch
            {
                StatusEmphasis.Good => "SmGoodBrush",
                StatusEmphasis.Warn => "SmWarnBrush",
                StatusEmphasis.Bad => "SmBadBrush",
                _ => "SmDimBrush",
            }
            : "SmDimBrush";

        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is Brush resolved
            ? resolved
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="StatusEmphasis"/> to a font weight, so emphasis is never carried by colour
/// alone — which would make the bar unreadable to anyone who cannot separate the two hues.
/// </summary>
public sealed partial class StatusEmphasisToWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is StatusEmphasis and not StatusEmphasis.None ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
