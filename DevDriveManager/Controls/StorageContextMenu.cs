using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace DevDriveManager.Controls;

internal static class StorageContextMenu
{
    public static MenuFlyout Create(FrameworkElement owner, StorageNode node)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem
        {
            Text = "Open in File Explorer",
            Icon = new SymbolIcon(Symbol.OpenFile),
        };
        AutomationProperties.SetAutomationId(open, "OpenInFileExplorer");
        open.Click += async (_, _) =>
        {
            string? error = null;
            try
            {
                string path = node.Kind == StorageNodeKind.Folder
                    ? node.PhysicalPath
                    : Path.GetDirectoryName(node.PhysicalPath)
                        ?? throw new ArgumentException("The file has no containing folder.");
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
                if (!await Launcher.LaunchFolderAsync(folder))
                {
                    error = "Windows could not open the folder in File Explorer.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                error = $"The folder could not be opened. It may have moved or you may not have access.\n\n{ex.Message}";
            }

            if (error is not null && owner.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Couldn't open folder",
                    Content = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close",
                    XamlRoot = owner.XamlRoot,
                    Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                };
                await dialog.ShowAsync();
            }
        };
        menu.Items.Add(open);

        // A package cache found off the Dev Drive is the one folder here with a better home than the
        // Recycle Bin, so the map offers the move rather than leaving the user to find the Caches tab.
        if (node.Kind == StorageNodeKind.Folder && FindCacheOffDevDrive(node.PhysicalPath) is { } cache)
        {
            var move = new MenuFlyoutItem
            {
                Text = $"Move {cache.Header} cache to Dev Drive",
                Icon = new FontIcon { Glyph = "\uE8DE" },
            };
            AutomationProperties.SetAutomationId(move, "MoveCacheToDevDrive");
            move.Click += (_, _) => ShellPage.Current?.SelectNavItem("caches");
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(move);
        }

        return menu;
    }

    /// <summary>The detected cache that holds <paramref name="path"/> and is not yet on a Dev Drive.</summary>
    private static ViewModels.PackageCacheRowViewModel? FindCacheOffDevDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string folder = Path.TrimEndingDirectorySeparator(path);
        foreach (ViewModels.PackageCacheRowViewModel row in App.Shared.PackageCaches.Caches)
        {
            if (!row.Info.Detected || row.IsOnDevDrive || string.IsNullOrWhiteSpace(row.LocationText))
            {
                continue;
            }

            string cache = Path.TrimEndingDirectorySeparator(row.LocationText);
            if (folder.Equals(cache, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }
}
