using System.Collections;

namespace DevDriveStorage;

/// <summary>
/// Nodes of a <see cref="StorageSnapshot"/>, made as they are read. The snapshot keeps its nodes in a
/// table, so reading one creates it, and nothing holds on to it unless the caller does.
/// </summary>
public sealed class StorageNodeList : IReadOnlyList<StorageNode>
{
    private readonly StorageSnapshot _snapshot;
    private readonly List<int>? _indices;

    /// <param name="indices">The table indices to list, or <see langword="null"/> for the whole table in order.</param>
    internal StorageNodeList(StorageSnapshot snapshot, List<int>? indices)
    {
        _snapshot = snapshot;
        _indices = indices;
    }

    public int Count => _indices?.Count ?? _snapshot.Table.Count;

    /// <summary>The same as <see cref="Count"/>, for callers written against an array.</summary>
    public int Length => Count;

    public StorageNode this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _snapshot.NodeAt(_indices is null ? index : _indices[index]);
        }
    }

    public IEnumerator<StorageNode> GetEnumerator()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            yield return _snapshot.NodeAt(_indices is null ? i : _indices[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
