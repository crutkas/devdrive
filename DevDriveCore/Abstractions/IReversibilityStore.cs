using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Persists <see cref="ReversibilityEntry"/> records so a mutation performed by one of the app's
/// reversible engines can later be undone — even across process restarts. Entries are
/// JSON-serializable plain data.
/// </summary>
/// <remarks>
/// <para>Two implementations ship in the core: <see cref="DevDriveCore.Platform.InMemoryReversibilityStore"/>
/// (the non-persistent default, and a convenient test fake) and
/// <see cref="DevDriveCore.Platform.JsonFileReversibilityStore"/> (real persistence to a JSON file).</para>
/// <para>Saving an entry is purely app-local bookkeeping — it records what <em>was</em> changed so it
/// can be restored. It does not itself change the user's machine.</para>
/// </remarks>
public interface IReversibilityStore
{
    /// <summary>Inserts or replaces (by <see cref="ReversibilityEntry.Id"/>) a reversibility entry.</summary>
    void Save(ReversibilityEntry entry);

    /// <summary>Returns the entry with the given <paramref name="id"/>, or <c>null</c> when absent.</summary>
    ReversibilityEntry? TryGet(string id);

    /// <summary>Returns every stored entry (order unspecified).</summary>
    IReadOnlyList<ReversibilityEntry> GetAll();

    /// <summary>Removes the entry with the given <paramref name="id"/>. Returns <c>true</c> when one was removed.</summary>
    bool Remove(string id);
}
