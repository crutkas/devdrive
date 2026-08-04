using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DevDriveManager.Controls;

/// <summary>
/// Maps a <see cref="VolumeAccentRole"/> to the brush its capacity bar uses.
/// </summary>
/// <remarks>
/// The library decides which volume plays which part; the theme decides what each part looks like.
/// Keeping the split here is what lets high contrast replace the whole palette without the storage
/// library knowing that themes exist.
/// </remarks>
public sealed partial class VolumeAccentRoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is VolumeAccentRole role
            ? role switch
            {
                VolumeAccentRole.DevDrive => "SmCategory2Brush",
                VolumeAccentRole.System => "SmCategory0Brush",
                _ => "SmCategory5Brush",
            }
            : "SmCategory5Brush";

        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is Brush resolved
            ? resolved
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
