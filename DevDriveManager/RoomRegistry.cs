using DevDriveManager.Pages;
using DevDriveManager.Views;

namespace DevDriveManager;

/// <summary>Which subsystem a room belongs to. Crossing a section changes what the context strip means.</summary>
public enum RoomSection
{
    /// <summary>Rooms about bytes on disk. The context strip shows volumes.</summary>
    Storage,

    /// <summary>Rooms about how the machine is configured. The context strip shows the machine.</summary>
    Machine,

    /// <summary>Actions and settings that sit below the section dividers.</summary>
    Utility,
}

/// <summary>One tab inside a room. A room with a single tab shows no tab bar.</summary>
/// <param name="Tag">Stable id used by navigation, attention signals, and suggestions. Never localise this.</param>
/// <param name="Title">Tab label.</param>
/// <param name="PageType">The page to host.</param>
/// <param name="AutomationId">Tab automation id; a contract with the UI suite, so stated explicitly.</param>
public sealed record RoomTab(
    string Tag,
    string Title,
    Type PageType,
    string AutomationId);

/// <summary>One navigable room on the rail.</summary>
/// <param name="Tag">Stable id used by navigation. Equal to the tag of its first tab, which it opens on.</param>
/// <param name="Title">Rail label and page title.</param>
/// <param name="Glyph">Segoe Fluent icon.</param>
/// <param name="Section">Which subsystem the room belongs to.</param>
/// <param name="AutomationId">
/// Rail automation id. Stated explicitly rather than derived from <paramref name="Tag"/>, because
/// this is a contract with the UI suite: deriving it means renaming a room silently breaks tests
/// that were green a moment earlier, which is how a rename turns into a debugging session.
/// </param>
/// <param name="Tabs">The room's pages, in tab order. The first is where the room opens.</param>
/// <param name="OwnsHeader">True when the page draws its own title (Home's hero), so the shell shows none.</param>
public sealed record Room(
    string Tag,
    string Title,
    string Glyph,
    RoomSection Section,
    string AutomationId,
    IReadOnlyList<RoomTab> Tabs,
    bool OwnsHeader = false)
{
    public bool HasTabs => Tabs.Count > 1;
}

/// <summary>
/// A page reached from inside a room rather than from the rail or a tab. While it is open the rail
/// keeps its parent room selected and the shell header becomes a breadcrumb back to that room.
/// </summary>
/// <param name="Tag">Stable id used by navigation, attention signals, and suggestions.</param>
/// <param name="Title">Last breadcrumb segment.</param>
/// <param name="PageType">The page to host.</param>
/// <param name="ParentTag">The rail room this page belongs to.</param>
public sealed record SubPage(
    string Tag,
    string Title,
    Type PageType,
    string ParentTag);

/// <summary>Where a navigation tag leads: a room, and either one of its tabs or one of its sub-pages.</summary>
public sealed record RoomRoute(Room Room, RoomTab? Tab, SubPage? SubPage)
{
    public string Tag => Tab?.Tag ?? SubPage!.Tag;

    public Type PageType => Tab?.PageType ?? SubPage!.PageType;
}

/// <summary>
/// The one place that knows what rooms exist.
/// </summary>
/// <remarks>
/// The app answers three questions, in priority order, and the rail has one room for each:
/// <list type="bullet">
/// <item><b>Home</b> — how do my drives look, and what should I do about it?</item>
/// <item><b>Space</b> — where did my space go, and what can I clean up? One page: the map, with what
/// can be cleaned up beside it.</item>
/// <item><b>Dev Drive</b> — set one up, move package caches onto it, and (later) prove it is faster.
/// One page: the Dev Drive, with the caches that belong on it beside it.</item>
/// </list>
/// Everything else is a tab or a sub-page of one of those rooms. Tags from the old flat rail
/// (<c>reclaim</c>, <c>caches</c>, <c>benchmarks</c>, <c>create</c>) still resolve, because attention
/// signals and suggestions are keyed on them.
/// </remarks>
public static class RoomRegistry
{
    /// <summary>Rooms in rail order.</summary>
    /// <remarks>
    /// Existing automation ids are kept verbatim even where the room has been retitled — Home stays
    /// <c>NavDashboard</c>, Dev Drive stays <c>NavDrives</c> — so the reframe does not cost a green UI
    /// suite. Renaming both at once would conflate a product decision with a test churn.
    /// </remarks>
    public static IReadOnlyList<Room> All { get; } =
    [
        new("overview", "Home", "\uE80F", RoomSection.Storage, "NavDashboard",
            [new("overview", "Home", typeof(DashboardPage), "Tab_Home")],
            OwnsHeader: true),
        new("space", "Space", "\uE9D5", RoomSection.Storage, "NavSpace",
            [new("space", "Map", typeof(SpacePage), "Tab_SpaceMap")]),
        new("drives", "Dev Drive", "\uEDA2", RoomSection.Storage, "NavDrives",
            [new("drives", "Overview", typeof(DevDrivePage), "Tab_DevDriveOverview")]),
    ];

    /// <summary>
    /// Pages opened from inside a room. Creating a Dev Drive, comparing speed, and the advanced
    /// volume and filter detail are all Dev Drive tasks, not rooms of their own.
    /// </summary>
    public static IReadOnlyList<SubPage> SubPages { get; } =
    [
        new("create", "Create Dev Drive", typeof(CreateDevDrivePage), "drives"),
        new("benchmarks", "Compare speed", typeof(BenchmarksPage), "drives"),
        new("volumes", "Drives and filters", typeof(DrivesPage), "drives"),
    ];

    /// <summary>
    /// Old tags that now open somewhere else. Clean up and package caches used to be tabs; they are
    /// now panels beside the Space map and the Dev Drive page, but attention signals and Home's links
    /// still ask for <c>reclaim</c> and <c>caches</c>.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["reclaim"] = "space",
        ["caches"] = "drives",
    };

    public static Room? Find(string? tag) =>
        tag is null ? null : All.FirstOrDefault(r => r.Tag == tag);

    public static SubPage? FindSubPage(string? tag) =>
        tag is null ? null : SubPages.FirstOrDefault(p => p.Tag == tag);

    /// <summary>Resolves any navigation tag — room, tab, or sub-page — to where it leads.</summary>
    public static RoomRoute? Resolve(string? tag)
    {
        if (tag is null)
        {
            return null;
        }

        tag = Aliases.GetValueOrDefault(tag, tag);
        foreach (Room room in All)
        {
            if (room.Tabs.FirstOrDefault(t => t.Tag == tag) is { } tab)
            {
                return new RoomRoute(room, tab, null);
            }
        }

        return FindSubPage(tag) is { } subPage && Find(subPage.ParentTag) is { } parent
            ? new RoomRoute(parent, null, subPage)
            : null;
    }

    /// <summary>The room the app opens on.</summary>
    public static Room Default => All[0];
}
