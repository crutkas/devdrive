namespace DevDriveManager.ViewModels;

/// <summary>
/// One thing this build is capable of writing, where the write lands, and how to undo it.
/// </summary>
/// <remarks>
/// <para>
/// This is a hand-maintained list, and deliberately so — it is a claim about the whole application,
/// not a projection of any one subsystem, so there is nothing to derive it from that would not
/// itself have to be kept in step by hand. It belongs in a ViewModel rather than in the XAML because
/// a row is three related strings plus the fact that decides how the last one is inked, and eight
/// literal <c>Grid</c>s in markup would put that relationship somewhere it cannot be checked.
/// </para>
/// <para>
/// Whenever a room learns to write something new, it gets a row here. A settings page that claims
/// the app only reads, while another room quietly moves a folder, is worse than one that claims
/// nothing.
/// </para>
/// </remarks>
public sealed class WriteSurfaceViewModel
{
    public WriteSurfaceViewModel(string id, string what, string where, string wayBack, bool isReversible)
    {
        AutomationId = $"WriteSurface_{id}";
        What = what;
        Where = where;
        WayBack = wayBack;
        IsReversible = isReversible;
    }

    /// <summary>Stable id so the row is addressable from a test without depending on its position.</summary>
    public string AutomationId { get; }

    /// <summary>What gets written.</summary>
    public string What { get; }

    /// <summary>Where the write lands — a path, a store, or a scope.</summary>
    public string Where { get; }

    /// <summary>How to undo it, or why it cannot be undone from here.</summary>
    public string WayBack { get; }

    /// <summary>Whether <see cref="WayBack"/> is a route this app offers.</summary>
    public bool IsReversible { get; }

    /// <summary>The whole row as one sentence, for a screen reader reading rows rather than cells.</summary>
    public string AutomationName => $"{What}. {Where}. {WayBack}";

    /// <summary>
    /// Everything this build writes, in the order a reader meets it: the two stores Settings itself
    /// owns, then the two rooms that change the machine, then the honest closing statement.
    /// </summary>
    public static IReadOnlyList<WriteSurfaceViewModel> All { get; } =
    [
        new("prefs", "App preferences", "This app's per-user settings", "Reset to defaults, above", true),
        new(
            "history",
            "Free-space history",
            @"%LOCALAPPDATA%\DevDriveManager\free-space-history.json",
            "Clear, on the left",
            true),
        new(
            "cachemove",
            "Moving a package cache",
            "A copy of the folder, and the tool's per-user environment variable",
            "Move back, from Caches",
            true),
        new(
            "create",
            "Creating a Dev Drive",
            "A new ReFS volume, or a VHDX file at a path you pick",
            "Not from here — Disk Management",
            false),
        new(
            "bench",
            "Running a benchmark",
            "A throwaway project under your temp folder",
            "Deleted when the run ends",
            true),
        new(
            "everything",
            "Everything else",
            "Nothing. Volumes, filter drivers and folder sizes are only measured.",
            "Nothing to undo",
            false),
    ];
}
