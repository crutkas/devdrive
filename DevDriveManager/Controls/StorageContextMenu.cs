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
        return menu;
    }
}
