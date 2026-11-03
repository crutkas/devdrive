using Microsoft.Win32;

namespace DevDriveCore.Services;

/// <summary>
/// Reads a minifilter's altitude — its fixed position in the I/O path — from the filter's own service
/// registration.
/// </summary>
/// <remarks>
/// <para>
/// <c>fltmc filters</c> is the usual way to get this and it needs elevation, which would make the
/// altitude column disappear on the same machines the filter list already needs a UAC prompt for.
/// The registration under <c>HKLM\SYSTEM\CurrentControlSet\Services\{name}\Instances</c> carries the
/// same number and any user can read it, so the altitude costs nothing once we know a filter's name.
/// </para>
/// <para>
/// A filter registers one or more named instances and nominates a <c>DefaultInstance</c>. We prefer
/// that one and fall back to the first instance present, because a filter with instances but no
/// nominated default is legal and still has a real altitude. A filter with no <c>Instances</c> key at
/// all is not an error — it means nothing is registered on this machine, and the caller shows an em
/// dash rather than inventing a number.
/// </para>
/// <para>
/// Altitudes are strings in the registry and are not always integers (they are decimal-ish strings
/// like <c>"189900"</c>, occasionally with a fractional part), so they are parsed as
/// <see cref="double"/> with the invariant culture. Parsing them as <c>int</c> would silently drop
/// the filters that use a fraction to slot between two allocated altitudes.
/// </para>
/// </remarks>
public static class FilterAltitudeReader
{
    private const string ServicesKeyPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>
    /// The altitude registered for <paramref name="filterName"/>, or null when the filter has no
    /// readable instance registration on this machine.
    /// </summary>
    public static double? Read(string filterName)
    {
        if (string.IsNullOrWhiteSpace(filterName))
        {
            return null;
        }

        try
        {
            using RegistryKey? instances = Registry.LocalMachine.OpenSubKey(
                $@"{ServicesKeyPath}\{filterName}\Instances");
            if (instances is null)
            {
                return null;
            }

            string? preferred = instances.GetValue("DefaultInstance") as string;
            if (!string.IsNullOrWhiteSpace(preferred)
                && ReadInstanceAltitude(instances, preferred) is double fromDefault)
            {
                return fromDefault;
            }

            foreach (string instanceName in instances.GetSubKeyNames())
            {
                if (ReadInstanceAltitude(instances, instanceName) is double altitude)
                {
                    return altitude;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A filter we are not allowed to read is reported as unknown rather than as a failure —
            // one unreadable filter must not cost us the whole list.
            return null;
        }
    }

    private static double? ReadInstanceAltitude(RegistryKey instances, string instanceName)
    {
        using RegistryKey? instance = instances.OpenSubKey(instanceName);
        return instance?.GetValue("Altitude") is string raw ? ParseAltitude(raw) : null;
    }

    /// <summary>
    /// Parses a registry altitude string. Exposed so the parsing rule is testable without a machine
    /// that happens to have the right filters installed.
    /// </summary>
    public static double? ParseAltitude(string? raw) =>
        double.TryParse(
            raw?.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value)
            ? value
            : null;
}
