using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Headless tests for the Drives room's filter-driver projection: the row set, the I/O-path ordering,
/// and the altitude parsing rule. The altitude lookup is injected, so nothing here depends on which
/// minifilters the test machine happens to have installed.
/// </summary>
[TestClass]
public sealed class FilterDriverProjectionTests
{
    private static readonly Dictionary<string, double> KnownAltitudes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bindFlt"] = 409800,
        ["WdFilter"] = 328010,
        ["storqosflt"] = 244000,
        ["wcifs"] = 189900,
        ["CldFlt"] = 180451,
        ["FileInfo"] = 40500,
    };

    private static double? Lookup(string name) =>
        KnownAltitudes.TryGetValue(name, out double value) ? value : null;

    [TestMethod]
    public void Project_ListsAllowedFiltersThatAreNotAttached()
    {
        // The most interesting row on a Dev Drive is the antivirus filter that ISN'T attached, so a
        // projection that only listed attached filters would hide the reason the volume is fast.
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: ["bindFlt", "wcifs"],
            allowed: ["WdFilter", "bindFlt"],
            Lookup);

        CollectionAssert.AreEquivalent(
            new[] { "bindFlt", "wcifs", "WdFilter" },
            rows.Select(r => r.Name).ToArray());

        FilterDriverInfo defender = rows.Single(r => r.Name == "WdFilter");
        Assert.IsFalse(defender.IsAttached, "Defender is allowed here but not attached.");
        Assert.IsTrue(defender.IsAllowed);
    }

    [TestMethod]
    public void Project_OrdersByAltitudeDescending_BecauseThatIsTheOrderAWritePassesThrough()
    {
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: ["FileInfo", "bindFlt", "CldFlt"],
            allowed: [],
            Lookup);

        CollectionAssert.AreEqual(
            new[] { "bindFlt", "CldFlt", "FileInfo" },
            rows.Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public void Project_SortsUnknownAltitudesLast_RatherThanTreatingThemAsZero()
    {
        // Zero would put an unreadable filter at the filesystem end of the path, which is a claim we
        // have no basis for. Last in a list that is explicitly altitude-ordered says "we don't know".
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: ["someThirdPartyFlt", "FileInfo", "bindFlt"],
            allowed: [],
            Lookup);

        Assert.AreEqual("someThirdPartyFlt", rows[^1].Name);
        Assert.IsNull(rows[^1].Altitude);
        CollectionAssert.AreEqual(
            new[] { "bindFlt", "FileInfo" },
            rows.Take(2).Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public void Project_DeduplicatesCaseInsensitively()
    {
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: ["bindFlt"],
            allowed: ["BINDFLT"],
            Lookup);

        Assert.HasCount(1, rows);
        Assert.AreEqual("bindFlt", rows[0].Name, "The first reported casing wins — it is the registered name.");
        Assert.IsTrue(rows[0].IsAttached);
        Assert.IsTrue(rows[0].IsAllowed);
    }

    [TestMethod]
    public void Project_DescribesKnownFilters_AndLeavesUnknownOnesBlank()
    {
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: ["WdFilter", "someThirdPartyFlt"],
            allowed: [],
            Lookup);

        Assert.AreNotEqual(
            string.Empty,
            rows.Single(r => r.Name == "WdFilter").Description,
            "Defender is a documented Windows component; we can say what it does.");
        Assert.AreEqual(
            string.Empty,
            rows.Single(r => r.Name == "someThirdPartyFlt").Description,
            "A wrong description is worse than none.");
    }

    [TestMethod]
    public void Project_IgnoresBlankNames()
    {
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: ["bindFlt", "  ", string.Empty],
            allowed: [],
            Lookup);

        Assert.HasCount(1, rows);
    }

    [TestMethod]
    public void Project_TrimsNames_SoWhitespaceDoesNotSplitOneFilterIntoTwo()
    {
        IReadOnlyList<FilterDriverInfo> rows = FilterDriverProjection.Project(
            attached: [" bindFlt "],
            allowed: ["bindFlt"],
            Lookup);

        Assert.HasCount(1, rows);
        Assert.AreEqual("bindFlt", rows[0].Name);
        Assert.AreEqual(409800d, rows[0].Altitude, "Trimming has to happen before the altitude lookup.");
    }

    [TestMethod]
    public void ParseAltitude_ReadsFractionalAltitudes()
    {
        // Filters slot between two allocated altitudes with a fraction; parsing as int would drop them.
        Assert.AreEqual(180451.5d, FilterAltitudeReader.ParseAltitude("180451.5"));
        Assert.AreEqual(409800d, FilterAltitudeReader.ParseAltitude(" 409800 "));
    }

    [TestMethod]
    public void ParseAltitude_ReturnsNullForAnythingUnparseable()
    {
        Assert.IsNull(FilterAltitudeReader.ParseAltitude(null));
        Assert.IsNull(FilterAltitudeReader.ParseAltitude(string.Empty));
        Assert.IsNull(FilterAltitudeReader.ParseAltitude("not-a-number"));
    }

    [TestMethod]
    public void ReadAltitude_ForAFilterThatDoesNotExist_IsNullRatherThanThrowing()
    {
        Assert.IsNull(FilterAltitudeReader.Read("NoSuchFilterExistsAnywhere"));
        Assert.IsNull(FilterAltitudeReader.Read(string.Empty));
    }
}
