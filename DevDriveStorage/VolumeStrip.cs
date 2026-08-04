using System.Collections.Immutable;

namespace DevDriveStorage;

/// <summary>Which colour family a volume's capacity bar should use.</summary>
/// <remarks>
/// A role rather than a colour, so the palette stays with the theme — including high contrast, where
/// the design's blues and teals are replaced wholesale by the user's system colours.
/// </remarks>
public enum VolumeAccentRole
{
    /// <summary>The volume Windows booted from.</summary>
    System,

    /// <summary>A Dev Drive. Called out because it is the volume this product exists to manage.</summary>
    DevDrive,

    /// <summary>Any other fixed volume.</summary>
    Other,
}

/// <summary>One volume as the context strip needs it.</summary>
/// <param name="Volume">The volume itself.</param>
/// <param name="IsSystemVolume">Whether Windows booted from it, which changes its caption.</param>
/// <param name="AccentRole">Which colour family the bar uses.</param>
/// <param name="ReclaimableBytes">Bytes the current reclaim selection would return here.</param>
public sealed record VolumeStripEntry(
    StorageVolume Volume,
    bool IsSystemVolume,
    VolumeAccentRole AccentRole,
    long ReclaimableBytes);

/// <summary>
/// Assembles the volume context strip shared by every storage room.
/// </summary>
/// <remarks>
/// This is deliberately UI-agnostic and lives beside <see cref="VolumeCapacity"/>: ordering, role
/// assignment, and the reclaim-to-volume match are all decisions that can be wrong, so they belong
/// somewhere they can be tested rather than in a page's code-behind.
/// </remarks>
public static class VolumeStrip
{
    /// <summary>Builds the strip.</summary>
    /// <param name="volumes">Fixed volumes, in any order.</param>
    /// <param name="systemRoot">
    /// Root of the volume Windows booted from, or null when it is unknown. Passed in rather than
    /// queried so the strip stays deterministic under test.
    /// </param>
    /// <param name="reclaimableByRoot">
    /// Bytes found per volume root. Keys are matched loosely — see <see cref="DriveKey"/> — because
    /// the reclaim subsystem and the volume enumerator produce root strings independently and have
    /// no contract about trailing slashes or case.
    /// </param>
    public static ImmutableArray<VolumeStripEntry> Build(
        IReadOnlyList<StorageVolume> volumes,
        string? systemRoot,
        IReadOnlyDictionary<string, long>? reclaimableByRoot = null)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        if (volumes.Count == 0)
        {
            return [];
        }

        Dictionary<string, long> byKey = new(StringComparer.OrdinalIgnoreCase);
        if (reclaimableByRoot is not null)
        {
            foreach ((string root, long bytes) in reclaimableByRoot)
            {
                string key = DriveKey(root);
                if (key.Length > 0)
                {
                    // Summed rather than overwritten: two spellings of the same root are the exact
                    // case this normalisation exists to handle, and taking the last one would
                    // silently discard half the finding.
                    byKey[key] = byKey.TryGetValue(key, out long existing) ? existing + bytes : bytes;
                }
            }
        }

        string? systemKey = systemRoot is null ? null : DriveKey(systemRoot);

        return
        [
            .. volumes
                .Select(volume =>
                {
                    string key = DriveKey(volume.RootPath);
                    bool isSystem = systemKey is not null && key.Equals(systemKey, StringComparison.OrdinalIgnoreCase);

                    return new VolumeStripEntry(
                        volume,
                        isSystem,
                        isSystem ? VolumeAccentRole.System
                            : volume.IsDevDrive ? VolumeAccentRole.DevDrive
                            : VolumeAccentRole.Other,
                        byKey.TryGetValue(key, out long bytes) ? bytes : 0);
                })
                // The system volume leads because it is the one the user cannot move off, so it is
                // the constraint every other decision in the room is measured against.
                .OrderByDescending(entry => entry.IsSystemVolume)
                .ThenBy(entry => entry.Volume.DriveLetter, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Reduces any spelling of a volume root to a comparable key: <c>"C:\"</c>, <c>"c:"</c>, and
    /// <c>"C:\\"</c> all become <c>"C"</c>.
    /// </summary>
    private static string DriveKey(string root) =>
        string.IsNullOrWhiteSpace(root) ? string.Empty : root.Trim().TrimEnd('\\', '/').TrimEnd(':');
}
