namespace DevDriveReclaim;

/// <summary>Which volume a path's bytes are on.</summary>
/// <remarks>
/// <para>
/// Not always the volume the path is spelled on. A source root can be a junction or a symbolic link to another volume
/// (<c>C:\src</c> to <c>D:\src</c>), and the scan reaches everything under it by the link's path. The guard resolves that
/// path before it acts (<see cref="ReclaimPathGuard"/>), so the run recycles an item into D:'s Recycle Bin, while everything
/// that decided where its bytes go — the bin limit and room the plan and the run checked it against, the volume whose bar
/// moved — read C:, the spelled root. So those follow the path to where it really leads, as the guard does, and fall back
/// to the spelled root when it can't be resolved.
/// </para>
/// <para>
/// A path that can't be opened (it's gone, or it's denied) is placed by its nearest ancestor that can be: that's where a link
/// along the way would be. A network path is never opened, so a share that doesn't answer can't stall a scan or a
/// confirmation; it has no Recycle Bin either way.
/// </para>
/// </remarks>
public static class ReclaimVolume
{
    /// <summary>The root of the volume <paramref name="path"/> really leads to, e.g. <c>D:\</c>.</summary>
    public static string RootOf(string path) => RootOf(path, FinalPath);

    /// <summary>
    /// <see cref="RootOf(string)"/>, with <paramref name="finalPath"/> saying where a path leads (null when it can't be
    /// opened). Separate so the choice of volume is testable without a second volume.
    /// </summary>
    internal static string RootOf(string path, Func<string, string?> finalPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(finalPath);

        string spelled = Root(path);
        if (!IsLocalDrive(spelled))
        {
            return spelled;
        }

        for (string? current = path; !string.IsNullOrEmpty(current); current = Parent(current))
        {
            if (finalPath(current) is { Length: > 0 } final)
            {
                string root = Root(final);
                return root.Length > 0 ? root : spelled;
            }
        }

        return spelled;
    }

    /// <summary>
    /// Where <paramref name="path"/> leads through every junction, symbolic link and mount point on the way, as the guard
    /// resolves it; null when it can't be opened.
    /// </summary>
    internal static string? FinalPath(string path) => ReclaimPathGuard.TryResolveFinalPath(path, isDirectory: true);

    private static string Root(string path)
    {
        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string? Parent(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A drive letter on this PC, not a mapped network drive, so opening a path on it can't wait on a network.</summary>
    private static bool IsLocalDrive(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] is not ('\\' or '/'))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType is not (DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
