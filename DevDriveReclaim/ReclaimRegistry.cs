using DevDriveReclaim.Providers;
using DevDriveStorage;

namespace DevDriveReclaim;

/// <summary>
/// Assembles the built-in providers and works out what to scan.
/// </summary>
/// <remarks>
/// The registry is the seam a future subsystem plugs into: it hands back an
/// <see cref="IReclaimEngine"/>-shaped fan-out over whatever providers exist, so adding crash dumps
/// or Windows Update leftovers means adding one class and one line here — not touching the engine,
/// the ViewModel, or the room.
/// </remarks>
public static class ReclaimRegistry
{
    /// <summary>The providers that ship in the box, in rail order.</summary>
    public static IReadOnlyList<IReclaimProvider> CreateDefaultProviders() =>
    [
        new RecycleBinReclaimProvider(),
        new BuildOutputReclaimProvider(),
        new PackageCacheReclaimProvider(),
        new WorktreeReclaimProvider(),
        new DormantProjectReclaimProvider(),
        new DuplicateFileReclaimProvider(),
    ];

    public static ReclaimEngine CreateDefaultEngine() => new(CreateDefaultProviders());

    /// <summary>
    /// Builds a scan context from the machine: every fixed volume, and the folders that plausibly
    /// hold code.
    /// </summary>
    /// <remarks>
    /// Source roots are guessed rather than configured because a first run that demands setup before
    /// showing a single number is a first run most people abandon. The guesses are the conventional
    /// locations, plus the user's own profile; Settings can correct them, but the default has to
    /// produce an answer unaided.
    /// </remarks>
    public static ReclaimScanContext CreateMachineContext(
        IVolumeProvider? volumeProvider = null,
        IEnumerable<string>? sourceRootOverride = null)
    {
        IVolumeProvider provider = volumeProvider ?? new SystemVolumeProvider();

        var volumeRoots = new List<string>();
        try
        {
            volumeRoots.AddRange(provider.GetFixedVolumes().Select(v => v.RootPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            volumeRoots.Add(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
        }

        IReadOnlyList<string> sourceRoots = sourceRootOverride is not null
            ? [.. sourceRootOverride.Where(Directory.Exists)]
            : DiscoverSourceRoots(volumeRoots);

        return new ReclaimScanContext(volumeRoots, sourceRoots);
    }

    /// <summary>
    /// Conventional code locations that exist on this machine. Deduplicated by path, and never
    /// nested inside one another — scanning both <c>C:\src</c> and <c>C:\src\work</c> would count
    /// the same bytes twice and quietly double the headline number.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSourceRoots(IEnumerable<string> volumeRoots)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        foreach (string folder in (string[])["source", "src", "repos", "dev", "code", "git", "Projects"])
        {
            candidates.Add(Path.Combine(profile, folder));
        }

        foreach (string volume in volumeRoots)
        {
            foreach (string folder in (string[])["src", "source", "repos", "dev", "code", "git", "projects"])
            {
                candidates.Add(Path.Combine(volume, folder));
            }
        }

        var accepted = new List<string>();
        foreach (string candidate in candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (accepted.Any(a => IsUnderneath(candidate, a)))
            {
                continue;
            }

            accepted.RemoveAll(a => IsUnderneath(a, candidate));
            accepted.Add(candidate);
        }

        return accepted;
    }

    private static bool IsUnderneath(string path, string potentialParent)
    {
        string normalizedParent = potentialParent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
