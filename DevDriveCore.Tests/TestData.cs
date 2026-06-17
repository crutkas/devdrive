using DevDriveCore.Abstractions;

namespace DevDriveCore.Tests;

/// <summary>Builders and realistic fixtures shared across the unit tests.</summary>
internal static class TestData
{
    public static StorageVolumeRecord Volume(
        char? letter, string fileSystem, string label = "",
        uint driveType = 3, ulong size = 0, ulong free = 0, string? path = null) => new()
    {
        DriveLetter = letter,
        FileSystem = fileSystem,
        Label = label,
        DriveType = driveType,
        SizeBytes = size,
        FreeBytes = free,
        Path = path,
    };

    public static StoragePartitionRecord Partition(char? letter, uint diskNumber) => new()
    {
        DriveLetter = letter,
        DiskNumber = diskNumber,
    };

    public static StorageDiskRecord Disk(uint number, ushort busType, string? location = null, string? model = null) => new()
    {
        Number = number,
        BusType = busType,
        Location = location,
        Model = model,
    };

    // ---- Realistic fsutil devdrv query fixtures ------------------------------------------------
    // NOTE: the exact elevated label strings are unverified (fsutil devdrv query needs elevation;
    // Microsoft Learn documents no literal sample). These follow fsutil's "Key : Value" house
    // style and exercise the keyword-tolerant parser. The access-denied fixture IS the real,
    // captured unelevated output from this machine.

    public const string FsutilTrusted =
        "Developer volume                                  : Yes\r\n" +
        "Developer volume trusted                          : Yes\r\n" +
        "Antivirus filter allowed on this developer volume : No\r\n" +
        "Filters allowed on this developer volume          : PrjFlt, bindFlt, wcifs, FileInfo, Wof, MsSecFlt, WdFilter\r\n" +
        "Filters currently attached                        : FileInfo, Wof, WdFilter\r\n";

    public const string FsutilUntrusted =
        "Developer volume                                  : Yes\r\n" +
        "Developer volume trusted                          : No\r\n" +
        "Antivirus filter allowed on this developer volume : Yes\r\n" +
        "Filters currently attached                        : FileInfo, Wof, WdFilter, MsSecFlt, luafv\r\n";

    public const string FsutilSentenceForm =
        "This is a trusted developer volume.\r\n" +
        "Filters currently attached: FileInfo, Wof\r\n";

    public const string FsutilNotDevDrive =
        "Developer volume : No\r\n";

    // A managed PC where group policy forces the antivirus filter on. AV filter allowed => performance
    // mode OFF, and the "by group policy" phrase => the user cannot flip it locally. Models the shape
    // described by fsutil on policy-protected machines ("…protected by antivirus filter, by group policy").
    public const string FsutilPolicyEnforced =
        "Developer volume                                  : Yes\r\n" +
        "Developer volume trusted                          : Yes\r\n" +
        "Antivirus filter allowed on this developer volume : Yes\r\n" +
        "This developer volume is protected by antivirus filter, by group policy.\r\n" +
        "Filters currently attached                        : FileInfo, Wof, WdFilter, MsSecFlt\r\n";

    // Real, captured unelevated output (see session probe on Win11 26200).
    public const string FsutilAccessDenied =
        "Failed to open the volume.\r\nError 5: Access is denied.\r\n";
}
