using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IPathProbe"/>. Walks the <c>PATH</c> directories (applying <c>PATHEXT</c> when the
/// requested name has no extension) and returns the first executable that exists. Read-only: it never
/// launches the file it resolves.
/// </summary>
public sealed class PathProbe : IPathProbe
{
    private static readonly char[] PathExtSeparators = { ';' };

    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <inheritdoc />
    public string? Resolve(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
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
