using System.Security.Cryptography;
using DevDriveStorage.Live;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Finds large files that exist in several places at once, keeping one copy and offering the rest.
/// </summary>
/// <remarks>
/// This category was not in the original design; it was found by scanning a real machine, where
/// 202.87 GB — 35% of a Dev Drive — turned out to be redundant copies: 617 copies of one 6.94 MB
/// header, 290 copies of a 25 MB SDK assembly, several 1.2 GB precompiled-header triples. Package
/// managers and build systems fan copies out per project, and nothing on the machine ever tells
/// you.
/// <para>
/// <b>Grouping is by size, then confirmed by content hash.</b> Same-name-same-size is a fast
/// shortlist but it is only a guess, and a delete tool that acts on a guess about identical content
/// is a data-loss tool. The hash runs only on the shortlist — files that already share an exact
/// byte length with another file — so the expensive step touches a tiny fraction of the disk.
/// </para>
/// <para>
/// Risk is <see cref="ReclaimRisk.Check"/>, never Safe. The copies are provably byte-identical, but
/// this provider cannot know whether some build expects a file at that exact path.
/// </para>
/// </remarks>
public sealed class DuplicateFileReclaimProvider : IReclaimProvider
{
    /// <summary>
    /// Files below this never justify their own row: the header of the table costs more attention
    /// than the space they return.
    /// </summary>
    private const long MinimumFileBytes = 4L * 1024 * 1024;

    /// <summary>Hashing every candidate in full is wasteful; a prefix separates distinct files almost as well.</summary>
    private const int HashPrefixBytes = 1024 * 1024;

    public static readonly ReclaimCategory ReclaimCategory = new(
        "duplicates",
        "Duplicate files",
        "Large files that exist byte-for-byte in more than one place. One copy is kept; the rest are listed.",
        "\uE8C8",
        order: 5);

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.Run<IReadOnlyList<ReclaimCandidate>>(() =>
        {
            progress?.Report(new ReclaimScanProgress(ReclaimCategory.Id, "Indexing large files", 0));

            // Pass 1 — index by exact size. Cheap, and files of different lengths cannot be equal.
            Dictionary<long, List<string>> bySize = IndexLargeFiles(context, cancellationToken);

            var candidates = new List<ReclaimCandidate>();
            int groupsChecked = 0;

            foreach ((long size, List<string> paths) in bySize.Where(kv => kv.Value.Count > 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                groupsChecked++;

                if (groupsChecked % 25 == 0)
                {
                    progress?.Report(new ReclaimScanProgress(
                        ReclaimCategory.Id, "Confirming duplicates by content", candidates.Count));
                }

                // Pass 2 — confirm by content. Only same-size files ever reach the hash.
                foreach (List<string> identical in GroupByContent(paths, cancellationToken))
                {
                    if (identical.Count < 2)
                    {
                        continue;
                    }

                    // Keep the shortest path: it is the likeliest original rather than a fan-out copy.
                    List<string> ordered = [.. identical.OrderBy(p => p.Length).ThenBy(p => p)];
                    string kept = ordered[0];

                    foreach (string duplicate in ordered.Skip(1))
                    {
                        candidates.Add(new ReclaimCandidate(
                            ReclaimCategory.Id,
                            duplicate,
                            Path.GetFileName(duplicate),
                            size,
                            ReclaimRisk.Check,
                            "This file is byte-for-byte identical to another copy on disk, confirmed " +
                                "by content hash rather than by name. One copy is being kept.",
                            $"Copy it back from {kept}, or re-run the build or restore that produced it.",
                            lastUsedUtc: SafeLastWrite(duplicate),
                            itemCount: 1,
                            detail: $"{ordered.Count:N0} identical copies · keeping {Shorten(kept)}"));
                    }
                }
            }

            progress?.Report(new ReclaimScanProgress(
                ReclaimCategory.Id, "Duplicate check complete", candidates.Count));

            return candidates;
        }, cancellationToken);
    }

    private static Dictionary<long, List<string>> IndexLargeFiles(
        ReclaimScanContext context, CancellationToken cancellationToken)
    {
        var bySize = new Dictionary<long, List<string>>();

        foreach (string root in context.SourceRoots.Where(Directory.Exists))
        {
            foreach ((string fullPath, FastDirEntry entry) in
                FastDirectoryWalker.EnumerateFiles(root, cancellationToken))
            {
                if (entry.ApparentBytes < MinimumFileBytes)
                {
                    continue;
                }

                // Keyed on apparent length, because two files of different logical size cannot be
                // equal. The reclaimed number reported later is the allocated size — that is what
                // actually comes back when the file goes away.
                if (!bySize.TryGetValue(entry.ApparentBytes, out List<string>? list))
                {
                    list = [];
                    bySize[entry.ApparentBytes] = list;
                }

                list.Add(fullPath);
            }
        }

        return bySize;
    }

    /// <summary>Splits same-size files into groups that are genuinely byte-identical.</summary>
    private static IEnumerable<List<string>> GroupByContent(
        List<string> paths, CancellationToken cancellationToken)
    {
        var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? hash = TryHashPrefix(path);
            if (hash is null)
            {
                continue;
            }

            if (!byHash.TryGetValue(hash, out List<string>? list))
            {
                list = [];
                byHash[hash] = list;
            }

            list.Add(path);
        }

        return byHash.Values;
    }

    private static string? TryHashPrefix(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[HashPrefixBytes];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or PathTooLongException)
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeLastWrite(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Shorten(string path)
    {
        string[] parts = path.Split('\\', '/');
        return parts.Length <= 3 ? path : string.Join('\\', parts[^3..]);
    }
}
