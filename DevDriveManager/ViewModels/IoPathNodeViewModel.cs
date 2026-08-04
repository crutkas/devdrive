namespace DevDriveManager.ViewModels;

/// <summary>
/// One node in the Drives room's I/O path diagram: an endpoint of a write, or a filter driver that
/// either runs on the volume or is skipped on it.
/// </summary>
/// <remarks>
/// A top-level type rather than one nested in the page, because <c>x:DataType</c> cannot name a nested
/// type. Immutable: the whole path is rebuilt from a fresh filter reading rather than mutated.
/// </remarks>
/// <param name="Title">The filter's name, or what the endpoint is.</param>
/// <param name="Caption">Its altitude, or what happens at that endpoint.</param>
/// <param name="Kind">
/// <c>on</c> (runs here), <c>off</c> (skipped here) or <c>end</c> (an endpoint) — see
/// <c>UiHelpers.StackNodeStyle</c>.
/// </param>
/// <param name="ShowArrow">False for the first node in a column, which has nothing above it.</param>
public sealed record IoPathNodeViewModel(string Title, string Caption, string Kind, bool ShowArrow)
{
    /// <summary>
    /// What a screen reader announces. The two lines read as one fact — "WdFilter, 328,010 — skipped"
    /// — because separately they are a name and a number with no relationship stated.
    /// </summary>
    public string AutomationName => $"{Title}, {Caption}";
}
