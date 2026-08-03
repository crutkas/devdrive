namespace DevDriveStorage;

public readonly record struct TreemapItem(Guid Id, double Weight);

public readonly record struct TreemapRectangle(
    Guid Id,
    double X,
    double Y,
    double Width,
    double Height,
    int Depth = 0)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    public double AspectRatio => Width <= 0 || Height <= 0
        ? double.PositiveInfinity
        : Math.Max(Width / Height, Height / Width);

    public bool Overlaps(TreemapRectangle other)
    {
        const double epsilon = 0.0000001;
        return X < other.Right - epsilon &&
            Right > other.X + epsilon &&
            Y < other.Bottom - epsilon &&
            Bottom > other.Y + epsilon;
    }
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, and van Wijk). Rectangle area stays
/// proportional to weight while the algorithm favours squarish rectangles, which is
/// what keeps labels readable and relative size easy to compare.
/// </summary>
public static class TreemapLayout
{
    public static IReadOnlyList<TreemapRectangle> Calculate(IEnumerable<TreemapItem> items) =>
        Calculate(items, 1, 1);

    public static IReadOnlyList<TreemapRectangle> Calculate(
        IEnumerable<TreemapItem> items,
        double width,
        double height)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        TreemapItem[] ordered = items.Where(item => item.Weight > 0)
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Id)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        double totalWeight = ordered.Sum(item => item.Weight);
        double areaPerWeight = width * height / totalWeight;

        var results = new List<TreemapRectangle>(ordered.Length);
        var free = new Frame(0, 0, width, height);
        var row = new List<RowEntry>();
        double rowArea = 0;

        for (int index = 0; index < ordered.Length;)
        {
            double candidateArea = ordered[index].Weight * areaPerWeight;
            double shortSide = Math.Min(free.Width, free.Height);

            if (row.Count == 0 ||
                Worst(row, rowArea, shortSide) >=
                    Worst(row, rowArea + candidateArea, shortSide, candidateArea))
            {
                row.Add(new RowEntry(ordered[index].Id, candidateArea));
                rowArea += candidateArea;
                index++;
                continue;
            }

            free = EmitRow(results, row, rowArea, free, isFinalRow: false);
            row.Clear();
            rowArea = 0;
        }

        if (row.Count > 0)
        {
            EmitRow(results, row, rowArea, free, isFinalRow: true);
        }

        return Clamp(results, width, height);
    }

    private static Frame EmitRow(
        List<TreemapRectangle> results,
        List<RowEntry> row,
        double rowArea,
        Frame free,
        bool isFinalRow)
    {
        // The row is laid along the shorter side, which is what keeps the resulting
        // rectangles close to square.
        if (free.Width <= free.Height)
        {
            double bandHeight = isFinalRow ? free.Height : SafeDivide(rowArea, free.Width);
            bandHeight = Math.Min(bandHeight, free.Height);
            double offset = free.X;
            for (int index = 0; index < row.Count; index++)
            {
                bool isLast = index == row.Count - 1;
                double itemWidth = isLast
                    ? free.X + free.Width - offset
                    : SafeDivide(row[index].Area, bandHeight);
                results.Add(new TreemapRectangle(
                    row[index].Id, offset, free.Y, Math.Max(0, itemWidth), Math.Max(0, bandHeight)));
                offset += itemWidth;
            }

            return free with { Y = free.Y + bandHeight, Height = free.Height - bandHeight };
        }

        double bandWidth = isFinalRow ? free.Width : SafeDivide(rowArea, free.Height);
        bandWidth = Math.Min(bandWidth, free.Width);
        double verticalOffset = free.Y;
        for (int index = 0; index < row.Count; index++)
        {
            bool isLast = index == row.Count - 1;
            double itemHeight = isLast
                ? free.Y + free.Height - verticalOffset
                : SafeDivide(row[index].Area, bandWidth);
            results.Add(new TreemapRectangle(
                row[index].Id, free.X, verticalOffset, Math.Max(0, bandWidth), Math.Max(0, itemHeight)));
            verticalOffset += itemHeight;
        }

        return free with { X = free.X + bandWidth, Width = free.Width - bandWidth };
    }

    private static double Worst(
        List<RowEntry> row,
        double rowArea,
        double side,
        double extraArea = 0)
    {
        if (rowArea <= 0 || side <= 0)
        {
            return double.PositiveInfinity;
        }

        double max = extraArea;
        double min = extraArea > 0 ? extraArea : double.MaxValue;
        foreach (RowEntry entry in row)
        {
            max = Math.Max(max, entry.Area);
            min = Math.Min(min, entry.Area);
        }

        if (min is <= 0 or double.MaxValue)
        {
            return double.PositiveInfinity;
        }

        double squared = rowArea * rowArea;
        double sideSquared = side * side;
        return Math.Max(sideSquared * max / squared, squared / (sideSquared * min));
    }

    private static IReadOnlyList<TreemapRectangle> Clamp(
        List<TreemapRectangle> rectangles,
        double width,
        double height)
    {
        for (int index = 0; index < rectangles.Count; index++)
        {
            TreemapRectangle rectangle = rectangles[index];
            double x = Math.Clamp(rectangle.X, 0, width);
            double y = Math.Clamp(rectangle.Y, 0, height);
            rectangles[index] = rectangle with
            {
                X = x,
                Y = y,
                Width = Math.Clamp(rectangle.Width, 0, width - x),
                Height = Math.Clamp(rectangle.Height, 0, height - y),
            };
        }

        return rectangles;
    }

    private static double SafeDivide(double value, double divisor) =>
        divisor <= 0 ? 0 : value / divisor;

    private readonly record struct RowEntry(Guid Id, double Area);

    private readonly record struct Frame(double X, double Y, double Width, double Height);
}
