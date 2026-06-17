namespace DevDriveCore.Platform;

/// <summary>
/// Public <c>winioctl.h</c> constants for Dev Drive detection via
/// <c>FSCTL_QUERY_PERSISTENT_VOLUME_STATE</c>.
/// </summary>
/// <remarks>
/// <para>
/// All values VERIFIED against the Windows SDK header on this machine:
/// <c>C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\winioctl.h</c>.
/// </para>
/// <list type="bullet">
///   <item><c>#define PERSISTENT_VOLUME_STATE_DEV_VOLUME      (0x00002000)</c></item>
///   <item><c>#define PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME  (0x00004000)</c> —
///   the SDK names this <c>..._TRUSTED_VOLUME</c> (not <c>..._TRUSTED_DEV_VOLUME</c>); the header
///   comments it is "set only when PERSISTENT_VOLUME_STATE_DEV_VOLUME is set".</item>
///   <item><c>FSCTL_QUERY_PERSISTENT_VOLUME_STATE = CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, 143,
///   METHOD_BUFFERED=0, FILE_ANY_ACCESS=0) = 0x9023C</c>.</item>
/// </list>
/// </remarks>
public static class PersistentVolumeState
{
    /// <summary>PERSISTENT_VOLUME_STATE_DEV_VOLUME — the "this is a Dev Drive" flag.</summary>
    public const uint DevVolume = 0x00002000;

    /// <summary>PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME — set only on trusted Dev Drives.</summary>
    public const uint TrustedVolume = 0x00004000;

    /// <summary>The control code for FSCTL_QUERY_PERSISTENT_VOLUME_STATE (0x9023C).</summary>
    public const uint FsctlQuery = 0x9023C;

    /// <summary>
    /// FlagMask to pass on input. We query ALL flags (0xFFFFFFFF): NTFS rejects a mask that
    /// contains only the dev/trusted bits with ERROR_INVALID_PARAMETER, whereas 0xFFFFFFFF is
    /// portable across NTFS and ReFS (verified empirically: C:\ -> 0x0000, G:\ -> 0x6001).
    /// </summary>
    public const uint QueryAllFlagsMask = 0xFFFFFFFF;
}
