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
        UsedPercent = volume.UsedFraction * 100d;

        // Stable automation ids so UI tests can locate this specific row and its badge.
        string idSuffix = volume.DriveLetter is char dl ? dl.ToString() : (label.Replace(' ', '_'));
        AutomationId = $"VolumeCard_{idSuffix}";
        DevDriveBadgeAutomationId = $"DevDriveBadge_{idSuffix}";

        AutomationName = BuildAutomationName(label, fileSystem, size, free);
    }

    public VolumeInfo Volume { get; }

    public string DriveLetterText { get; }

    public string Header { get; }

    public string Description { get; }

    public string FileSystemType { get; }

    public double UsedPercent { get; }

    public string GlyphCode { get; } = "\uEDA2"; // Segoe Fluent Icons: Hard drive

    public bool IsDevDrive { get; }

    public bool IsTrusted { get; }

    public bool IsVhd { get; }

    public string? VhdFilePath { get; }

    public bool HasVhdPath => !string.IsNullOrEmpty(VhdFilePath);

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
}
