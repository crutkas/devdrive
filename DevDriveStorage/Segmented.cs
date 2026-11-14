using System.Runtime.CompilerServices;

namespace DevDriveStorage;

/// <summary>
/// An append-only array kept in fixed-size segments. It grows by adding a segment, never by
/// copying everything written so far, so a table of millions of entries is never reallocated whole.
/// Only the first segment starts small and doubles, which keeps a small table small.
/// </summary>
/// <remarks>
/// One thread writes. It publishes every array it replaces with a release write, so a reader that
/// has observed a count published after the write (see <see cref="NodeTable.Count"/>) can read any
/// index below that count from whichever generation of array it finds.
/// </remarks>
internal sealed class Segmented<T>
{
    private const int FirstSegmentLength = 16;

    private readonly int _shift;
    private readonly int _mask;
    private T[][] _segments = [];
    private int _capacity;

    /// <param name="shift">Log2 of the segment length.</param>
    /// <param name="capacity">Entries to allocate up front, exactly.</param>
    public Segmented(int shift, int capacity = 0)
    {
        _shift = shift;
        _mask = (1 << shift) - 1;
        while (_capacity < capacity)
        {
            Grow(capacity, exact: true);
        }
    }

    public int SegmentLength => 1 << _shift;

    /// <summary>Bytes held by the segments, for footprint checks.</summary>
    public long CapacityBytes => ((long)_capacity * Unsafe.SizeOf<T>()) + ((long)_segments.Length * IntPtr.Size);

    /// <summary>Arrays the collector has to track: the segments and the array that holds them.</summary>
    public int ArrayCount => _capacity == 0 ? 0 : ((_capacity - 1) >> _shift) + 2;

    public ref T this[int index] => ref _segments[index >> _shift][index & _mask];

    /// <summary>A run of entries inside one segment.</summary>
    public Span<T> Slice(int index, int length) => _segments[index >> _shift].AsSpan(index & _mask, length);

    /// <summary>Makes every index below <paramref name="needed"/> writable.</summary>
    public void EnsureCapacity(int needed)
    {
        while (_capacity < needed)
        {
            Grow(needed, exact: false);
        }
    }

    /// <summary>
    /// Once the final count is known, gives back the unused tail of the last segment: one copy of at
    /// most a segment, so a table never keeps most of a segment it does not use.
    /// </summary>
    public void Trim(int count)
    {
        if (count <= 0 || count >= _capacity)
        {
            return;
        }

        int last = (count - 1) >> _shift;
        int used = count - (last << _shift);
        T[] segment = _segments[last];
        for (int s = last + 1; s < _segments.Length; s++)
        {
            _segments[s] = null!;
        }

        if ((long)(segment.Length - used) * Unsafe.SizeOf<T>() >= 4096)
        {
            var trimmed = new T[used];
            Array.Copy(segment, trimmed, used);
            Volatile.Write(ref _segments[last], trimmed);
            _capacity = count;
        }
        else
        {
            _capacity = (last << _shift) + segment.Length;
        }
    }

    private void Grow(int needed, bool exact)
    {
        int segmentLength = 1 << _shift;
        int last = (_capacity - 1) >> _shift;
        if (_capacity > 0 && _segments[last].Length < segmentLength)
        {
            T[] current = _segments[last];
            int wanted = needed - (last << _shift);
            int length = exact ? Math.Min(segmentLength, wanted)
                : last == 0 ? Math.Min(segmentLength, Math.Max(wanted, current.Length * 2))
                : segmentLength;
            var grown = new T[length];
            Array.Copy(current, grown, current.Length);
            Volatile.Write(ref _segments[last], grown);
            _capacity = (last << _shift) + length;
            return;
        }

        int next = last + 1;
        if (next == _segments.Length)
        {
            var directory = new T[Math.Max(4, _segments.Length * 2)][];
            Array.Copy(_segments, directory, _segments.Length);
            Volatile.Write(ref _segments, directory);
        }

        int remaining = needed - (next << _shift);
        int newLength = exact
            ? Math.Min(segmentLength, remaining)
            : next == 0 ? Math.Min(segmentLength, Math.Max(FirstSegmentLength, remaining)) : segmentLength;
        Volatile.Write(ref _segments[next], new T[newLength]);
        _capacity = (next << _shift) + newLength;
    }
}
