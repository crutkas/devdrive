using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DevDriveCore.Platform;

namespace DevDriveStorage;

[Flags]
internal enum NodeFlags : byte
{
    None = 0,
    Folder = 1 << 0,
    Measured = 1 << 1,
    Rollup = 1 << 2,

    /// <summary>The name is stored as UTF-16 rather than one byte per ASCII character.</summary>
    WideName = 1 << 3,

    /// <summary>The name is too long for the pool and is kept beside the table.</summary>
    LongName = 1 << 4,

    /// <summary>The path is not the parent's path joined with the name, so it is kept beside the table.</summary>
    OwnPath = 1 << 5,
    Logical = 1 << 6,
    Provider = 1 << 7,
}

/// <summary>A node's fixed-size facts. It holds no references, so the collector never looks inside an array of them.</summary>
internal struct NodeRecord
{
    public Guid Id;
    public long SizeBytes;
    public long ModifiedTicks;
    public int Parent;
    public int ItemCount;
    public int NameOffset;
    public ushort NameBytes;
    public NodeFlags Flags;

    public readonly bool IsFolder => (Flags & NodeFlags.Folder) != 0;

    public readonly bool Has(NodeFlags flag) => (Flags & flag) != 0;
}

internal interface INodeEntry
{
    int Node { get; }
}

internal readonly record struct LogicalEntry(int Node, long Bytes) : INodeEntry;

internal readonly record struct ExtraEntry(int Node, StorageProviderContext? Provider, string? Path, string? Name) : INodeEntry;

/// <summary>
/// The nodes of one <see cref="StorageSnapshot"/>, kept in a few large arrays instead of an object
/// per node. A full garbage collection marks every live object, so a snapshot of a large volume
/// held as millions of node objects made each collection cost seconds; held here it is a few dozen
/// arrays whose contents the collector skips, at a fraction of the bytes per node.
/// </summary>
/// <remarks>
/// <para>
/// A node is a <see cref="NodeRecord"/> addressed by its index. Its name lives in a byte pool, one
/// byte per character when it is ASCII and UTF-16 otherwise, and never spans two pool segments. Its
/// path is not stored: it is its parent's path joined with its name, rebuilt when asked for. The few
/// nodes whose path is something else (the root, the scanner's roll-ups) keep theirs beside the
/// table, as do the other rare facts: apparent size and provider context.
/// </para>
/// <para>
/// One thread appends. A node is written completely before the count that covers it is published,
/// so a reader may read any node below <see cref="Count"/> while the writer carries on.
/// <see cref="Seal"/> then indexes ids and children once, after which nothing changes.
/// </para>
/// </remarks>
internal sealed class NodeTable
{
    private const int RecordShift = 16;
    private const int NameShift = 20;
    private const int SideShift = 12;
    private const int MaxPooledNameBytes = 4096;
    private const int StackNameChars = 256;
    private const int ParallelOrderingThreshold = 65_536;

    private readonly Segmented<NodeRecord> _records;
    private readonly Segmented<byte> _names = new(NameShift);
    private readonly Segmented<LogicalEntry> _logical = new(SideShift);
    private readonly Segmented<ExtraEntry> _extras = new(SideShift);
    private int _count;
    private int _nameBytes;
    private int _logicalCount;
    private int _extraCount;

    private int _root = -1;
    private int[] _childStart = [];
    private int[] _children = [];
    private int[] _buckets = [];
    private int _bucketShift;

    /// <param name="capacity">Nodes to allocate room for up front, when the count is known.</param>
    public NodeTable(int capacity = 0) => _records = new Segmented<NodeRecord>(RecordShift, capacity);

    /// <summary>Nodes written so far. Every index below it can be read, even while the writer appends.</summary>
    public int Count => Volatile.Read(ref _count);

    public int RootIndex => _root;

    /// <summary>Bytes the table holds, not counting the few strings and provider records beside it.</summary>
    public long FootprintBytes =>
        _records.CapacityBytes + _names.CapacityBytes + _logical.CapacityBytes + _extras.CapacityBytes +
        (4L * (_childStart.Length + _children.Length + _buckets.Length));

    /// <summary>Arrays the table holds: what a full collection has to visit instead of one object per node.</summary>
    public int ArrayCount =>
        _records.ArrayCount + _names.ArrayCount + _logical.ArrayCount + _extras.ArrayCount + 3;

    public ref readonly NodeRecord this[int index] => ref _records[index];

    /// <summary>Copies a list of nodes into a table, checking that they describe one tree.</summary>
    public static NodeTable FromNodes(ImmutableArray<StorageNode> nodes, Guid rootId)
    {
        var positions = new Dictionary<Guid, int>(nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!positions.TryAdd(nodes[i].Id, i))
            {
                throw new StorageSnapshotValidationException($"Duplicate node ID: {nodes[i].Id}.");
            }
        }

        if (!positions.TryGetValue(rootId, out int rootIndex) ||
            nodes[rootIndex].ParentId is not null ||
            nodes[rootIndex].Kind != StorageNodeKind.Folder)
        {
            throw new StorageSnapshotValidationException("The root must be a folder with no parent.");
        }

        var parents = new int[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (i == rootIndex)
            {
                parents[i] = -1;
                continue;
            }

            StorageNode node = nodes[i];
            if (node.ParentId is not Guid parentId ||
                !positions.TryGetValue(parentId, out int parent) ||
                nodes[parent].Kind != StorageNodeKind.Folder)
            {
                throw new StorageSnapshotValidationException(
                    $"Node '{node.PhysicalPath}' must have an existing folder parent.");
            }

            parents[i] = parent;
        }

        var paths = new string[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            paths[i] = nodes[i].PhysicalPath;
        }

        var table = new NodeTable(nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            StorageNode node = nodes[i];
            int parent = parents[i];
            string? ownPath = parent >= 0 && !node.IsRollup && IsJoined(paths[i], paths[parent], node.Name)
                ? null
                : paths[i];
            table.Add(
                node.Id,
                parent,
                node.Name,
                node.Kind,
                node.SizeBytes,
                node.ItemCount,
                node.ModifiedAtUtc.UtcTicks,
                node.IsMeasured,
                node.LogicalBytes,
                node.Provider,
                ownPath);
        }

        table.Seal(rootIndex);
        return table;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is exactly what <see cref="Path.Combine(string, string)"/> makes of
    /// <paramref name="folder"/> and <paramref name="name"/>, so it can be rebuilt instead of stored.
    /// </summary>
    internal static bool IsJoined(string path, string folder, string name)
    {
        if (folder.Length == 0 || name.Length == 0 || Path.IsPathRooted(name))
        {
            return false;
        }

        bool separator = !IsSeparator(folder[^1]);
        return path.Length == folder.Length + (separator ? 1 : 0) + name.Length &&
            path.StartsWith(folder, StringComparison.Ordinal) &&
            (!separator || path[folder.Length] == '\\') &&
            path.EndsWith(name, StringComparison.Ordinal);
    }

    /// <summary>Makes room for <paramref name="count"/> nodes in one step, when the writer knows a batch is coming.</summary>
    public void Reserve(int count) => _records.EnsureCapacity(count);

    /// <summary>
    /// Appends a node and publishes it. <paramref name="ownPath"/> is <see langword="null"/> when the
    /// node's path is its parent's path joined with <paramref name="name"/>.
    /// </summary>
    public int Add(
        Guid id,
        int parent,
        string name,
        StorageNodeKind kind,
        long sizeBytes,
        int itemCount,
        long modifiedTicks,
        bool isMeasured,
        long? logicalBytes = null,
        StorageProviderContext? provider = null,
        string? ownPath = null)
    {
        if (_buckets.Length > 0)
        {
            throw new InvalidOperationException("A sealed node table cannot change.");
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A node ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(parent, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        if (kind == StorageNodeKind.File && itemCount != 0)
        {
            throw new ArgumentException("Files cannot contain child items.", nameof(itemCount));
        }

        if (logicalBytes is long logical)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(logical, nameof(logicalBytes));
        }

        if (ownPath is null)
        {
            if (parent < 0)
            {
                throw new ArgumentException("A node without a parent needs its own path.", nameof(ownPath));
            }

            // Path.Combine returns a rooted second part as it is.
            if (Path.IsPathRooted(name))
            {
                ownPath = name;
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownPath);
        }

        int index = _count;
        NodeFlags flags = (kind == StorageNodeKind.Folder ? NodeFlags.Folder : NodeFlags.None) |
            (isMeasured ? NodeFlags.Measured : NodeFlags.None);

        int nameOffset = 0;
        int nameBytes = 0;
        string? longName = null;
        if (name.Length <= MaxPooledNameBytes && Ascii.IsValid(name))
        {
            nameBytes = name.Length;
            nameOffset = ReserveName(nameBytes, alignment: 1);
            Ascii.FromUtf16(name, _names.Slice(nameOffset, nameBytes), out _);
        }
        else if (name.Length <= MaxPooledNameBytes / 2)
        {
            flags |= NodeFlags.WideName;
            nameBytes = name.Length * 2;
            nameOffset = ReserveName(nameBytes, alignment: 2);
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(_names.Slice(nameOffset, nameBytes));
        }
        else
        {
            flags |= NodeFlags.LongName;
            longName = name;
        }

        if (logicalBytes is long apparent)
        {
            flags |= NodeFlags.Logical;
            _logical.EnsureCapacity(_logicalCount + 1);
            _logical[_logicalCount] = new LogicalEntry(index, apparent);
            Volatile.Write(ref _logicalCount, _logicalCount + 1);
        }

        if (ownPath is not null)
        {
            flags |= NodeFlags.OwnPath;
            if (kind == StorageNodeKind.Folder && StorageNode.IsRollupPath(ownPath))
            {
                flags |= NodeFlags.Rollup;
            }
        }

        if (provider is not null)
        {
            flags |= NodeFlags.Provider;
        }

        if (provider is not null || ownPath is not null || longName is not null)
        {
            _extras.EnsureCapacity(_extraCount + 1);
            _extras[_extraCount] = new ExtraEntry(index, provider, ownPath, longName);
            Volatile.Write(ref _extraCount, _extraCount + 1);
        }

        _records.EnsureCapacity(index + 1);
        _records[index] = new NodeRecord
        {
            Id = id,
            SizeBytes = sizeBytes,
            ModifiedTicks = modifiedTicks,
            Parent = parent,
            ItemCount = itemCount,
            NameOffset = nameOffset,
            NameBytes = (ushort)nameBytes,
            Flags = flags,
        };
        Volatile.Write(ref _count, index + 1);
        return index;
    }

    /// <summary>
    /// Indexes ids and children and checks the tree: unique ids, a folder root with no parent, a
    /// folder parent for everything else and no two siblings with the same name. Children are kept
    /// in the order <see cref="StorageSnapshot.ChildrenOf"/> promises: folders first, then by name.
    /// </summary>
    public void Seal(int rootIndex)
    {
        int count = _count;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)rootIndex, (uint)count, nameof(rootIndex));
        _records.Trim(count);
        _names.Trim(_nameBytes);
        _logical.Trim(_logicalCount);
        _extras.Trim(_extraCount);
        BuildLookup(count);

        ref readonly NodeRecord root = ref this[rootIndex];
        if (root.Parent >= 0 || !root.IsFolder)
        {
            throw new StorageSnapshotValidationException("The root must be a folder with no parent.");
        }

        for (int i = 0; i < count; i++)
        {
            int parent = this[i].Parent;
            if (i != rootIndex && (parent < 0 || parent >= count || !this[parent].IsFolder))
            {
                string path = parent >= 0 && parent < count ? PathOf(i) : OwnPathOf(i) ?? NameOf(i);
                throw new StorageSnapshotValidationException($"Node '{path}' must have an existing folder parent.");
            }
        }

        BuildChildren(count);
        _root = rootIndex;
    }

    /// <summary>The index of the node with <paramref name="id"/>, or -1. Only after <see cref="Seal"/>.</summary>
    public int IndexOf(Guid id)
    {
        int[] buckets = _buckets;
        if (buckets.Length == 0)
        {
            return -1;
        }

        int mask = buckets.Length - 1;
        int slot = Slot(id, _bucketShift);
        while (true)
        {
            int entry = buckets[slot];
            if (entry == 0)
            {
                return -1;
            }

            if (this[entry - 1].Id == id)
            {
                return entry - 1;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Where <paramref name="index"/>'s children start in <see cref="ChildAt"/>.</summary>
    public int FirstChild(int index) => _childStart[index];

    /// <summary>Where <paramref name="index"/>'s children end in <see cref="ChildAt"/>, exclusive.</summary>
    public int ChildEnd(int index) => _childStart[index + 1];

    public int ChildAt(int position) => _children[position];

    public string NameOf(int index)
    {
        ref readonly NodeRecord record = ref this[index];
        if (record.Has(NodeFlags.LongName))
        {
            return ExtraOf(index).Name!;
        }

        ReadOnlySpan<byte> bytes = _names.Slice(record.NameOffset, record.NameBytes);
        return record.Has(NodeFlags.WideName)
            ? new string(MemoryMarshal.Cast<byte, char>(bytes))
            : Encoding.ASCII.GetString(bytes);
    }

    public long? LogicalOf(int index) =>
        this[index].Has(NodeFlags.Logical)
            ? _logical[Locate(_logical, Volatile.Read(ref _logicalCount), index)].Bytes
            : null;

    public StorageProviderContext? ProviderOf(int index) =>
        this[index].Has(NodeFlags.Provider) ? ExtraOf(index).Provider : null;

    public string? OwnPathOf(int index) =>
        this[index].Has(NodeFlags.OwnPath) ? ExtraOf(index).Path : null;

    /// <summary>The node's full path: the nearest ancestor that keeps its own path, joined with each name below it.</summary>
    public string PathOf(int index)
    {
        if (this[index].Has(NodeFlags.OwnPath))
        {
            return ExtraOf(index).Path!;
        }

        int[] chain = ArrayPool<int>.Shared.Rent(32);
        try
        {
            int depth = 0;
            int current = index;
            while (!this[current].Has(NodeFlags.OwnPath))
            {
                if (depth == chain.Length)
                {
                    int[] longer = ArrayPool<int>.Shared.Rent(depth * 2);
                    Array.Copy(chain, longer, depth);
                    ArrayPool<int>.Shared.Return(chain);
                    chain = longer;
                }

                if (depth > _count)
                {
                    throw new InvalidOperationException("A node's ancestry loops back on itself.");
                }

                chain[depth++] = current;
                current = this[current].Parent;
            }

            string basePath = ExtraOf(current).Path!;
            int length = basePath.Length;
            char last = basePath[^1];
            for (int k = depth - 1; k >= 0; k--)
            {
                length += (IsSeparator(last) ? 0 : 1) + NameLength(chain[k]);
                last = LastNameChar(chain[k]);
            }

            return string.Create(length, (Table: this, Chain: chain, Depth: depth, Base: basePath), static (span, state) =>
            {
                state.Base.CopyTo(span);
                int position = state.Base.Length;
                for (int k = state.Depth - 1; k >= 0; k--)
                {
                    if (!IsSeparator(span[position - 1]))
                    {
                        span[position++] = '\\';
                    }

                    position += state.Table.WriteName(state.Chain[k], span[position..]);
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(chain);
        }
    }

    /// <summary>A child's path given its parent's, without climbing the tree.</summary>
    public string ChildPath(int index, string parentPath) =>
        OwnPathOf(index) ?? Path.Combine(parentPath, NameOf(index));

    /// <summary>
    /// Compares two names the way <see cref="StringComparison.OrdinalIgnoreCase"/> compares strings,
    /// without making strings of them.
    /// </summary>
    public int CompareNames(int left, int right)
    {
        ref readonly NodeRecord a = ref this[left];
        ref readonly NodeRecord b = ref this[right];
        const NodeFlags notAscii = NodeFlags.WideName | NodeFlags.LongName;
        if (((a.Flags | b.Flags) & notAscii) == 0)
        {
            return CompareAsciiIgnoreCase(
                _names.Slice(a.NameOffset, a.NameBytes),
                _names.Slice(b.NameOffset, b.NameBytes));
        }

        int leftDecoded = a.Has(notAscii) ? 0 : a.NameBytes;
        int rightDecoded = b.Has(notAscii) ? 0 : b.NameBytes;
        char[]? rented = null;
        Span<char> scratch = leftDecoded + rightDecoded <= StackNameChars
            ? stackalloc char[StackNameChars]
            : (rented = ArrayPool<char>.Shared.Rent(leftDecoded + rightDecoded));
        int result = NameChars(left, scratch[..leftDecoded])
            .CompareTo(NameChars(right, scratch.Slice(leftDecoded, rightDecoded)), StringComparison.OrdinalIgnoreCase);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }

        return result;
    }

    /// <summary>The name's length in characters.</summary>
    public int NameLength(int index)
    {
        ref readonly NodeRecord record = ref this[index];
        return record.Has(NodeFlags.LongName) ? ExtraOf(index).Name!.Length
            : record.Has(NodeFlags.WideName) ? record.NameBytes / 2
            : record.NameBytes;
    }

    /// <summary>Writes the name into <paramref name="destination"/> and returns its length.</summary>
    public int WriteName(int index, Span<char> destination)
    {
        ref readonly NodeRecord record = ref this[index];
        if (record.Has(NodeFlags.LongName))
        {
            string name = ExtraOf(index).Name!;
            name.CopyTo(destination);
            return name.Length;
        }

        ReadOnlySpan<byte> bytes = _names.Slice(record.NameOffset, record.NameBytes);
        if (record.Has(NodeFlags.WideName))
        {
            MemoryMarshal.Cast<byte, char>(bytes).CopyTo(destination);
            return bytes.Length / 2;
        }

        Ascii.ToUtf16(bytes, destination, out int written);
        return written;
    }

    internal static bool IsSeparator(char c) => c is '\\' or '/';

    private static int CompareAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (int i = 0; i < length; i++)
        {
            int x = left[i];
            int y = right[i];
            if (x == y)
            {
                continue;
            }

            if ((uint)(x - 'a') <= 'z' - 'a')
            {
                x -= 'a' - 'A';
            }

            if ((uint)(y - 'a') <= 'z' - 'a')
            {
                y -= 'a' - 'A';
            }

            if (x != y)
            {
                return x - y;
            }
        }

        return left.Length - right.Length;
    }

    private static int Slot(Guid id, int shift)
    {
        ref ulong low = ref Unsafe.As<Guid, ulong>(ref id);
        ulong hash = low ^ BitOperations.RotateLeft(Unsafe.Add(ref low, 1), 32);
        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        hash *= 0xC4CEB9FE1A85EC53UL;
        hash ^= hash >> 33;
        return (int)(hash >> shift);
    }

    private static int Locate<TEntry>(Segmented<TEntry> entries, int count, int node)
        where TEntry : struct, INodeEntry
    {
        int low = 0;
        int high = count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int found = entries[middle].Node;
            if (found == node)
            {
                return middle;
            }

            if (found < node)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        throw new InvalidOperationException($"Node {node} has no entry beside the table.");
    }

    private ExtraEntry ExtraOf(int index) => _extras[Locate(_extras, Volatile.Read(ref _extraCount), index)];

    private char LastNameChar(int index)
    {
        ref readonly NodeRecord record = ref this[index];
        if (record.Has(NodeFlags.LongName))
        {
            return ExtraOf(index).Name![^1];
        }

        ReadOnlySpan<byte> bytes = _names.Slice(record.NameOffset, record.NameBytes);
        return record.Has(NodeFlags.WideName) ? MemoryMarshal.Cast<byte, char>(bytes)[^1] : (char)bytes[^1];
    }

    /// <summary>
    /// The name as characters: where it is stored when that is UTF-16, otherwise decoded into
    /// <paramref name="scratch"/>, which must hold an ASCII name's length.
    /// </summary>
    private ReadOnlySpan<char> NameChars(int index, Span<char> scratch)
    {
        ref readonly NodeRecord record = ref this[index];
        if (record.Has(NodeFlags.LongName))
        {
            return ExtraOf(index).Name;
        }

        ReadOnlySpan<byte> bytes = _names.Slice(record.NameOffset, record.NameBytes);
        if (record.Has(NodeFlags.WideName))
        {
            return MemoryMarshal.Cast<byte, char>(bytes);
        }

        Ascii.ToUtf16(bytes, scratch, out int written);
        return scratch[..written];
    }

    private int ReserveName(int length, int alignment)
    {
        long offset = (_nameBytes + alignment - 1) & ~(long)(alignment - 1);
        int segmentLength = _names.SegmentLength;
        if ((offset & (segmentLength - 1)) + length > segmentLength)
        {
            offset = (offset | (segmentLength - 1L)) + 1;
        }

        if (offset + length > int.MaxValue)
        {
            throw new InvalidOperationException("The snapshot's names do not fit in one table.");
        }

        _names.EnsureCapacity((int)offset + length);
        _nameBytes = (int)offset + length;
        return (int)offset;
    }

    private void BuildLookup(int count)
    {
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(8, count + (count / 3) + 1));
        var buckets = new int[size];
        int shift = 64 - BitOperations.Log2((uint)size);
        int mask = size - 1;
        for (int i = 0; i < count; i++)
        {
            Guid id = this[i].Id;
            int slot = Slot(id, shift);
            while (buckets[slot] != 0)
            {
                if (this[buckets[slot] - 1].Id == id)
                {
                    throw new StorageSnapshotValidationException($"Duplicate node ID: {id}.");
                }

                slot = (slot + 1) & mask;
            }

            buckets[slot] = i + 1;
        }

        _bucketShift = shift;
        _buckets = buckets;
    }

    private void BuildChildren(int count)
    {
        // Count each parent's children, turn the counts into end offsets, then fill every range
        // backwards so it ends up in index order with its start offset left behind.
        var start = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            int parent = this[i].Parent;
            if (parent >= 0)
            {
                start[parent]++;
            }
        }

        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += start[i];
            start[i] = total;
        }

        start[count] = total;
        var children = new int[total];
        for (int i = count - 1; i >= 0; i--)
        {
            int parent = this[i].Parent;
            if (parent >= 0)
            {
                children[--start[parent]] = i;
            }
        }

        // Sibling groups are independent, so a large table orders them on every core.
        int duplicateParent = int.MaxValue;
        if (count < ParallelOrderingThreshold)
        {
            int[] files = [];
            for (int parent = 0; parent < count && duplicateParent == int.MaxValue; parent++)
            {
                if (!OrderSiblings(children, start, parent, ref files))
                {
                    duplicateParent = parent;
                }
            }
        }
        else
        {
            const int ParentsPerChunk = 4096;
            BackgroundWork.For(
                0,
                (count + ParentsPerChunk - 1) / ParentsPerChunk,
                static () => Array.Empty<int>(),
                (chunk, _, files) =>
                {
                    int end = Math.Min(count, (chunk + 1) * ParentsPerChunk);
                    for (int parent = chunk * ParentsPerChunk; parent < end; parent++)
                    {
                        if (!OrderSiblings(children, start, parent, ref files))
                        {
                            int seen;
                            while (parent < (seen = Volatile.Read(ref duplicateParent)) &&
                                Interlocked.CompareExchange(ref duplicateParent, parent, seen) != seen)
                            {
                            }

                            break;
                        }
                    }

                    return files;
                },
                static _ => { });
        }

        if (duplicateParent != int.MaxValue)
        {
            Span<int> siblings = children.AsSpan(start[duplicateParent], start[duplicateParent + 1] - start[duplicateParent]);
            for (int k = 1; ; k++)
            {
                if (CompareNames(siblings[k - 1], siblings[k]) == 0)
                {
                    throw new StorageSnapshotValidationException(
                        $"Duplicate physical path: {PathOf(SecondOfRun(siblings, k - 1))}.");
                }
            }
        }

        _childStart = start;
        _children = children;
    }

    /// <summary>
    /// Puts one parent's children in display order: sorted by name alone first, so two siblings whose
    /// names differ only in case sit together whatever their kinds, then folders moved ahead without
    /// disturbing name order. Returns false, leaving them in name order, when two share a name.
    /// </summary>
    private bool OrderSiblings(int[] children, int[] start, int parent, ref int[] files)
    {
        int first = start[parent];
        int length = start[parent + 1] - first;
        if (length < 2)
        {
            return true;
        }

        Span<int> siblings = children.AsSpan(first, length);
        siblings.Sort(new NameOrder(this));
        for (int k = 1; k < length; k++)
        {
            if (CompareNames(siblings[k - 1], siblings[k]) == 0)
            {
                return false;
            }
        }

        if (files.Length < length)
        {
            files = new int[Math.Max(length, files.Length * 2)];
        }

        int folders = 0;
        int fileCount = 0;
        foreach (int sibling in siblings)
        {
            if (this[sibling].IsFolder)
            {
                siblings[folders++] = sibling;
            }
            else
            {
                files[fileCount++] = sibling;
            }
        }

        files.AsSpan(0, fileCount).CopyTo(siblings[folders..]);
        return true;
    }

    /// <summary>The second node, in the order they were added, of the run of equal names starting at <paramref name="first"/>.</summary>
    private int SecondOfRun(Span<int> siblings, int first)
    {
        int end = first + 1;
        while (end < siblings.Length && CompareNames(siblings[first], siblings[end]) == 0)
        {
            end++;
        }

        int lowest = int.MaxValue;
        int second = int.MaxValue;
        foreach (int sibling in siblings[first..end])
        {
            if (sibling < lowest)
            {
                second = lowest;
                lowest = sibling;
            }
            else if (sibling < second)
            {
                second = sibling;
            }
        }

        return second;
    }

    private readonly struct NameOrder(NodeTable table) : IComparer<int>
    {
        public int Compare(int left, int right) => table.CompareNames(left, right);
    }
}
