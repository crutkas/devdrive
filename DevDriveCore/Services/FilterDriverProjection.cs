using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Turns the two filter name lists that <c>fsutil devdrv query</c> reports into the rows the Drives
/// room shows, in I/O-path order.
/// </summary>
/// <remarks>
/// <para>
/// The room's question is "what is allowed to sit in the I/O path", so the row set is every filter we
/// know about for this volume: the ones attached now, plus the ones policy would let attach. Listing
/// only the attached ones would hide the single most interesting row on a Dev Drive — the antivirus
/// filter that is <i>not</i> there, which is the whole reason the volume is fast.
/// </para>
/// <para>
/// Rows are ordered by altitude descending, because that is the order a write actually passes through
/// them: highest altitude is furthest from the filesystem and sees the I/O first. Filters whose
/// altitude we could not read sort last — they have to go somewhere, and the end of a list that is
/// explicitly in altitude order is the only honest place for "position unknown".
/// </para>
/// </remarks>
public static class FilterDriverProjection
{
    /// <summary>
    /// Projects attached and allowed filter names into ordered rows, reading each filter's altitude
    /// from its service registration.
    /// </summary>
    /// <param name="attached">Filters currently attached to the volume.</param>
    /// <param name="allowed">Filters policy permits to attach. Empty means "unknown / all".</param>
    /// <param name="altitudeReader">
    /// Altitude lookup by filter name. Defaults to the registry reader; tests pass a fake so they do
    /// not depend on which filters the test machine happens to have installed.
    /// </param>
    public static IReadOnlyList<FilterDriverInfo> Project(
        IReadOnlyList<string> attached,
        IReadOnlyList<string> allowed,
        Func<string, double?>? altitudeReader = null)
    {
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(allowed);

        Func<string, double?> readAltitude = altitudeReader ?? FilterAltitudeReader.Read;

        HashSet<string> attachedSet = new(attached, StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedSet = new(allowed, StringComparer.OrdinalIgnoreCase);

        List<FilterDriverInfo> rows = [];
        foreach (string name in Names(attached, allowed))
        {
            rows.Add(new FilterDriverInfo
            {
                Name = name,
                Altitude = readAltitude(name),
                IsAttached = attachedSet.Contains(name),
                IsAllowed = allowedSet.Contains(name),
                Description = FilterCatalog.Describe(name),
            });
        }

        // Unknown altitude sorts last, then by name so the order is stable rather than however the
        // two source lists happened to be concatenated.
        return rows
            .OrderBy(row => row.Altitude is null)
            .ThenByDescending(row => row.Altitude ?? 0d)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The union of both lists, de-duplicated case-insensitively but keeping the casing each filter
    /// was first reported with — <c>fsutil</c>'s casing is the filter's own registered name.
    /// </summary>
    private static IEnumerable<string> Names(IReadOnlyList<string> attached, IReadOnlyList<string> allowed)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in attached.Concat(allowed))
        {
            string trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                yield return trimmed;
            }
        }
    }
}
