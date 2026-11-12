using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IPathProbe"/>. Walks the <c>PATH</c> directories (applying <c>PATHEXT</c> when the
/// requested name has no extension) and returns the first executable that exists. Read-only: it never
/// launches the file it resolves. Folders on another computer are skipped (<see cref="IsNetworkFolder"/>).
/// </summary>
public sealed class PathProbe : IPathProbe
{
    private static readonly char[] PathExtSeparators = { ';' };

    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private readonly Func<string?> _path;

    public PathProbe()
        : this(() => Environment.GetEnvironmentVariable("PATH"))
    {
    }

    /// <param name="path">Reads the PATH value to walk.</param>
    public PathProbe(Func<string?> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = path;
    }

    /// <summary>
    /// A folder on another computer: <c>\\server\share</c>, <c>\\?\UNC\server\share</c>, or a mapped network drive. Looking for
    /// a file there waits on the network, about 20 seconds per lookup when the server doesn't answer, so tools aren't looked
    /// for there.
    /// </summary>
    public static bool IsNetworkFolder(string dir)
    {
        if (dir.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dir.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return !dir.StartsWith(@"\\?\", StringComparison.Ordinal) && !dir.StartsWith(@"\\.\", StringComparison.Ordinal);
        }

        try
        {
            return dir.Length >= 2 && dir[1] == ':' && char.IsAsciiLetter(dir[0]) && new DriveInfo(dir[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? Resolve(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        string? pathEnv = _path();
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultPathExt)
            .Split(PathExtSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool nameHasExtension = Path.HasExtension(executableName);

        foreach (string rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = rawDir.Trim('"');
            if (IsNetworkFolder(dir))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(dir, executableName);
            }
            catch (ArgumentException)
            {
                // Skip malformed PATH entries (illegal characters, etc.).
                continue;
            }

            if (nameHasExtension)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            foreach (string ext in extensions)
            {
                string withExt = candidate + ext;
                if (File.Exists(withExt))
                {
                    return withExt;
                }
            }

            // Fall back to the bare (extensionless) name in case it is directly executable.
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
