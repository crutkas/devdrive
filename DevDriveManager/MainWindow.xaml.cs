using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DevDriveManager;

/// <summary>
/// The application window. This hosts a Frame that is navigated to <see cref="ShellPage"/> (the
/// NavigationView shell) on startup; per-area UI lives in the pages under <c>Pages\</c>, not here.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // The Storage Manager rooms are drawn against a 1600x900 DIP canvas: a 64px rail, a 236px
        // subrail, a 312px inspector, and whatever is left for the centre. Below about 1400 DIPs the
        // centre column gets squeezed hard enough that the Space table has to start dropping
        // columns, so ask for the full design width and only give it up when the display cannot
        // hold it. AppWindow.Resize takes physical pixels, hence the DPI scaling.
        nint hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96.0;

        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(1600 * scale), (int)(work.Width * 0.94));
        int height = Math.Min((int)(900 * scale), (int)(work.Height * 0.94));
        AppWindow.Resize(new SizeInt32(width, height));

        // Preferences must load before anything reads them, and the first read happens during the
        // Navigate below: constructing ShellPage touches App.SharedCreate, whose constructor seeds
        // the create method from PreferResizeOverVhdx. Initialising afterwards meant that seed
        // always saw the default, so "prefer resize" silently never survived a restart even though
        // Settings showed it set.
        DevDriveManager.Services.ThemeService.Initialize();
        DevDriveManager.Services.PreferencesService.Initialize();

        // Navigate the root frame to the navigation shell on startup.
        RootFrame.Navigate(typeof(ShellPage));

        // Apply the persisted theme override (System / Light / Dark) to the live content root.
        RootFrame.RequestedTheme = DevDriveManager.Services.ThemeService.Mode;
    }
}
