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

        // Size derived from the new NavigationView shell (248px pane + content): ~1180 x 820 DIPs.
        // AppWindow.Resize takes physical pixels, so scale by the window DPI.
        nint hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(820 * scale)));

        // Navigate the root frame to the navigation shell on startup.
        RootFrame.Navigate(typeof(ShellPage));

        // Apply the persisted theme override (System / Light / Dark) to the live content root.
        DevDriveManager.Services.ThemeService.Initialize();
        RootFrame.RequestedTheme = DevDriveManager.Services.ThemeService.Mode;
    }
}
