using DevDriveCore.Services;
using DevDriveStorage.Live;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Measures each package manager's download cache using the catalogue the manager already ships.
/// </summary>
/// <remarks>
/// Reuses <see cref="PackageCacheCatalog"/> rather than defining a second list of ecosystems. Two
/// lists would drift, and the failure mode is nasty in both directions: a cache the Caches room can
/// relocate but Reclaim cannot see, or a cache Reclaim offers to delete that the rest of the app has
/// never heard of.
/// <para>
/// The environment variable is honoured before the default path, so a cache the user already moved
/// to their Dev Drive is measured where it actually lives — reporting the stale default would claim
/// space on the wrong volume, which is precisely the mistake the before/after bars exist to prevent.
/// </para>
/// <para>
/// Every entry is <see cref="ReclaimRisk.Safe"/>: a package cache is a download cache by
/// definition. The cost is re-downloading, which the recovery hint states plainly rather than
/// pretending deletion is free.
/// </para>
/// </remarks>
public sealed class PackageCacheReclaimProvider : IReclaimProvider
{
    public static readonly ReclaimCategory ReclaimCategory = new(
        "package-caches",
        "Package caches",
        "Downloaded packages your tools keep so they don't fetch them twice. Deleting costs a re-download, nothing else.",
        "\uE896",
        order: 2);

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.Run<IReadOnlyList<ReclaimCandidate>>(() =>
        {
            var candidates = new List<ReclaimCandidate>();

            foreach (PackageCacheDefinition definition in PackageCacheCatalog.Default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReclaimScanProgress(
                    ReclaimCategory.Id, $"Measuring {definition.Name}", candidates.Count));

                string? path = ResolvePath(definition);
                if (path is null || !Directory.Exists(path))
                {
                    continue;
                }

                DirectoryMeasurement measurement = DirectoryMeasurer.Measure(path, cancellationToken);
                if (measurement.AllocatedBytes <= 0)
                {
                    continue;
                }

                candidates.Add(new ReclaimCandidate(
                    ReclaimCategory.Id,
                    path,
                    definition.Name,
                    measurement.AllocatedBytes,
                    ReclaimRisk.Safe,
                    "A download cache. Everything in it can be fetched again from the same source it " +
                        "came from the first time.",
                    $"Nothing to restore — the next {definition.Name} restore re-downloads what it " +
                        "needs. Expect the first build afterwards to be slower.",
                    lastUsedUtc: measurement.NewestWriteUtc,
                    itemCount: measurement.FileCount,
                    detail: $"{measurement.FileCount:N0} files · {path}"));
            }

            return candidates;
        }, cancellationToken);
    }

    /// <summary>
    /// Where this cache actually is: the relocation environment variable wins over the default,
    /// because a moved cache measured at its old path is a number about a folder that isn't there.
    /// </summary>
    private static string? ResolvePath(PackageCacheDefinition definition)
    {
        string? configured = Environment.GetEnvironmentVariable(definition.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        try
        {
            return Environment.ExpandEnvironmentVariables(definition.DefaultPathTemplate);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
