using DevDriveCore;
using DevDriveCore.Models;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Immutable presentation wrapper around a <see cref="VolumeInfo"/> for one row in the volumes
/// list. All display strings are computed once (the underlying data is a read-only snapshot in
/// Milestone 1), so the bindings are <c>OneTime</c>.
/// </summary>
public sealed class VolumeRowViewModel
{
    public VolumeRowViewModel(VolumeInfo volume)
    {
        Volume = volume;
        IsDevDrive = volume.IsDevDrive;
        IsTrusted = volume.IsTrusted;
        IsVhd = volume.IsVhd;
        VhdFilePath = volume.VhdFilePath;

        DriveLetterText = volume.DriveLetter is char c ? $"{c}:" : "—";

        string label = string.IsNullOrWhiteSpace(volume.Label)
            ? (volume.DriveLetter is null ? "System volume" : "Local disk")
            : volume.Label;
        Header = volume.DriveLetter is char letter ? $"{letter}:  {label}" : label;

        string fileSystem = string.IsNullOrWhiteSpace(volume.FileSystemType) ? "Unknown" : volume.FileSystemType;
        string size = ByteSizeFormatter.Format(volume.SizeBytes);
        string free = ByteSizeFormatter.Format(volume.FreeBytes);
        Description = $"{fileSystem}  ·  {free} free of {size}";

        FileSystemType = fileSystem;

        CapacityText = size;
        FreeText = free;
        DevDrivePillText = volume.IsDevDrive ? (volume.IsTrusted ? "Trusted" : "Untrusted") : "No";

        // A Dev Drive we could not confirm the trust of is not the good state and not the neutral one
        // — it is the case worth looking at, which is what the warn pill is for.
        DevDrivePillKind = !volume.IsDevDrive ? "mute" : volume.IsTrusted ? "dev" : "system";

        NotesText = BuildNotes(volume);

        // Stable automation ids so UI tests can locate this specific row and its badge. Deliberately
        // not "VolumeCard_" — the context strip's VolumeCard control already owns that prefix, and the
        // Drives room shows the strip and this table at once, so sharing it would make every lookup
        // ambiguous.
        string idSuffix = volume.DriveLetter is char dl ? dl.ToString() : (label.Replace(' ', '_'));
        AutomationId = $"VolumeRow_{idSuffix}";
        DevDriveBadgeAutomationId = $"DevDriveBadge_{idSuffix}";

        AutomationName = BuildAutomationName(label, fileSystem, size, free);
    }

    public VolumeInfo Volume { get; }

    public string DriveLetterText { get; }

    public string Header { get; }

    public string Description { get; }

    public string FileSystemType { get; }

    /// <summary>The volume's total size. Its own column in the volumes table.</summary>
    public string CapacityText { get; }

    /// <summary>Free bytes. Beside capacity rather than folded into a caption, so the two compare.</summary>
    public string FreeText { get; }

    /// <summary>"Trusted" / "Untrusted" / "No" — the volume's Dev Drive standing in one word.</summary>
    public string DevDrivePillText { get; }

    /// <summary>Pill kind for <see cref="DevDrivePillText"/>: dev, system (needs a look) or mute.</summary>
    public string DevDrivePillKind { get; }

    /// <summary>
    /// The one thing worth saying about this volume that no other column carries — what it is for, or
    /// what it is backed by. Empty when there is nothing true and useful to add.
    /// </summary>
    public string NotesText { get; }

    /// <summary>
    /// Index into the room's category swatch ramp. Assigned by the loader in row order so the volume
    /// colours line up with the capacity strip above the table.
    /// </summary>
    public int CategoryIndex { get; set; }

    public bool IsDevDrive { get; }

    public bool IsTrusted { get; }

    public bool IsVhd { get; }

    public string? VhdFilePath { get; }

    public string AutomationId { get; }

    public string DevDriveBadgeAutomationId { get; }

    public string AutomationName { get; }

    private string BuildAutomationName(string label, string fileSystem, string size, string free)
    {
        string head = Volume.DriveLetter is char c ? $"Drive {c}, {label}" : label;
        string badges = IsDevDrive ? ", Dev Drive" : string.Empty;
        badges += IsTrusted ? ", Trusted" : string.Empty;
        badges += IsVhd ? ", VHD backed" : string.Empty;
        return $"{head}, {fileSystem}, {free} free of {size}{badges}";
    }

    /// <summary>
    /// The volume's one-line "what is this for", for the notes column.
    /// </summary>
    /// <remarks>
    /// Deliberately not a shrinkable-space figure. Free space is not shrinkable space — a shrink can
    /// only use free space that is contiguous and at the end of the volume — so printing free bytes
    /// under the word "shrink" would be a number the user could act on and we could not stand behind.
    /// The Create room does that arithmetic properly, with the guard rails attached.
    /// </remarks>
    private static string BuildNotes(VolumeInfo volume)
    {
        if (volume.IsDevDrive)
        {
            return volume.IsVhd
                ? $"Dev Drive on a virtual disk \u00B7 {volume.VhdFilePath}"
                : "Dev Drive on a real partition \u00B7 no host filesystem in the way";
        }

        if (IsSystemVolume(volume))
        {
            return "System volume \u00B7 Windows, installed apps and your profile";
        }

        if (volume.IsVhd)
        {
            return $"Virtual disk \u00B7 {volume.VhdFilePath}";
        }

        return string.Empty;
    }

    /// <summary>True for the volume Windows itself is installed on.</summary>
    private static bool IsSystemVolume(VolumeInfo volume)
    {
        if (volume.DriveLetter is not char letter)
        {
            return false;
        }

        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return system.Length > 0 && char.ToUpperInvariant(system[0]) == char.ToUpperInvariant(letter);
    }
}
