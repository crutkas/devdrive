using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Formats a <see cref="CacheMoveProgress"/> snapshot into a short, live status line for the M4 move
/// UI (e.g. "Copying 12/40 files \u00B7 3.2 MB / 10 MB"). Pure/static so it is shared by the ViewModel
/// and unit-tested without any UI.
/// </summary>
public static class CacheMoveProgressFormatter
{
    /// <summary>Builds the live progress caption; returns a "Preparing…" placeholder before any file is counted.</summary>
    public static string Describe(CacheMoveProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (progress.TotalFiles <= 0)
        {
            return "Preparing\u2026";
        }

        string copied = ByteSizeFormatter.Format((ulong)Math.Max(0L, progress.BytesCompleted));
        string total = ByteSizeFormatter.Format((ulong)Math.Max(0L, progress.TotalBytes));
        return $"Copying {progress.FilesCompleted}/{progress.TotalFiles} files \u00B7 {copied} / {total}";
    }

    /// <summary>Percent complete in [0, 100] for a determinate progress bar.</summary>
    public static double Percent(CacheMoveProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Math.Clamp(progress.Fraction * 100d, 0d, 100d);
    }
}
