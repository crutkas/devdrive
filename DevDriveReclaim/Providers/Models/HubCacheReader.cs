using System.Text;
using System.Text.RegularExpressions;

namespace DevDriveReclaim.Providers.Models;

/// <summary>
/// A Hugging Face hub cache (<c>HF_HUB_CACHE</c>): a <c>models--org--name</c> folder per repo, with its blobs, the snapshots
/// that point at them and its refs. The folders are the index; there's nothing else to keep in step.
/// </summary>
/// <remarks>
/// huggingface_hub 2.x also keeps a cache-wide store of shared files, <c>blobs\&lt;2 hex&gt;\&lt;hash&gt;</c>, when the cache's
/// <c>blobs\.huggingface-shared-blobs</c> marker says so: a repo's <c>blobs\&lt;etag&gt;</c> is then a symbolic link into it, and
/// <c>&lt;hash&gt;.refs</c> lists the links that use each stored file. Most of a model's bytes can be there, outside its own
/// folder. A stored file counts toward a repo only when every link that uses it is in that repo, which is when deleting the repo
/// frees it (<see cref="SharedBlobStore"/>, after huggingface_hub's <c>DeleteCacheStrategy.execute</c>).
/// <para>
/// Removing a repo is what <c>hf cache rm</c> does when every revision goes: the repo's folder, then each stored file nothing
/// else uses. Datasets and spaces aren't models, so they aren't read.
/// </para>
/// </remarks>
internal static class HubCacheReader
{
    /// <summary>The models in a hub cache, a row per repo.</summary>
    /// <param name="unreadable">Told about each repo folder that couldn't be read, and why.</param>
    public static List<FoundModel> Read(HubCache cache, Action<string, string> unreadable, CancellationToken cancellationToken)
    {
        string hub = cache.Path;
        List<FoundModel> found = [];
        foreach (string repo in Directory.EnumerateDirectories(hub, "models--*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(repo) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            try
            {
                Measured measured = Measure(hub, repo, cancellationToken);
                string id = RepoId(repo);
                int revisions = Directory.Exists(Path.Combine(repo, "snapshots")) ? Directory.EnumerateDirectories(Path.Combine(repo, "snapshots")).Count() : 0;
                found.Add(new FoundModel(
                    LocalModelTools.HuggingFace,
                    cache.Title,
                    hub,
                    repo,
                    [id],
                    measured.Bytes,
                    measured.SharedBytes,
                    revisions > 1 ? $"{revisions} revisions" : null,
                    LocalModelReclaimProvider.Recovery(LocalModelTools.HuggingFace, cache.Title, id)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable(repo, exception is UnauthorizedAccessException ? "access denied" : exception.Message);
            }
        }

        return found;
    }

    /// <summary>A repo's cost: its own files, and the stored files only it uses.</summary>
    internal sealed record Measured(long Bytes, long SharedBytes);

    /// <summary>Measures one repo folder in <paramref name="hub"/>.</summary>
    internal static Measured Measure(string hub, string repo, CancellationToken cancellationToken)
    {
        ModelFootprint own = ModelFiles.Measure(repo, cancellationToken);
        long bytes = own.Bytes, shared = own.SharedBytes;
        if (SharedBlobStore.IsMarked(hub))
        {
            foreach (string target in StoredTargets(hub, repo, own.Links))
            {
                long size = ModelFiles.Allocated(target, new FileInfo(target).Length);
                IReadOnlyList<string>? users = SharedBlobStore.References(hub, target);
                if (users is { Count: > 0 } && users.All(u => ReclaimOverlapResolver.Contains(repo, u)) && SharedBlobStore.Freed(target) > 0)
                {
                    bytes += size;
                }
                else if (users is { Count: > 0 })
                {
                    shared += size;
                }
            }
        }

        return new Measured(bytes, shared);
    }

    /// <summary>The stored files the repo's <c>blobs</c> links point at.</summary>
    internal static IReadOnlyList<string> StoredTargets(string hub, string repo, IEnumerable<(string Link, string Target)> links)
    {
        string blobs = Path.Combine(repo, "blobs");
        return [.. links
            .Where(l => string.Equals(Path.GetDirectoryName(l.Link), blobs, StringComparison.OrdinalIgnoreCase))
            .Select(l => SharedBlobStore.Target(hub, l.Link, l.Target))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The repo id a folder holds: <c>models--org--name</c> is <c>org/name</c> (huggingface_hub's repo_folder_name, reversed).</summary>
    internal static string RepoId(string repo) => Path.GetFileName(Path.TrimEndingDirectorySeparator(repo))["models--".Length..].Replace("--", "/", StringComparison.Ordinal);
}

/// <summary>
/// huggingface_hub's cache-wide store of shared files (utils/_shared_blobs.py), read and collected the same way: a stored file is
/// deleted only when none of the links its <c>.refs</c> list names still points at it, under the same per-file lock, and a list
/// that can't be read keeps the file.
/// </summary>
internal static partial class SharedBlobStore
{
    private const string MarkerName = ".huggingface-shared-blobs";

    /// <summary>
    /// The cache has a store huggingface_hub owns: <c>blobs\.huggingface-shared-blobs</c> holds layout 1. Python writes it in text
    /// mode, so on Windows it's <c>1\r\n</c>, and reads it back with universal newlines; this reads it the same way.
    /// </summary>
    public static bool IsMarked(string hub)
    {
        string marker = Path.Combine(hub, "blobs", MarkerName);
        try
        {
            return File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0 &&
                File.ReadAllText(marker).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') == "1\n";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The stored file a repo blob link points at, when it's one of the store's (<c>blobs\ab\abcd…</c>, a regular file), else null.
    /// </summary>
    public static string? Target(string hub, string link, string linkTarget)
    {
        try
        {
            string target = Path.GetFullPath(Path.IsPathRooted(linkTarget) ? linkTarget : Path.Combine(Path.GetDirectoryName(link)!, linkTarget));
            string store = Path.GetFullPath(Path.Combine(hub, "blobs"));
            if (!string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(target)), store, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string name = Path.GetFileName(target);
            string prefix = Path.GetFileName(Path.GetDirectoryName(target)!);
            return Hash().IsMatch(name) && string.Equals(prefix, name[..2], StringComparison.Ordinal) &&
                File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0
                ? target
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The repo blob links that still point at <paramref name="stored"/>: the lines of its <c>.refs</c> list that are a repo's
    /// <c>blobs\&lt;name&gt;</c> link and resolve to it. Null when the list is missing or can't be read, which means "keep it".
    /// </summary>
    public static IReadOnlyList<string>? References(string hub, string stored)
    {
        string list = stored + ".refs";
        string[] lines;
        try
        {
            if (!File.Exists(list))
            {
                return null;
            }

            lines = File.ReadAllLines(list);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var users = new List<string>();
        foreach (string line in lines)
        {
            string relative = line.Trim();
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Contains(".."))
            {
                continue;
            }

            string[] parts = relative.Split('/');
            if (parts.Length != 3 || !RepoFolder().IsMatch(parts[0]) || parts[1] != "blobs" || parts[2].Length == 0)
            {
                continue;
            }

            string link = Path.Combine(hub, parts[0], parts[1], parts[2]);
            try
            {
                var info = new FileInfo(link);
                if (!info.Exists || info.LinkTarget is not { } target)
                {
                    continue;
                }

                if (Target(hub, link, target) is { } resolved && string.Equals(resolved, Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase))
                {
                    users.Add(link);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return users;
    }

    /// <summary>
    /// True when deleting <paramref name="repo"/> frees <paramref name="stored"/>: every link its list names is in the repo.
    /// A list that can't be read keeps the file, as huggingface_hub does.
    /// </summary>
    public static bool OnlyFor(string hub, string stored, string repo) =>
        References(hub, stored) is { } users && users.All(u => ReclaimOverlapResolver.Contains(repo, u));

    /// <summary>Bytes deleting a stored file gives back: none when it has another hard link.</summary>
    public static long Freed(string stored)
    {
        var file = new FileInfo(stored);
        return !file.Exists || (file.Length >= ModelFiles.LargeFileBytes && ModelFiles.LinkCount(stored) > 1)
            ? 0
            : ModelFiles.Allocated(stored, file.Length);
    }

    /// <summary>
    /// Rewrites a stored file's list to the links that still use it, once a repo's links are gone; a list that can't be read
    /// is left as it is. Call it under <see cref="LockAll"/>.
    /// </summary>
    public static void Prune(string hub, string stored)
    {
        if (References(hub, stored) is not { } users)
        {
            return;
        }

        string list = stored + ".refs";
        string root = Path.GetFullPath(hub);
        string temporary = $"{list}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        File.WriteAllText(temporary, string.Concat(users
            .Select(u => Path.GetRelativePath(root, u).Replace('\\', '/') + "\n")
            .Order(StringComparer.Ordinal)), new UTF8Encoding(false));
        File.Move(temporary, list, overwrite: true);
    }

    /// <summary>
    /// Takes huggingface_hub's lock on each stored file (filelock's one-byte lock on <c>&lt;hash&gt;.lock</c>), in order, and
    /// holds them until disposed. Null when one can't be had within 10 seconds.
    /// </summary>
    public static IDisposable? LockAll(IEnumerable<string> stored)
    {
        var held = new HeldLocks();
        foreach (string file in stored.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Lock(file + ".lock") is not { } stream)
            {
                held.Dispose();
                return null;
            }

            held.Add(file + ".lock", stream);
        }

        return held;
    }

    /// <summary>
    /// One lock, waited for up to 10 seconds. It's opened without delete sharing: filelock deletes the file when it lets go,
    /// and that delete failing is what keeps a second holder from locking a new file of the same name.
    /// </summary>
    private static FileStream? Lock(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                stream.Lock(0, 1);
                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                stream.Dispose();
                return null;
            }
        }
    }

    /// <summary>Locks held together; letting go deletes each lock file, as filelock does, unless someone else has it open.</summary>
    private sealed class HeldLocks : IDisposable
    {
        private readonly List<(string Path, FileStream Stream)> _locks = [];

        public void Add(string path, FileStream stream) => _locks.Add((path, stream));

        public void Dispose()
        {
            foreach ((string path, FileStream stream) in _locks)
            {
                try
                {
                    stream.Unlock(0, 1);
                }
                catch (IOException)
                {
                    // Closing lets go of it too.
                }

                stream.Dispose();
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Another holder has it open; it's theirs to delete.
                }
            }

            _locks.Clear();
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hash();

    [GeneratedRegex("^(datasets|kernels|models|spaces)--.+$")]
    private static partial Regex RepoFolder();
}
