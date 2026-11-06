namespace DevDriveStorage.Live;

/// <summary>
/// Every folder listing the walkers make (the Space scan, <see cref="DirectoryMeasurer"/>, <see cref="FastDirectoryWalker"/>
/// and the Reclaim detectors' repository walk), for a caller that wants to see how often each folder is read. Off unless a
/// listener is set; the listener is called on the walkers' threads.
/// </summary>
public static class DirectoryReadTrace
{
    private static volatile Action<string>? s_listener;

    /// <summary>Called with each folder's path as it's listed.</summary>
    public static Action<string>? Listener
    {
        get => s_listener;
        set => s_listener = value;
    }

    /// <summary>One listing of <paramref name="path"/>. Walkers outside this assembly call it too.</summary>
    public static void Record(string path) => s_listener?.Invoke(path);
}
