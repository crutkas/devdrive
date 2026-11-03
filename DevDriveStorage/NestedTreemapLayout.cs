namespace DevDriveStorage;

/// <summary>What a nested treemap rectangle stands for.</summary>
public enum TreemapCellKind
{
    Folder,
    File,

    /// <summary>The smallest siblings of a folder, folded into one block so the map stays legible.</summary>
    Overflow,

    /// <summary>
    /// Bytes a folder reports that none of its listed children account for. During a streaming scan
    /// these are the files the walk has already counted but not yet listed.
    /// </summary>
    Unlisted,
}

/// <summary>
/// One rectangle of a nested treemap, in absolute canvas coordinates. Cells are emitted parents
/// first, so a renderer can create a parent before any of its children.
/// </summary>
/// <param name="Id">The node ID, or a stable synthetic ID for overflow and unlisted cells.</param>
/// <param name="ParentId">The containing cell, or <see langword="null"/> for a top-level cell.</param>
/// <param name="BranchId">The top-level cell this one sits inside; it carries the branch colour.</param>
/// <param name="Target">
/// The node a click or context menu acts on. For overflow and unlisted cells this is the folder
/// that owns them, because the cell itself has no path of its own.
/// </param>
/// <param name="Rank">Position among its siblings by size, largest first.</param>
/// <param name="FoldedCount">For an overflow cell, how many siblings it stands for.</param>
/// <param name="HeaderHeight">Height of the title strip; greater than zero only for containers.</param>
public sealed record NestedTreemapCell(
    Guid Id,
    Guid? ParentId,
    Guid BranchId,
    TreemapCellKind Kind,
    StorageNode Target,
    string Name,
    long SizeBytes,
    int Depth,
    int Rank,
    int FoldedCount,
    double X,
    double Y,
    double Width,
    double Height,
    double HeaderHeight)
{
    /// <summary>Whether the cell draws its children inside itself below a title strip.</summary>
    public bool IsContainer => HeaderHeight > 0;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public sealed record NestedTreemapOptions
{
    public static NestedTreemapOptions Default { get; } = new();

    /// <summary>Deepest level that may still be drawn; cells at this depth never nest further.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>
    /// Children drawn individually per container. The rest fold into one overflow cell, which keeps
    /// a folder of ten thousand tiny files from turning into ten thousand slivers.
    /// </summary>
    public int ItemsPerContainer { get; init; } = StorageExplorerViewModel.TreemapItemLimit;

    /// <summary>
    /// Upper bound on emitted cells. The layout is breadth-first, so the budget runs out in the
    /// deepest, smallest detail and never in the top level.
    /// </summary>
    public int MaxCells { get; init; } = 1800;

    /// <summary>Cells thinner than this on either side are left to their container's background.</summary>
    public double MinimumCellSide { get; init; } = 3;

    public double TopLevelGap { get; init; } = 4;

    public double Gap { get; init; } = 2;

    /// <summary>Inset between a container's edge and its children, left, right and bottom.</summary>
    public double Padding { get; init; } = 3;

    public double TopLevelHeaderHeight { get; init; } = 22;

    public double HeaderHeight { get; init; } = 18;

    public double MinimumContainerWidth { get; init; } = 48;

    /// <summary>A folder only opens up when at least this much height is left below its header.</summary>
    public double MinimumContentHeight { get; init; } = 14;

    /// <summary>Unaccounted bytes below this share of their folder are rounding, not a block.</summary>
    public double MinimumUnlistedShare { get; init; } = 0.005;
}

/// <summary>
/// Nested squarified treemap in the style of SpaceSniffer: every folder with room to spare opens up
/// into its own children, level by level, under a title strip. Built on <see cref="TreemapLayout"/>
/// so each level keeps its areas proportional and its rectangles squarish.
/// </summary>
public static class NestedTreemapLayout
{
    private const byte OverflowSalt = 0x4F;
    private const byte UnlistedSalt = 0x55;

    public static IReadOnlyList<NestedTreemapCell> Calculate(
        StorageSnapshot snapshot,
        Guid scopeId,
        double width,
        double height,
        NestedTreemapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= NestedTreemapOptions.Default;
        if (!(width > 0) || !(height > 0) || snapshot.Find(scopeId) is not StorageNode scope)
        {
            return [];
        }

        string unlistedName = snapshot.Completion == SnapshotCompletion.Partial ? "Files" : "Other";
        var cells = new List<NestedTreemapCell>();
        var childCounts = new Dictionary<Guid, int>();
        var pending = new Queue<Frame>();
        pending.Enqueue(new Frame(scope, null, null, 0, 0, 0, width, height));

        while (pending.Count > 0 && cells.Count < options.MaxCells)
        {
            Frame frame = pending.Dequeue();
            List<Entry> entries = EntriesOf(snapshot, frame.Folder, options, unlistedName);
            if (entries.Count == 0)
            {
                continue;
            }

            // A folder opens up whole or not at all: half its children would read as missing data.
            // The top level is always drawn, whatever the budget.
            if (frame.Depth > 0 && cells.Count + entries.Count > options.MaxCells)
            {
                continue;
            }

            var byId = new Dictionary<Guid, Entry>(entries.Count);
            foreach (Entry entry in entries)
            {
                byId[entry.Id] = entry;
            }

            IReadOnlyList<TreemapRectangle> rectangles = TreemapLayout.Calculate(
                entries.Select(entry => new TreemapItem(entry.Id, entry.Bytes)),
                frame.Width,
                frame.Height);

            double gap = frame.Depth == 0 ? options.TopLevelGap : options.Gap;
            double header = frame.Depth == 0 ? options.TopLevelHeaderHeight : options.HeaderHeight;
            for (int rank = 0; rank < rectangles.Count; rank++)
            {
                TreemapRectangle rectangle = rectangles[rank];
                Entry entry = byId[rectangle.Id];
                double x = frame.X + rectangle.X + (gap / 2);
                double y = frame.Y + rectangle.Y + (gap / 2);
                double cellWidth = rectangle.Width - gap;
                double cellHeight = rectangle.Height - gap;
                if (cellWidth < options.MinimumCellSide || cellHeight < options.MinimumCellSide)
                {
                    continue;
                }

                bool nests = entry.Kind == TreemapCellKind.Folder &&
                    frame.Depth < options.MaxDepth &&
                    cellWidth >= options.MinimumContainerWidth &&
                    cellHeight >= header + options.Padding + options.MinimumContentHeight &&
                    HasSizedChildren(snapshot, entry.Target);
                Guid branch = frame.BranchId ?? entry.Id;
                cells.Add(new NestedTreemapCell(
                    entry.Id,
                    frame.ParentCellId,
                    branch,
                    entry.Kind,
                    entry.Target,
                    entry.Name,
                    entry.Bytes,
                    frame.Depth,
                    rank,
                    entry.FoldedCount,
                    x,
                    y,
                    cellWidth,
                    cellHeight,
                    nests ? header : 0));

                if (frame.ParentCellId is Guid parent)
                {
                    childCounts[parent] = childCounts.GetValueOrDefault(parent) + 1;
                }

                if (nests)
                {
                    pending.Enqueue(new Frame(
                        entry.Target,
                        entry.Id,
                        branch,
                        frame.Depth + 1,
                        x + options.Padding,
                        y + header,
                        cellWidth - (2 * options.Padding),
                        cellHeight - header - options.Padding));
                }
            }
        }

        // A container whose children were all too small to draw — or were cut by the cell budget —
        // would be an empty frame with a title. It reads better as the solid block it really is.
        for (int index = 0; index < cells.Count; index++)
        {
            if (cells[index].IsContainer && !childCounts.ContainsKey(cells[index].Id))
            {
                cells[index] = cells[index] with { HeaderHeight = 0 };
            }
        }

        return cells;
    }

    /// <summary>The ID of the cell that stands for a folder's smallest, folded-away children.</summary>
    public static Guid OverflowIdOf(Guid folderId) => Derive(folderId, OverflowSalt);

    /// <summary>The ID of the cell that stands for bytes a folder reports but does not list.</summary>
    public static Guid UnlistedIdOf(Guid folderId) => Derive(folderId, UnlistedSalt);

    private static List<Entry> EntriesOf(
        StorageSnapshot snapshot,
        StorageNode folder,
        NestedTreemapOptions options,
        string unlistedName)
    {
        List<StorageNode> sized = snapshot.ChildrenOf(folder.Id)
            .Where(child => child.SizeBytes > 0)
            .OrderByDescending(child => child.SizeBytes)
            .ThenBy(child => child.Id)
            .ToList();

        int keep = Math.Min(sized.Count, Math.Max(1, options.ItemsPerContainer));
        var entries = new List<Entry>(keep + 2);
        long listed = 0;
        for (int index = 0; index < keep; index++)
        {
            StorageNode child = sized[index];
            entries.Add(new Entry(
                child.Id,
                child.Kind == StorageNodeKind.Folder ? TreemapCellKind.Folder : TreemapCellKind.File,
                child,
                child.Name,
                child.SizeBytes,
                0));
            listed += child.SizeBytes;
        }

        long folded = 0;
        for (int index = keep; index < sized.Count; index++)
        {
            folded += sized[index].SizeBytes;
        }

        int foldedCount = sized.Count - keep;
        if (foldedCount > 0 && folded > 0)
        {
            entries.Add(new Entry(
                Derive(folder.Id, OverflowSalt),
                TreemapCellKind.Overflow,
                folder,
                $"+{foldedCount:N0} more",
                folded,
                foldedCount));
        }

        long remainder = folder.IsMeasured ? folder.SizeBytes - listed - folded : 0;
        if (remainder > 0 && remainder >= folder.SizeBytes * options.MinimumUnlistedShare)
        {
            entries.Add(new Entry(
                Derive(folder.Id, UnlistedSalt),
                TreemapCellKind.Unlisted,
                folder,
                unlistedName,
                remainder,
                0));
        }

        return entries;
    }

    private static bool HasSizedChildren(StorageSnapshot snapshot, StorageNode folder)
    {
        foreach (StorageNode child in snapshot.ChildrenOf(folder.Id))
        {
            if (child.SizeBytes > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A synthetic cell needs an ID that survives every re-layout of its folder, so the renderer can
    /// animate it instead of recreating it; deriving it from the folder's own ID does exactly that.
    /// </summary>
    private static Guid Derive(Guid folderId, byte salt)
    {
        Span<byte> bytes = stackalloc byte[16];
        folderId.TryWriteBytes(bytes);
        bytes[0] ^= salt;
        bytes[7] ^= (byte)~salt;
        bytes[15] ^= (byte)(salt * 3);
        return new Guid(bytes);
    }

    private readonly record struct Entry(
        Guid Id,
        TreemapCellKind Kind,
        StorageNode Target,
        string Name,
        long Bytes,
        int FoldedCount);

    private readonly record struct Frame(
        StorageNode Folder,
        Guid? ParentCellId,
        Guid? BranchId,
        int Depth,
        double X,
        double Y,
        double Width,
        double Height);
}
