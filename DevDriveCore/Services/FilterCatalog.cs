namespace DevDriveCore.Services;

/// <summary>
/// One-line descriptions for the Windows minifilters a developer machine actually shows, so the
/// Drives room can say what a filter <i>does</i> rather than only that it exists.
/// </summary>
/// <remarks>
/// <para>
/// These are descriptions of documented Windows components, not readings from the machine. Anything
/// not listed returns an empty string and the room leaves the cell blank: a third-party filter's
/// purpose is not ours to guess, and a wrong description is worse than none.
/// </para>
/// <para>
/// Deliberately not an altitude table. Altitudes are read per machine by
/// <see cref="FilterAltitudeReader"/>, because a hard-coded altitude would be a claim about the
/// user's machine that we did not check.
/// </para>
/// </remarks>
public static class FilterCatalog
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WdFilter"] = "Microsoft Defender real-time scanning",
        ["bindFlt"] = "Bind redirection for app containers and MSIX",
        ["wcifs"] = "Windows container isolation",
        ["CldFlt"] = "Cloud files \u2014 OneDrive placeholders",
        ["FileInfo"] = "File metadata cache used by SuperFetch",
        ["storqosflt"] = "Storage quality-of-service policy",
        ["luafv"] = "UAC file and registry virtualisation",
        ["Wof"] = "Compressed file overlay \u2014 Windows and app file compression",
        ["FileCrypt"] = "Encrypted app data for app containers",
        ["npsvctrig"] = "Named-pipe service trigger",
        ["PrjFlt"] = "Windows Projected File System \u2014 virtual filesystems such as VFS for Git",
        ["bfs"] = "Windows Subsystem for Linux filesystem bridge",
        ["FsDepends"] = "Filesystem dependency tracking for mounted VHDs",
        ["Dfsc"] = "DFS namespace client",
        ["MsSecFlt"] = "Microsoft Defender for Endpoint sensor",
        ["SysmonDrv"] = "Sysinternals Sysmon event logging",
        ["eaCrypt"] = "Encrypting File System filter",
        ["applockerfltr"] = "AppLocker enforcement",
    };

    /// <summary>What the named filter does, or an empty string when we do not recognise it.</summary>
    public static string Describe(string? filterName) =>
        filterName is not null && Descriptions.TryGetValue(filterName.Trim(), out string? description)
            ? description
            : string.Empty;
}
