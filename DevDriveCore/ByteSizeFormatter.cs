using System.Globalization;

namespace DevDriveCore;

/// <summary>
/// Formats byte counts for display using binary (1024-based) units with Windows-style labels
/// (B, KB, MB, GB, TB, PB). Lives in the core so the UI and the unit tests share one implementation.
/// </summary>
public static class ByteSizeFormatter
{
    private const double Step = 1024d;
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>
    /// Formats <paramref name="bytes"/> as a human-readable string, e.g. <c>976.5 GB</c>.
    /// GB and larger units use one decimal place; smaller units are whole numbers.
    /// </summary>
    public static string Format(ulong bytes)
    {
        double size = bytes;
        int unit = 0;
        while (size >= Step && unit < Units.Length - 1)
        {
            size /= Step;
            unit++;
        }

        // One decimal for GB and up (unit >= 3), whole numbers below.
        string format = unit >= 3 ? "0.0" : "0";
        return size.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
