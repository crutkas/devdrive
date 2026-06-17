using CommunityToolkit.Mvvm.ComponentModel;
using DevDriveCore;
using DevDriveCore.Models;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Drives the "Drive health" card: a one-line health summary, a "Healthy" pill, and a capacity bar
/// (used vs total). All values are REAL, projected from the detected <see cref="VolumeInfo"/> and the
/// (elevation-dependent) <see cref="DevDriveTrustInfo"/>. Read-only — it changes nothing.
/// </summary>
public partial class DriveHealthViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial string HeaderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DescriptionText { get; set; } = string.Empty;

    /// <summary>
    /// Honest, specific performance-mode line, e.g. "Performance mode: managed by your organization",
    /// "Performance mode: On — scanning asynchronously", or "Performance mode: Unknown — run as admin to
    /// confirm". Derived by <see cref="DevDriveCore.Services.PerformanceModeEvaluator"/> from the trust /
    /// policy detail (never from attached antivirus filters, which attach in both async and sync modes).
    /// </summary>
    [ObservableProperty]
    public partial string PerformanceModeLineText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HealthPillText { get; set; } = "Healthy";

    [ObservableProperty]
    public partial double CapacityPercent { get; set; }

    [ObservableProperty]
    public partial string CapacityCaption { get; set; } = string.Empty;

    /// <summary>Projects the detected Dev Drive volume (and the derived effective perf mode) into the card.</summary>
    public void Initialize(VolumeInfo dev, DevDriveTrustInfo? trust, EffectivePerformanceMode effective)
    {
        string label = string.IsNullOrWhiteSpace(dev.Label) ? "Dev Drive" : dev.Label;
        HeaderText = $"{dev.DriveLetter}: \u2014 {label}";

        string fs = string.IsNullOrWhiteSpace(dev.FileSystemType) ? "ReFS" : dev.FileSystemType;
        string size = ByteSizeFormatter.Format(dev.SizeBytes);
        string trustText = dev.IsTrusted ? "Trusted" : "Untrusted";

        DescriptionText = $"{fs} \u00B7 {trustText}";
        PerformanceModeLineText = $"Performance mode: {effective.Display}";

        CapacityPercent = dev.UsedFraction * 100d;
        string used = ByteSizeFormatter.Format(dev.UsedBytes);
        string free = ByteSizeFormatter.Format(dev.FreeBytes);
        CapacityCaption = $"{used} used of {size} \u00B7 {free} free";

        HasData = true;
    }
}
