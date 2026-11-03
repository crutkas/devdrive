namespace DevDriveManager.ViewModels;

/// <summary>
/// One row of the Benchmarks room's Suggestions tab: a lever that moves the numbers, what it is
/// worth, and the room that pulls it.
/// </summary>
/// <remarks>
/// The wording comes from <see cref="PerformanceSuiteViewModel"/>, which has carried these strings —
/// and tests for them — since before the room had anywhere to put them. The measured table says how
/// fast the machine is today; this says what would change that, which is a different question about
/// the same subject and therefore earns the second tab rather than a heading further down one page.
/// </remarks>
public sealed class BenchSuggestionViewModel
{
    /// <summary>Short imperative title — the lever, not the outcome.</summary>
    public required string Title { get; init; }

    /// <summary>The honest take on what it is worth on this machine.</summary>
    public required string Detail { get; init; }

    /// <summary>The verb, or empty when there is nothing this app can do about it.</summary>
    public string ActionText { get; init; } = string.Empty;

    /// <summary>Room tag for <c>ShellPage.SelectNavItem</c>, or empty for an advisory row.</summary>
    public string RoomTag { get; init; } = string.Empty;

    public required string AutomationId { get; init; }

    public bool HasAction => !string.IsNullOrEmpty(RoomTag) && !string.IsNullOrEmpty(ActionText);

    public string ActionAutomationId => $"{AutomationId}_Action";
}
