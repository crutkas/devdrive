using DevDriveCore;
using DevDriveCore.Models;
using Microsoft.UI.Xaml;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One drive card on Home or Dev Drive: what is used, what is free, and the total.
/// </summary>
/// <remarks>
/// Built fresh on every refresh rather than updated in place; a rebuild is cheaper to reason about
/// than several partial updates.
/// </remarks>
public sealed class DriveCardViewModel
{
    /// <param name="idPrefix">Prefixes the automation ids, so each page's cards stay distinct.</param>
    public DriveCardViewModel(VolumeRowViewModel row, double lowFreeFraction, string idPrefix = "Overview")
    {
        Row = row;
        VolumeInfo volume = row.Volume;

        Title = row.Header;
        Glyph = volume.IsDevDrive ? "\uEC7A" : "\uEDA2";
        Subtitle = $"{row.FileSystemType}  ·  {Role(volume)}";

        IsSizeKnown = volume.IsSizeKnown && volume.SizeBytes > 0;
        ulong size = volume.SizeBytes;
        ulong free = Math.Min(volume.FreeBytes, size);
        ulong used = size - free;

        UsedWidth = new GridLength(used, GridUnitType.Star);
        FreeWidth = new GridLength(free, GridUnitType.Star);

        FreeText = IsSizeKnown ? $"{ByteSizeFormatter.Format(free)} free" : "Size unavailable";
        OfText = IsSizeKnown ? $"of {ByteSizeFormatter.Format(size)}" : string.Empty;
        UsedText = $"{ByteSizeFormatter.Format(used)} used";

        double freeFraction = IsSizeKnown ? (double)free / size : 1d;
        IsLow = IsSizeKnown && freeFraction < lowFreeFraction;
        LowText = $"Low on space · {freeFraction:P0} free";

        // "Dev Drive" alone on a trusted volume; an untrusted one is the state worth noticing, so it
        // says so — the antivirus filter still scans it inline.
        HasBadge = volume.IsDevDrive || volume.IsVhd;
        BadgeText = volume.IsDevDrive
            ? volume.IsTrusted ? "Dev Drive" : "Dev Drive · Untrusted"
            : "Virtual disk";

        string id = volume.DriveLetter is char letter
            ? letter.ToString()
            : (string.IsNullOrWhiteSpace(volume.Label) ? "Volume" : volume.Label.Replace(' ', '_'));
        AutomationId = $"{idPrefix}Drive_{id}";
        ActionAutomationId = $"{idPrefix}DriveAction_{id}";

        AutomationName = IsSizeKnown
            ? $"{row.AutomationName}. {FreeText} {OfText}. {UsedText}."
                + (IsLow ? $" {LowText}." : string.Empty)
            : row.AutomationName;
    }

    public VolumeRowViewModel Row { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Glyph { get; }

    public bool IsSizeKnown { get; }

    /// <summary>Used space, as a star width for the capacity bar.</summary>
    public GridLength UsedWidth { get; }

    public GridLength FreeWidth { get; }

    public string FreeText { get; }

    public string OfText { get; }

    public string UsedText { get; }

    /// <summary>True below the low free-space threshold from Settings.</summary>
    public bool IsLow { get; }

    public string LowText { get; }

    public bool HasBadge { get; }

    public string BadgeText { get; }

    public string AutomationId { get; }

    public string ActionAutomationId { get; }

    public string AutomationName { get; }

    private static string Role(VolumeInfo volume)
    {
        if (volume.IsDevDrive)
        {
            return volume.IsVhd ? "Dev Drive on a virtual disk" : "Dev Drive";
        }

        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool isSystem = volume.DriveLetter is char letter
            && system.Length > 0
            && char.ToUpperInvariant(system[0]) == char.ToUpperInvariant(letter);

        return isSystem ? "Windows drive" : volume.IsVhd ? "Virtual disk" : "Local disk";
    }
}
