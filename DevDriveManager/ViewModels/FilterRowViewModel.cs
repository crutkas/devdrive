using System.Globalization;
using DevDriveCore.Models;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One filter driver row in the Drives room. Immutable — the underlying reading is a snapshot taken
/// by a single elevated probe, so every binding is <c>OneTime</c>.
/// </summary>
public sealed class FilterRowViewModel
{
    public FilterRowViewModel(FilterDriverInfo info, char devLetter)
    {
        Info = info;
        Name = info.Name;

        // Grouped thousands, because an altitude is read as a position and 409,800 is easier to place
        // against 328,010 than 409800 is. An em dash when the filter has no registered instance we
        // could read — better than a zero, which would claim it runs closest to the filesystem.
        AltitudeText = info.Altitude is double altitude
            ? altitude.ToString("#,0.###", CultureInfo.CurrentCulture)
            : "\u2014";

        StatusKind = info.IsAttached ? "dev" : "mute";
        StatusText = info.IsAttached ? "Attached" : "Detached";
        Description = info.Description;

        AutomationId = $"FilterRow_{info.Name}";
        AutomationName = BuildAutomationName(devLetter);
    }

    public FilterDriverInfo Info { get; }

    public string Name { get; }

    public string AltitudeText { get; }

    /// <summary>Pill kind: attached reads as the good state, detached as muted.</summary>
    public string StatusKind { get; }

    public string StatusText { get; }

    public string Description { get; }

    public bool IsAttached => Info.IsAttached;

    /// <summary>True when the filter has a readable altitude, so the stack can place it.</summary>
    public bool HasAltitude => Info.Altitude is not null;

    /// <summary>The stack tile's second line: the altitude, and why it is not in the path if it isn't.</summary>
    public string StackCaption => Info.IsAttached
        ? AltitudeText
        : $"{AltitudeText} \u2014 skipped";

    public string AutomationId { get; }

    public string AutomationName { get; }

    private string BuildAutomationName(char devLetter)
    {
        string where = Info.IsAttached ? $"attached to {devLetter}:" : $"not attached to {devLetter}:";
        string altitude = Info.Altitude is null ? "altitude unknown" : $"altitude {AltitudeText}";
        return Description.Length > 0
            ? $"{Name}, {altitude}, {where}. {Description}"
            : $"{Name}, {altitude}, {where}";
    }
}
