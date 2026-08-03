using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DevDriveStoragePrototype;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Title = "Dev Drive Storage Prototype";

        ResizeLogical(1600, 900);

        // Frame.Navigate swallows page construction failures, so a XAML error in
        // MainPage would otherwise show as a silently blank window.
        RootFrame.NavigationFailed += (_, args) =>
        {
            args.Handled = true;
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "devdrive-prototype-startup-error.txt"),
                args.Exception.ToString());
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    public void ResizeLogical(int width, int height)
    {
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(windowHandle) / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale)));

        CenterOnDisplay();
    }

    /// <summary>
    /// Keeps the window fully on screen. Repeated launches otherwise cascade the
    /// window off the work area, which silently breaks window capture.
    /// </summary>
    private void CenterOnDisplay()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);

        RectInt32 work = display.WorkArea;

        AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
    }
}
