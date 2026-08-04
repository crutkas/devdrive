namespace DevDriveManager.ViewModels;

/// <summary>
/// One line of the Overview inspector's fact lists: a label on the left, a value on the right, and
/// an optional quiet trailing note.
/// </summary>
/// <remarks>
/// Deliberately generic rather than one view model per list. The inspector shows three lists that
/// differ only in what fills them — coverage facts, the biggest folders, and what moved — and three
/// near-identical row types would be three places to fix the same alignment bug.
/// </remarks>
/// <param name="Label">Left-hand label.</param>
/// <param name="Value">Right-hand value, emphasised.</param>
/// <param name="Note">Optional trailing note in the faint ink, or empty.</param>
/// <param name="AutomationId">Automation id for the value, so a test can assert the number.</param>
public sealed record InspectorFactViewModel(
    string Label,
    string Value,
    string Note = "",
    string AutomationId = "")
{
    /// <summary>True when there is a trailing note to show.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>What a screen reader reads for the line.</summary>
    public string AutomationName => HasNote ? $"{Label}: {Value}, {Note}" : $"{Label}: {Value}";
}
