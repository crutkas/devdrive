using DevDriveManager.ViewModels;
using DevDriveStorage;
using DevDriveStorage.Live;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DevDriveManager;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// The single, shared composition-root view model. Every page in the NavigationView shell binds to
    /// this one instance (or its sub-view-models), so the expensive volume / Dev Drive / package-cache
    /// load runs once and all pages reflect the same live state. Created lazily on first access.
    /// </summary>
    public static MainPageViewModel Shared { get; } = new();

    /// <summary>
    /// The shared Reclaim room state. Separate from <see cref="Shared"/> because a reclaim scan is
    /// minutes long and explicitly user-initiated, so it must not be dragged into the startup load —
    /// but shared, so navigating away from the room and back does not discard a completed scan or,
    /// worse, silently abandon one still running.
    /// </summary>
    public static ReclaimViewModel SharedReclaim { get; } = new();

    /// <summary>
    /// The shared Space room state, for the same reason as <see cref="SharedReclaim"/>: a cold
    /// full-volume walk is minutes long, so a scan must outlive the page that started it. WinUI
    /// rebuilds a page on every navigation to it, so a page-owned view model would throw away a
    /// finished scan the moment the user looked at another room — and leave an unfinished one
    /// walking the disk on behalf of a page nobody can see.
    /// </summary>
    public static StorageExplorerViewModel SharedSpace { get; } =
        new(new LiveStorageSnapshotSource());

    /// <summary>
    /// The shared Create room state. A half-filled create form is exactly the kind of state a user
    /// expects to find where they left it, and WinUI rebuilds a page on every navigation to it — so a
    /// page-owned view model would silently reset the size, letter and label the moment someone
    /// stepped over to Reclaim to free space first, which the room itself recommends.
    /// </summary>
    public static CreateDevDriveViewModel SharedCreate { get; } = CreateDevDriveViewModel.CreateDefault();

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Without these, any exception escaping an async void handler takes the process down with
        // no dialog, no log and nothing to diagnose from. On a desktop that manages someone's disk,
        // vanishing mid-operation is the worst way to fail: the user cannot tell whether the thing
        // they just confirmed happened, half-happened, or never started.
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private async void OnUnhandledException(
        object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Marking it handled keeps the window alive so the message can actually be read. The
        // alternative is a process that disappears while the dialog is still being constructed.
        e.Handled = true;

        Exception? exception = e.Exception;
        System.Diagnostics.Debug.WriteLine($"[unhandled] {exception}");

        try
        {
            if (Window?.Content?.XamlRoot is not Microsoft.UI.Xaml.XamlRoot root)
            {
                return;
            }

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = root,
                Title = "Something went wrong",
                Content = "The app hit an unexpected error. Nothing on your drives was changed by " +
                    "this failure.\n\n" + (exception?.Message ?? e.Message),
                CloseButtonText = "Close",
            };

            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog that cannot open must not become the next unhandled exception.
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[unobserved] {e.Exception}");
        e.SetObserved();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
