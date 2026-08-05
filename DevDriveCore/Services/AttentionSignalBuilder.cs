using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>What one package cache looks like to the signal builder.</summary>
/// <param name="Ecosystem">Display name — "pip", "npm".</param>
/// <param name="Location">Where it currently lives.</param>
/// <param name="SizeBytes">How big it is, or zero when it has not been measured.</param>
/// <param name="IsRedirected">True when an environment variable already points it somewhere.</param>
public sealed record CacheSignalInput(string Ecosystem, string Location, long SizeBytes, bool IsRedirected);

/// <summary>Everything the Overview room knows about the machine, in one shape.</summary>
/// <remarks>
/// A single input record rather than eight parameters, because the caller assembles this from four
/// different view models and a positional argument list is how the reclaim total ends up in the
/// cache count's slot.
/// </remarks>
public sealed record AttentionInputs
{
    /// <summary>Every fixed volume.</summary>
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];

    /// <summary>True once a reclaim scan has finished at least once this session.</summary>
    public bool HasReclaimScan { get; init; }

    /// <summary>Total bytes the reclaim scan found.</summary>
    public long ReclaimableBytes { get; init; }

    /// <summary>Of that, how much carries no risk.</summary>
    public long ReclaimSafeBytes { get; init; }

    /// <summary>How many categories found something.</summary>
    public int ReclaimCategoryCount { get; init; }

    /// <summary>Caches that exist on this PC but are not on a Dev Drive.</summary>
    public IReadOnlyList<CacheSignalInput> CachesOffDevDrive { get; init; } = [];

    /// <summary>How many caches are already on a Dev Drive.</summary>
    public int CachesOnDevDrive { get; init; }

    /// <summary>True once a space scan has produced a snapshot.</summary>
    public bool HasSpaceScan { get; init; }

    /// <summary>
    /// Below this fraction free, a volume is called out. Defaults to
    /// <see cref="AttentionSignalBuilder.LowFreeFraction"/>; Settings can move it.
    /// </summary>
    public double LowFreeFraction { get; init; } = AttentionSignalBuilder.LowFreeFraction;

    /// <summary>
    /// How many caches can sit off a Dev Drive before they are reported as one signal instead of one
    /// each. Zero means never roll up. Defaults to
    /// <see cref="AttentionSignalBuilder.DefaultCacheRollupThreshold"/>.
    /// </summary>
    public int CacheRollupThreshold { get; init; } = AttentionSignalBuilder.DefaultCacheRollupThreshold;

    /// <summary>Whether cache placement is reported at all.</summary>
    public bool WatchCachesOffDevDrive { get; init; } = true;
}

/// <summary>
/// Ranks what this machine needs done.
/// </summary>
/// <remarks>
/// <para>
/// The order is the room's whole argument, so it is stated here rather than emerging from whatever
/// order the callers happen to run in: a volume already below its free-space floor comes first,
/// then everything that returns bytes, largest first, then everything else.
/// </para>
/// <para>
/// The reason urgency outranks size is that the two are not the same currency. A volume at 4% free
/// will fail a build tonight; 40 GB of stale package cache on a half-empty drive will not. Sorting
/// both by bytes would bury the one that has a deadline.
/// </para>
/// </remarks>
public static class AttentionSignalBuilder
{
    /// <summary>
    /// Below this fraction free, a volume is called out by default. Matches the headroom Windows
    /// itself wants for servicing, and is roughly where NTFS fragmentation and ReFS allocation both
    /// start to bite. Settings can move it; see <see cref="AttentionInputs.LowFreeFraction"/>.
    /// </summary>
    public const double LowFreeFraction = 0.15d;

    /// <summary>
    /// How many caches can sit off the Dev Drive by default before they are reported as one signal
    /// instead of one row each.
    /// </summary>
    /// <remarks>
    /// Above this, the per-ecosystem rows say the same sentence in different words and crowd out
    /// findings that are not about caches at all. This room ranks; the Caches room enumerates.
    /// </remarks>
    public const int DefaultCacheRollupThreshold = 3;

    /// <summary>Builds the ranked signal list.</summary>
    public static IReadOnlyList<AttentionSignal> Build(AttentionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        List<AttentionSignal> signals = [];
        signals.AddRange(LowSpaceSignals(inputs));
        AttentionSignal? reclaim = ReclaimSignal(inputs);
        if (reclaim is not null)
        {
            signals.Add(reclaim);
        }

        signals.AddRange(CacheSignals(inputs));
        AttentionSignal? noDevDrive = NoDevDriveSignal(inputs);
        if (noDevDrive is not null)
        {
            signals.Add(noDevDrive);
        }

        AttentionSignal? notScanned = NotScannedSignal(inputs);
        if (notScanned is not null)
        {
            signals.Add(notScanned);
        }

        return [.. signals
            .OrderByDescending(s => s.IsUrgent)
            .ThenBy(s => s.IsAdvisory)
            .ThenByDescending(s => s.ImpactBytes)
            .ThenBy(s => s.Kind)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<AttentionSignal> LowSpaceSignals(AttentionInputs inputs)
    {
        foreach (VolumeInfo volume in inputs.Volumes.Where(v => v.SizeBytes > 0))
        {
            double free = 1d - volume.UsedFraction;
            if (free >= inputs.LowFreeFraction)
            {
                continue;
            }

            string name = Describe(volume);
            yield return new AttentionSignal
            {
                Id = $"LowSpace_{Slug(name)}",
                Title = $"{name} is {volume.UsedFraction * 100:0}% full",
                Detail = $"{ByteSizeFormatter.Format(volume.FreeBytes)} free — below the "
                    + $"{inputs.LowFreeFraction * 100:0}% Windows wants to keep",
                Where = name,
                Impact = $"{free * 100:0}% free",
                Kind = free < inputs.LowFreeFraction / 2d ? SignalKind.Bad : SignalKind.Warn,
                RoomTag = "reclaim",
                RoomLabel = "Reclaim",
                IsUrgent = true,
            };
        }
    }

    private static AttentionSignal? ReclaimSignal(AttentionInputs inputs)
    {
        if (!inputs.HasReclaimScan || inputs.ReclaimableBytes <= 0)
        {
            return null;
        }

        string safe = inputs.ReclaimSafeBytes > 0
            ? $"{inputs.ReclaimCategoryCount} categories, "
                + $"{ByteSizeFormatter.Format((ulong)inputs.ReclaimSafeBytes)} of it zero-risk"
            : $"{inputs.ReclaimCategoryCount} categories, none of it zero-risk";

        return new AttentionSignal
        {
            Id = "Reclaimable",
            Title = $"{ByteSizeFormatter.Format((ulong)inputs.ReclaimableBytes)} can be reclaimed",
            Detail = safe,
            Where = VolumeList(inputs.Volumes),
            Impact = $"+{ByteSizeFormatter.Format((ulong)inputs.ReclaimableBytes)}",
            ImpactBytes = inputs.ReclaimableBytes,
            Kind = SignalKind.Gain,
            RoomTag = "reclaim",
            RoomLabel = "Reclaim",
        };
    }

    /// <summary>
    /// How many caches can sit off the Dev Drive before they are reported as one signal instead of
    /// one row each.
    /// </summary>
    /// <remarks>
    /// Above this, the per-ecosystem rows say the same sentence in different words and crowd out
    /// findings that are not about caches at all. This room ranks; the Caches room enumerates.
    /// </remarks>
    private static IEnumerable<AttentionSignal> CacheSignals(AttentionInputs inputs)
    {
        if (!inputs.WatchCachesOffDevDrive)
        {
            yield break;
        }

        // Only worth raising once a Dev Drive exists to move them to. Without one the actionable
        // signal is "there is no Dev Drive", and saying both is the same advice twice.
        if (!inputs.Volumes.Any(v => v.IsDevDrive))
        {
            yield break;
        }

        IReadOnlyList<CacheSignalInput> off = inputs.CachesOffDevDrive;
        int moved = inputs.CachesOnDevDrive;
        int total = moved + off.Count;

        if (inputs.CacheRollupThreshold > 0 && off.Count >= inputs.CacheRollupThreshold)
        {
            long bytes = off.Sum(c => c.SizeBytes);
            yield return new AttentionSignal
            {
                Id = "CachesOffDrive",
                Title = $"{off.Count} package caches are not on a Dev Drive",
                Detail = $"{string.Join(", ", off.OrderBy(c => c.Ecosystem, StringComparer.OrdinalIgnoreCase).Select(c => c.Ecosystem))}"
                    + $" — {moved} of {total} already moved",
                Where = RootList(off),
                Impact = bytes > 0 ? ByteSizeFormatter.Format((ulong)bytes) : "not measured",
                ImpactBytes = bytes,
                Kind = SignalKind.Info,
                RoomTag = "caches",
                RoomLabel = "Caches",
            };

            yield break;
        }

        foreach (CacheSignalInput cache in off.OrderByDescending(c => c.SizeBytes))
        {
            string detail = cache.IsRedirected
                ? $"Redirected, but not to a Dev Drive — {moved} of {total} already moved"
                : $"Still where it was installed — {moved} of {total} already moved";

            yield return new AttentionSignal
            {
                Id = $"CacheOffDrive_{Slug(cache.Ecosystem)}",
                Title = $"{cache.Ecosystem} cache is not on a Dev Drive",
                Detail = detail,
                Where = cache.Location,
                Impact = cache.SizeBytes > 0
                    ? ByteSizeFormatter.Format((ulong)cache.SizeBytes)
                    : "not measured",
                ImpactBytes = cache.SizeBytes,
                Kind = SignalKind.Info,
                RoomTag = "caches",
                RoomLabel = "Caches",
            };
        }
    }

    /// <summary>
    /// The distinct drive roots a set of caches sit on. The rolled-up row cannot name one path, and
    /// the roots are what the reader actually needs — which disk is carrying them.
    /// </summary>
    private static string RootList(IReadOnlyList<CacheSignalInput> caches)
    {
        string[] roots = [.. caches
            .Select(c => c.Location.Length >= 2 && c.Location[1] == ':'
                ? c.Location[..2].ToUpperInvariant()
                : string.Empty)
            .Where(root => root.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

        return roots.Length == 0 ? "this PC" : string.Join(" and ", roots);
    }

    private static AttentionSignal? NoDevDriveSignal(AttentionInputs inputs)
    {
        if (inputs.Volumes.Count == 0 || inputs.Volumes.Any(v => v.IsDevDrive))
        {
            return null;
        }

        return new AttentionSignal
        {
            Id = "NoDevDrive",
            Title = "There is no Dev Drive on this PC",
            Detail = "Source and package caches are all on volumes the antivirus filter scans inline",
            Where = VolumeList(inputs.Volumes),
            Impact = "not set up",
            Kind = SignalKind.Warn,
            RoomTag = "create",
            RoomLabel = "Create",
        };
    }

    private static AttentionSignal? NotScannedSignal(AttentionInputs inputs)
    {
        if (inputs.HasSpaceScan)
        {
            return null;
        }

        return new AttentionSignal
        {
            Id = "NoSpaceScan",
            Title = "Nothing has been scanned yet",
            Detail = "Until a scan runs, every figure here comes from the volume table alone",
            Where = VolumeList(inputs.Volumes),
            Impact = "no scan",
            Kind = SignalKind.Info,
            RoomTag = "space",
            RoomLabel = "Space",
            IsAdvisory = true,
        };
    }

    private static string Describe(VolumeInfo volume) => volume.DriveLetter is char letter
        ? $"{letter}:"
        : volume.Label.Length > 0 ? volume.Label : "Unnamed volume";

    private static string VolumeList(IReadOnlyList<VolumeInfo> volumes)
    {
        string[] named = [.. volumes
            .Where(v => v.DriveLetter is not null)
            .Select(v => $"{v.DriveLetter}:")];

        return named.Length switch
        {
            0 => "this PC",
            1 => named[0],
            2 => $"{named[0]} and {named[1]}",
            _ => string.Join(", ", named[..^1]) + $" and {named[^1]}",
        };
    }

    private static string Slug(string value) =>
        new([.. value.Where(char.IsLetterOrDigit)]);
}
