namespace DevDriveCore.Services;

/// <summary>
/// Pure, deterministic math for the unified Performance test suite. Bars show the <em>measured
/// magnitude</em> (longer = larger value): for lower-is-better wall-clock seconds that means the longer
/// bar is the <em>slower</em> drive. The faster drive is marked instead by the accent fill plus the
/// single green "N×" delta (shorter = faster). No I/O, so it is fully unit-testable.
/// </summary>
/// <remarks>
/// Every remaining group is lower-is-better seconds, so sizing bars to the raw value keeps the bar lengths
/// in step with the numbers shown next to them (more seconds → longer bar). <see cref="Speedup"/>,
/// <see cref="IsFavorable"/> and <see cref="Performance"/> still fold both metric directions into the
/// faster-drive "N×" delta and favorable colour — only the bar <em>lengths</em> are raw-magnitude.
/// </remarks>
public static class PerfSuiteMath
{
    /// <summary>
    /// Converts a measured value into "performance" (higher = better) regardless of metric direction.
    /// Higher-is-better metrics pass through; lower-is-better (seconds) invert to <c>1 / value</c>.
    /// Returns <c>0</c> for non-positive / non-finite input so callers can ignore it.
    /// </summary>
    public static double Performance(double value, bool higherIsBetter)
    {
        if (value <= 0d || double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return higherIsBetter ? value : 1d / value;
    }

    /// <summary>
    /// Bar fractions in <c>[0, 1]</c> for <c>(system, dev)</c>, sized to the <em>raw measured magnitude</em>
    /// (<c>value / max</c>) — <b>not</b> performance space. The larger value is <c>1.0</c> (the longest bar);
    /// for lower-is-better seconds that makes the <em>slower</em> drive the longest bar, matching the numbers
    /// shown. The faster drive is marked by the accent fill + the green "N×" delta, not the bar length. Both
    /// are <c>0</c> when neither value is usable. <paramref name="higherIsBetter"/> is accepted for signature
    /// stability but does not affect the bar length (raw magnitude reads the same either way).
    /// </summary>
    public static (double System, double Dev) BarFractions(double systemValue, double devValue, bool higherIsBetter)
    {
        double system = RawMagnitude(systemValue);
        double dev = RawMagnitude(devValue);
        double max = Math.Max(system, dev);
        if (max <= 0d)
        {
            return (0d, 0d);
        }

        return (system / max, dev / max);
    }

    /// <summary>Raw value when usable (positive &amp; finite); <c>0</c> otherwise so callers can ignore it.</summary>
    private static double RawMagnitude(double value) =>
        value > 0d && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 0d;

    /// <summary>
    /// Dev-vs-system speedup (above <c>1.0</c> means the Dev Drive is faster), unified across metric
    /// directions. Returns <c>0</c> when either value is unusable.
    /// </summary>
    public static double Speedup(double systemValue, double devValue, bool higherIsBetter)
    {
        double system = Performance(systemValue, higherIsBetter);
        double dev = Performance(devValue, higherIsBetter);
        return system <= 0d || dev <= 0d ? 0d : dev / system;
    }

    /// <summary>
    /// True when the Dev Drive is at least as fast, judged on the <em>rounded</em> speedup so the colour
    /// matches the shown "×" (e.g. "1.0×" reads as favorable, "0.9×" does not).
    /// </summary>
    public static bool IsFavorable(double speedup) => speedup > 0d && Math.Round(speedup, 1) >= 1.0d;

    /// <summary>Formats a speedup as a one-decimal "1.6×" (or an em dash when not computable).</summary>
    public static string FormatSpeedup(double speedup) =>
        speedup > 0d ? $"{speedup:0.0}\u00D7" : "\u2014";
}
