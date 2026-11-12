using System.Collections.Concurrent;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IPathProbe"/>. Walks the <c>PATH</c> directories (applying <c>PATHEXT</c> when the
/// requested name has no extension) and returns the first executable that exists. Read-only: it never
/// launches the file it resolves. A file on another computer is checked within a bound (<see cref="FileExists"/>).
/// </summary>
public sealed class PathProbe : IPathProbe
{
    /// <summary>How long a check on another computer may wait.</summary>
    public static readonly TimeSpan NetworkLimit = TimeSpan.FromSeconds(1.5);

    private static readonly char[] PathExtSeparators = { ';' };

    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private static readonly ConcurrentDictionary<string, Lazy<Share>> Shares = new(StringComparer.OrdinalIgnoreCase);

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
    /// a file there waits on the network, about 20 seconds per lookup when the server doesn't answer.
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

    /// <summary>
    /// Whether a file is there. On another computer the check waits at most <see cref="NetworkLimit"/>, and a share that didn't
    /// answer in time counts as not having it, without waiting again, for the rest of the process.
    /// </summary>
    public static bool FileExists(string path) => Exists(path, File.Exists, Directory.Exists, NetworkLimit);

    /// <summary><see cref="FileExists"/> with its checks and bound given: <paramref name="shareExists"/> asks whether a share answers.</summary>
    public static bool Exists(string path, Func<string, bool> exists, Func<string, bool> shareExists, TimeSpan limit)
    {
        if (!IsNetworkFolder(path))
        {
            return Safely(exists, path);
        }

        Share share = Shares.GetOrAdd(ShareOf(path), s => new Lazy<Share>(() => new Share(OnItsOwnThread(() => Safely(shareExists, s))))).Value;
        if (share.Unanswered || !share.Answered(share.Answer, limit) || !share.Answer.Result)
        {
            return false;
        }

        Task<bool> check = OnItsOwnThread(() => Safely(exists, path));
        return share.Answered(check, limit) && check.Result;
    }

    /// <summary>The share a network path is on: <c>\\server\share</c> (for <c>\\?\UNC\…</c> too), or a mapped drive's root.</summary>
    public static string ShareOf(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }

        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path[..2] + @"\";
        }

        string[] parts = path[2..].Split(new[] { '\\', '/' }, 3);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : $@"\\{parts[0]}";
    }

    /// <summary>A network check blocks; on a thread of its own, a busy thread pool can't hold it past its bound.</summary>
    private static Task<bool> OnItsOwnThread(Func<bool> check) =>
        Task.Factory.StartNew(check, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static bool Safely(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class Share(Task<bool> answer)
    {
        private volatile bool _unanswered;

        public Task<bool> Answer { get; } = answer;

        public bool Unanswered => _unanswered;

        /// <summary>Whether <paramref name="check"/> finished within <paramref name="limit"/>; a wait that ran out marks the share.</summary>
        public bool Answered(Task<bool> check, TimeSpan limit)
        {
            if (check.Wait(limit))
            {
                return true;
            }

            _unanswered = true;
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
                if (FileExists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            foreach (string ext in extensions)
            {
                string withExt = candidate + ext;
                if (FileExists(withExt))
                {
                    return withExt;
                }
            }

            // Fall back to the bare (extensionless) name in case it is directly executable.
            if (FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
