namespace DevDriveCore.Services;

/// <summary>
/// Small, pure path utilities shared by the package-cache and source-location services. No I/O.
/// </summary>
internal static class PathHelpers
{
    /// <summary>
    /// Returns the uppercased drive letter of <paramref name="path"/> (e.g. <c>'C'</c>) when it is a
    /// rooted <c>X:\…</c> path, otherwise <c>null</c> (UNC paths, relative paths, empty input).
    /// </summary>
    public static char? DriveLetterOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        return char.ToUpperInvariant(root[0]);
    }

    /// <summary>Normalizes a path to a full path when possible; returns the trimmed input on failure. No I/O.</summary>
    public static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }
}
