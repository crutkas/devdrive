using DevDriveStorage.Live;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Finds compiler and package-manager output inside repositories — <c>bin</c>, <c>obj</c>,
/// <c>target</c>, <c>node_modules</c> and friends.
/// </summary>
/// <remarks>
/// This is almost always the largest category on a developer machine and it is also the safest,
/// which is an unusual and valuable combination: the bytes are large, and every one of them is
/// reproducible by a command the user already runs. That is why each candidate carries the literal
/// rebuild command as its recovery hint — the honest answer to "what does this cost me" is not
/// "nothing", it is "one <c>npm install</c>, about ninety seconds".
/// <para>
/// Output folders are attributed to their owning repository rather than listed as bare paths,
/// because <c>obj</c> means nothing on its own and "PowerToys · obj" is a decision a user can make.
/// </para>
/// </remarks>
public sealed class BuildOutputReclaimProvider : IReclaimProvider
{
    /// <summary>Folder name to the command that regenerates it. The map <em>is</em> the safety argument.</summary>
    private static readonly Dictionary<string, string> OutputFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bin"] = "dotnet build",
        ["obj"] = "dotnet build (restores on next build)",
        ["target"] = "cargo build",
        ["node_modules"] = "npm install",
        ["dist"] = "npm run build",
        ["build"] = "the project's build command",
        ["out"] = "the project's build command",
        ["__pycache__"] = "nothing — Python regenerates it on next import",
        ["CMakeFiles"] = "cmake",
        [".gradle"] = "gradle build",
    };

    public static readonly ReclaimCategory ReclaimCategory = new(
        "build-outputs",
        "Build outputs",
        "Compiler and package-manager output inside your repos. Large, and reproducible by a command you already run.",
        "\uE9F5",
        order: 1);

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.Run<IReadOnlyList<ReclaimCandidate>>(() =>
        {
            IReadOnlyList<DiscoveredRepository> repositories =
                context.Repositories(cancellationToken);

            var candidates = new List<ReclaimCandidate>();

            foreach (DiscoveredRepository repository in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReclaimScanProgress(
                    ReclaimCategory.Id, $"Scanning {repository.Name}", candidates.Count));

                foreach (string outputPath in FindOutputFolders(repository.Path, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DirectoryMeasurement measurement =
                        DirectoryMeasurer.Measure(outputPath, cancellationToken);
                    if (measurement.AllocatedBytes < context.MinimumCandidateBytes)
                    {
                        continue;
                    }

                    string folderName = Path.GetFileName(outputPath);
                    string rebuild = OutputFolders.TryGetValue(folderName, out string? command)
                        ? command
                        : "the project's build command";

                    candidates.Add(new ReclaimCandidate(
                        ReclaimCategory.Id,
                        outputPath,
                        $"{repository.Name} · {Relative(repository.Path, outputPath)}",
                        measurement.AllocatedBytes,
                        ReclaimRisk.Safe,
                        $"Generated output. Nothing here was written by hand — {rebuild} recreates it.",
                        $"Run {rebuild} in {repository.Name}.",
                        lastUsedUtc: measurement.NewestWriteUtc,
                        itemCount: measurement.FileCount,
                        detail: $"{measurement.FileCount:N0} files in {repository.Name}"));
                }
            }

            return candidates;
        }, cancellationToken);
    }

    /// <summary>
    /// Walks a repo looking for output folders, never descending into one it already matched — the
    /// <c>obj</c> inside a <c>node_modules</c> is already counted by the <c>node_modules</c> row,
    /// and reporting both would double-count the same bytes.
    /// </summary>
    private static IEnumerable<string> FindOutputFolders(
        string repositoryPath,
        CancellationToken cancellationToken,
        int maxDepth = 6)
    {
        var results = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((repositoryPath, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string current, int depth) = stack.Pop();

            foreach (string child in RepositoryWalker.SafeSubdirectories(current))
            {
                string name = Path.GetFileName(child);

                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (OutputFolders.ContainsKey(name))
                {
                    results.Add(child);
                    continue;
                }

                if (depth < maxDepth)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return results;
    }

    private static string Relative(string root, string path) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart('\\', '/')
            : path;
}
