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

/// <summary>One navigable room.</summary>
/// <param name="Tag">Stable id used by navigation. Never localise this.</param>
/// <param name="Title">Rail label.</param>
/// <param name="Glyph">Segoe Fluent icon.</param>
/// <param name="PageType">The page to host.</param>
/// <param name="Section">Which subsystem the room belongs to.</param>
/// <param name="AutomationId">
/// Rail automation id. Stated explicitly rather than derived from <paramref name="Tag"/>, because
/// this is a contract with the UI suite: deriving it means renaming a room silently breaks tests
/// that were green a moment earlier, which is how a rename turns into a debugging session.
/// </param>
public sealed record Room(
    string Tag,
    string Title,
    string Glyph,
    Type PageType,
    RoomSection Section,
    string AutomationId);

/// <summary>
/// The one place that knows what rooms exist.
/// </summary>
/// <remarks>
/// This replaced a hardcoded <c>switch</c> in the shell that had to be edited in three places — the
/// XAML rail, the tag-to-page switch, and the automation ids — to add a single room. The product is
/// explicitly heading toward "an integrated system, or something even larger", so the cost of adding
/// a room is a thing worth keeping near zero.
/// <para>
/// It also makes the still-open Env / Hosts question cheap to answer either way: they are one entry
/// each in <see cref="All"/>, and the rail, the section divider, and the context strip follow.
/// </para>
/// </remarks>
public static class RoomRegistry
{
    /// <summary>Rooms in rail order.</summary>
    /// <remarks>
    /// Existing automation ids are kept verbatim even where the room has been retitled — "Dashboard"
    /// is now "Overview" but stays <c>NavDashboard</c> — so the reframe does not cost a green UI
    /// suite. Renaming both at once would conflate a product decision with a test churn.
    /// </remarks>
    public static IReadOnlyList<Room> All { get; } =
    [
        new("overview", "Overview", "\uE80F", typeof(DashboardPage), RoomSection.Storage, "NavDashboard"),
        new("reclaim", "Reclaim", "\uE74D", typeof(ReclaimPage), RoomSection.Storage, "NavReclaim"),
        new("caches", "Package caches", "\uE8B7", typeof(PackageCachesPage), RoomSection.Storage, "NavPackageCaches"),
        new("drives", "Drives", "\uEDA2", typeof(DrivesPage), RoomSection.Storage, "NavDrives"),
        new("benchmarks", "Benchmarks", "\uE9D9", typeof(BenchmarksPage), RoomSection.Storage, "NavBenchmarks"),
        new("create", "Create Dev Drive", "\uE710", typeof(CreateDevDrivePage), RoomSection.Utility, "NavCreate"),
    ];

    public static Room? Find(string? tag) =>
        tag is null ? null : All.FirstOrDefault(r => r.Tag == tag);

    /// <summary>The room the app opens on.</summary>
    public static Room Default => All[0];
}
