using System.Text.Json;

namespace DevDriveReclaim.Providers.Models;

/// <summary>A model the scan found, before it's a candidate.</summary>
/// <param name="Tool">Its manager's id (<see cref="LocalModelTools"/>).</param>
/// <param name="Title">Its manager's name.</param>
/// <param name="Store">The folder its manager keeps models in.</param>
/// <param name="Path">The candidate's path: what identifies it on disk (a manifest, a folder or a file).</param>
/// <param name="Names">Every name its manager knows it by, the main one first.</param>
/// <param name="Bytes">What removing it gives back, counting nothing another model still uses.</param>
/// <param name="SharedBytes">Files it shares with another model, which removing it doesn't free.</param>
/// <param name="Extra">One more fact for its row ("2 revisions"), or null.</param>
/// <param name="RecoveryHint">How to get it back.</param>
internal sealed record FoundModel(
    string Tool,
    string Title,
    string Store,
    string Path,
    IReadOnlyList<string> Names,
    long Bytes,
    long SharedBytes,
    string? Extra,
    string RecoveryHint);

/// <summary>
/// Ollama's model folder (<c>OLLAMA_MODELS</c>): manifests name models and list the blobs they're made of, and blobs are
/// content-addressed, so models share them.
/// </summary>
/// <remarks>
/// The layout is Ollama's own (ollama/ollama manifest/paths.go): <c>manifests-v2\&lt;host&gt;\&lt;namespace&gt;\&lt;model&gt;\&lt;tag&gt;</c>
/// (Ollama 0.40 writes these, with the host as <c>ollama.com</c>), the older <c>manifests\…</c> tree with the host as
/// <c>registry.ollama.ai</c>, which Ollama still reads and still writes for a pull, and <c>blobs\sha256-&lt;hex&gt;</c> for a
/// digest <c>sha256:&lt;hex&gt;</c>. There's no index: the manifests are the list. A name in both trees is one model.
/// <para>
/// Models that share their weights are one row: <c>ollama cp</c> makes a second name for the same blobs, and a model made
/// with <c>ollama create</c> from another one shares its weights. Removing only one of those names frees nothing, so the row
/// is every name that shares a large blob with another, and its size is the blobs only those names use. <c>ollama rm</c>
/// removes a name and then every blob no other manifest references (manifest.RemoveNamed), which is exactly that.
/// </para>
/// </remarks>
internal static class OllamaStore
{
    /// <summary>One named manifest.</summary>
    internal sealed record Entry(string Name, string Manifest, DateTime WrittenUtc, IReadOnlySet<string> Digests);

    /// <summary>The models in <paramref name="store"/>, a row per set of names that share their weights.</summary>
    /// <param name="unreadable">Told about each manifest that couldn't be read or parsed.</param>
    public static List<FoundModel> Read(string store, Action<string> unreadable, CancellationToken cancellationToken)
    {
        List<Entry> entries = ReadManifests(store, unreadable, cancellationToken);
        if (entries.Count == 0)
        {
            return [];
        }

        Dictionary<string, long?> sizes = BlobSizes(store, entries);
        List<List<Entry>> groups = Group(entries, sizes);

        // How many groups use each blob: a blob two groups use is freed by neither alone.
        var users = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (List<Entry> group in groups)
        {
            foreach (string digest in group.SelectMany(e => e.Digests).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                users[digest] = users.GetValueOrDefault(digest) + 1;
            }
        }

        List<FoundModel> found = [];
        foreach (List<Entry> group in groups)
        {
            long bytes = 0, shared = 0;
            foreach (string digest in group.SelectMany(e => e.Digests).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (sizes[digest] is not long size)
                {
                    continue;
                }

                // A blob with another hard link (into LM Studio's folder, say) isn't freed by ollama rm either.
                if (users[digest] == 1 && !(size >= ModelFiles.LargeFileBytes && ModelFiles.LinkCount(BlobPath(store, digest)) > 1))
                {
                    bytes += size;
                }
                else
                {
                    shared += size;
                }
            }

            List<Entry> ordered = Ordered(group);
            found.Add(new FoundModel(
                LocalModelTools.Ollama,
                LocalModelTools.TitleOf(LocalModelTools.Ollama),
                store,
                ordered[0].Manifest,
                [.. ordered.Select(e => e.Name)],
                bytes,
                shared,
                null,
                LocalModelReclaimProvider.Recovery(LocalModelTools.Ollama, string.Empty, ordered[0].Name)));
        }

        return found;
    }

    /// <summary>
    /// The names that share their weights with <paramref name="name"/> now, the way <see cref="Read"/> groups them, or null when
    /// Ollama no longer has <paramref name="name"/>.
    /// </summary>
    public static IReadOnlyList<string>? NamesWith(string store, string name) =>
        GroupWith(store, name) is { } group ? [.. group.Select(e => e.Name)] : null;

    /// <summary>The manifests of the names that share their weights with <paramref name="name"/> now, or null.</summary>
    internal static IReadOnlyList<Entry>? GroupWith(string store, string name)
    {
        List<Entry> entries = ReadManifests(store, _ => { }, CancellationToken.None);
        List<List<Entry>> groups = Group(entries, BlobSizes(store, entries));
        return groups.FirstOrDefault(g => g.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))) is { } group
            ? Ordered(group)
            : null;
    }

    /// <summary>The digest Ollama gives a name (<c>/api/tags</c>): the SHA-256 of the manifest file, in lowercase hex.</summary>
    public static string ManifestDigest(string manifest)
    {
        using FileStream stream = File.OpenRead(manifest);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// The name Ollama shows for a manifest at <c>&lt;host&gt;\&lt;namespace&gt;\&lt;model&gt;\&lt;tag&gt;</c> (<c>ollama list</c>):
    /// <c>model:tag</c> for the library, <c>namespace/model:tag</c> for someone's model on ollama.com, and the host in front
    /// for another registry (<c>hf.co/org/repo:Q4_K_M</c>).
    /// </summary>
    public static string NameOf(string host, string space, string model, string tag)
    {
        host = Uri.UnescapeDataString(host);
        if (string.Equals(host, "registry.ollama.ai", StringComparison.OrdinalIgnoreCase))
        {
            host = "ollama.com";
        }

        model = Uri.UnescapeDataString(model);
        tag = Uri.UnescapeDataString(tag);
        space = Uri.UnescapeDataString(space);
        return string.Equals(host, "ollama.com", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(space, "library", StringComparison.OrdinalIgnoreCase) ? $"{model}:{tag}" : $"{space}/{model}:{tag}"
            : $"{host}/{space}/{model}:{tag}";
    }

    /// <summary>The blob file for a digest: <c>sha256:abc</c> is <c>blobs\sha256-abc</c>.</summary>
    public static string BlobPath(string store, string digest) => Path.Combine(store, "blobs", digest.Replace(':', '-'));

    /// <summary>Every named manifest, the v2 tree first, one entry per name.</summary>
    internal static List<Entry> ReadManifests(string store, Action<string> unreadable, CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (string tree in (string[])["manifests-v2", "manifests"])
        {
            string root = Path.Combine(store, tree);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string host in Directory.EnumerateDirectories(root))
            {
                foreach (string space in Directory.EnumerateDirectories(host))
                {
                    foreach (string model in Directory.EnumerateDirectories(space))
                    {
                        foreach (string tag in Directory.EnumerateFiles(model))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string name = NameOf(Path.GetFileName(host), Path.GetFileName(space), Path.GetFileName(model), Path.GetFileName(tag));
                            if (byName.ContainsKey(name))
                            {
                                continue;
                            }

                            try
                            {
                                IReadOnlySet<string> digests = Digests(File.ReadAllText(tag));
                                if (digests.Count == 0)
                                {
                                    unreadable(tag);
                                    continue;
                                }

                                byName[name] = new Entry(name, tag, File.GetLastWriteTimeUtc(tag), digests);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                            {
                                unreadable(tag);
                            }
                        }
                    }
                }
            }
        }

        return [.. byName.Values];
    }

    /// <summary>Every blob digest a manifest references: its config, its layers, and those of any manifests it lists.</summary>
    internal static IReadOnlySet<string> Digests(string json)
    {
        var digests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json);
        Collect(document.RootElement);
        return digests;

        void Collect(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (element.TryGetProperty("config", out JsonElement config))
            {
                Add(config);
            }

            foreach (string list in (string[])["layers", "manifests"])
            {
                if (element.TryGetProperty(list, out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        Add(item);
                        Collect(item);
                    }
                }
            }
        }

        void Add(JsonElement layer)
        {
            if (layer.ValueKind == JsonValueKind.Object &&
                layer.TryGetProperty("digest", out JsonElement digest) &&
                digest.GetString() is { } value &&
                value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                value.Length == 71)
            {
                digests.Add(value);
            }
        }
    }

    /// <summary>Each referenced blob's size on disk, or null when it's missing (a broken model frees nothing for it).</summary>
    private static Dictionary<string, long?> BlobSizes(string store, IEnumerable<Entry> entries)
    {
        var sizes = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (string digest in entries.SelectMany(e => e.Digests).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var blob = new FileInfo(BlobPath(store, digest));
            sizes[digest] = blob.Exists ? ModelFiles.Allocated(blob.FullName, blob.Length) : null;
        }

        return sizes;
    }

    /// <summary>Joins names that share any large blob (weights, an adapter, a projector); small ones (a license, a template) don't join.</summary>
    private static List<List<Entry>> Group(List<Entry> entries, Dictionary<string, long?> sizes)
    {
        int[] parent = [.. Enumerable.Range(0, entries.Count)];
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var firstUser = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            foreach (string digest in entries[i].Digests.Where(d => sizes.GetValueOrDefault(d) >= ModelFiles.LargeFileBytes))
            {
                if (firstUser.TryGetValue(digest, out int other))
                {
                    parent[Find(i)] = Find(other);
                }
                else
                {
                    firstUser[digest] = i;
                }
            }
        }

        return [.. Enumerable.Range(0, entries.Count).GroupBy(Find).Select(g => g.Select(i => entries[i]).ToList())];
    }

    /// <summary>The name pulled first leads (the others were copied or made from it), then the rest A to Z.</summary>
    private static List<Entry> Ordered(IEnumerable<Entry> group)
    {
        List<Entry> all = [.. group.OrderBy(e => e.WrittenUtc).ThenBy(e => e.Name, StringComparer.Ordinal)];
        return [all[0], .. all.Skip(1).OrderBy(e => e.Name, StringComparer.Ordinal)];
    }
}
