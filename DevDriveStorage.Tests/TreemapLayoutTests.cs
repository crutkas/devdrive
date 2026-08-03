using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class TreemapLayoutTests
{
    [TestMethod]
    public void LayoutIsBoundedNonOverlappingAndProportional()
    {
        var ids = Enumerable.Range(1, 4)
            .Select(index => Guid.Parse($"20000000-0000-0000-0000-{index:D12}"))
            .ToArray();
        var result = TreemapLayout.Calculate(
        [
            new TreemapItem(ids[0], 50),
            new TreemapItem(ids[1], 25),
            new TreemapItem(ids[2], 25),
            new TreemapItem(ids[3], 0),
        ]);

        Assert.HasCount(3, result);
        Assert.IsTrue(result.All(rectangle =>
            rectangle.X >= 0 && rectangle.Y >= 0 &&
            rectangle.Right <= 1 && rectangle.Bottom <= 1));
        Assert.AreEqual(0.5, result[0].Area, 0.0001);
        Assert.AreEqual(0.25, result[1].Area, 0.0001);
        for (int first = 0; first < result.Count; first++)
        {
            for (int second = first + 1; second < result.Count; second++)
            {
                Assert.IsFalse(result[first].Overlaps(result[second]));
            }
        }
    }

    [TestMethod]
    public void LayoutIsDeterministicAndHandlesEmptyInput()
    {
        var items = new[]
        {
            new TreemapItem(Guid.Parse("30000000-0000-0000-0000-000000000001"), 3),
            new TreemapItem(Guid.Parse("30000000-0000-0000-0000-000000000002"), 7),
        };

        CollectionAssert.AreEqual(
            TreemapLayout.Calculate(items).ToArray(),
            TreemapLayout.Calculate(items).ToArray());
        Assert.IsEmpty(TreemapLayout.Calculate([]));
        Assert.IsEmpty(TreemapLayout.Calculate(items, 0, 100));
        Assert.IsEmpty(TreemapLayout.Calculate(items, 100, -1));
    }

    [TestMethod]
    public void LayoutFillsTheCanvasAndKeepsAreaProportionalToWeight()
    {
        const double width = 900;
        const double height = 260;
        TreemapItem[] items = BuildItems(24);
        double totalWeight = items.Sum(item => item.Weight);

        var result = TreemapLayout.Calculate(items, width, height);

        Assert.HasCount(items.Length, result);
        Assert.AreEqual(width * height, result.Sum(rectangle => rectangle.Area), 1.0);
        foreach (TreemapRectangle rectangle in result)
        {
            double expected = items.Single(item => item.Id == rectangle.Id).Weight
                / totalWeight * width * height;
            Assert.AreEqual(expected, rectangle.Area, expected * 0.02 + 0.5);
            Assert.IsLessThanOrEqualTo(width + 0.0001, rectangle.Right);
            Assert.IsLessThanOrEqualTo(height + 0.0001, rectangle.Bottom);
        }
    }

    [TestMethod]
    public void LayoutProducesSquarishRectanglesRatherThanSlivers()
    {
        TreemapItem[] items = BuildItems(20);

        var squarified = TreemapLayout.Calculate(items, 900, 260);

        // A naive single-row strip would give the smallest item an aspect ratio in
        // the hundreds. Squarified layout must keep every rectangle usable.
        double worst = squarified.Max(rectangle => rectangle.AspectRatio);
        Assert.IsLessThan(12, worst, $"Worst aspect ratio was {worst:N1}.");

        double median = squarified.Select(rectangle => rectangle.AspectRatio)
            .Order()
            .ElementAt(squarified.Count / 2);
        Assert.IsLessThan(4, median, $"Median aspect ratio was {median:N1}.");
    }

    [TestMethod]
    public void LayoutNeverOverlapsForLargeDeterministicInput()
    {
        var result = TreemapLayout.Calculate(BuildItems(60), 640, 480);

        for (int first = 0; first < result.Count; first++)
        {
            for (int second = first + 1; second < result.Count; second++)
            {
                Assert.IsFalse(
                    result[first].Overlaps(result[second]),
                    $"Rectangles {first} and {second} overlap.");
            }
        }
    }

    private static TreemapItem[] BuildItems(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new TreemapItem(
                Guid.Parse($"40000000-0000-0000-0000-{index:D12}"),
                // Long-tailed distribution, which is what real storage looks like.
                Math.Pow(1.35, count - index) + index))
            .ToArray();
}
