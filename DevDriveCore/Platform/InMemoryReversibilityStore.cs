using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// In-memory <see cref="IReversibilityStore"/>. Serves as the core's default (non-persistent)
/// implementation and as a convenient fake for unit tests. Entries live only for the process
/// lifetime; use <see cref="JsonFileReversibilityStore"/> for cross-session persistence.
/// </summary>
public sealed class InMemoryReversibilityStore : IReversibilityStore
{
    private readonly Dictionary<string, ReversibilityEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Save(ReversibilityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(entry.Id);
        _entries[entry.Id] = entry;
    }

    /// <inheritdoc />
    public ReversibilityEntry? TryGet(string id) =>
        id is not null && _entries.TryGetValue(id, out ReversibilityEntry? entry) ? entry : null;

    /// <inheritdoc />
    public IReadOnlyList<ReversibilityEntry> GetAll() => _entries.Values.ToList();

    /// <inheritdoc />
    public bool Remove(string id) => id is not null && _entries.Remove(id);
}
