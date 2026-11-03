using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevDriveStorage;

/// <summary>
/// A fixed volume the analyzer can scan. Physical identity (letter, root path,
/// capacity) is independent of the Dev Drive / ReFS classification flags.
/// </summary>
public sealed record StorageVolume(
    string RootPath,
    string DriveLetter,
    string Label,
    string FileSystem,
    long CapacityBytes,
    long FreeBytes,
    bool IsReFS,
    bool IsDevDrive,
    bool IsTrusted)
{
    /// <summary>
    /// False when the Dev Drive probe could not run — the volume root would not open, or the FSCTL
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, "we asked and the answer was no" and "we could not ask" arrive looking
    /// identical, and a BitLockered or locked ReFS Dev Drive is reported as an ordinary ReFS
    /// volume — a confident claim about the one property this product exists to manage, made on
    /// no evidence at all.
    /// </para>
    /// <para>
    /// Defaults to <see langword="true"/> so every construction site that knows what it is building
    /// — tests, fakes, scenarios — is unaffected; only the live probe clears it. Same shape, and the
    /// same reasoning, as <c>VolumeInfo.IsDevDriveStateKnown</c> in DevDriveCore.
    /// </para>
    /// </remarks>
    public bool IsDevDriveStateKnown { get; init; } = true;

    /// <summary>
    /// False when capacity or free space could not be read. <see cref="CapacityBytes"/> and
    /// <see cref="FreeBytes"/> are 0 in that case, which is also a legitimate answer to a different
    /// question — hence the flag rather than a sentinel.
    /// </summary>
    public bool IsSizeKnown { get; init; } = true;

    public long UsedBytes => Math.Max(0, CapacityBytes - FreeBytes);

    /// <summary>Short label for pickers, e.g. "Dev Drive (D:)" or "Windows (C:)".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? DriveLetter : $"{Label} ({DriveLetter})";

    /// <summary>
    /// What this volume is, in one phrase. Never asserts a Dev Drive verdict the probe did not
    /// actually return: an unread volume names its filesystem and says the rest is unknown, because
    /// "ReFS volume" reads as a finding rather than as a gap.
    /// </summary>
    public string ClassificationDisplay => !IsDevDriveStateKnown
        ? $"{FileSystem} · Dev Drive unknown"
        : IsDevDrive
            ? IsTrusted ? "Dev Drive · trusted" : "Dev Drive"
            : IsReFS ? "ReFS volume" : FileSystem;
}

/// <summary>
/// Enumerates fixed volumes. Abstracted so the prototype and tests can substitute a
/// deterministic list instead of touching the developer's real disks.
/// </summary>
public interface IVolumeProvider
{
    IReadOnlyList<StorageVolume> GetFixedVolumes();
}

/// <summary>
/// Real volume enumeration via <see cref="DriveInfo"/> plus the public
/// <c>FSCTL_QUERY_PERSISTENT_VOLUME_STATE</c> control code for Dev Drive detection.
/// UI-agnostic: this lives in the reusable library and only depends on .NET + Win32.
/// </summary>
public sealed class SystemVolumeProvider : IVolumeProvider
{
    // Public winioctl.h constants (verified against the Windows SDK header).
    private const uint DevVolumeFlag = 0x00002000;
    private const uint TrustedVolumeFlag = 0x00004000;
    private const uint FsctlQueryPersistentVolumeState = 0x9023C;
    private const uint QueryAllFlagsMask = 0xFFFFFFFF;

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_FS_PERSISTENT_VOLUME_INFORMATION
    {
        public uint VolumeFlags;
        public uint FlagMask;
        public uint Version;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref FILE_FS_PERSISTENT_VOLUME_INFORMATION lpInBuffer, int nInBufferSize,
        ref FILE_FS_PERSISTENT_VOLUME_INFORMATION lpOutBuffer, int nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    public IReadOnlyList<StorageVolume> GetFixedVolumes()
    {
        var volumes = new List<StorageVolume>();
        foreach (DriveInfo drive in SafeGetDrives())
        {
            StorageVolume? volume = TryDescribe(drive);
            if (volume is not null)
            {
                volumes.Add(volume);
            }
        }

        return volumes
            .OrderBy(volume => volume.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DriveInfo[] SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private StorageVolume? TryDescribe(DriveInfo drive)
    {
        try
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                return null;
            }

            string root = drive.RootDirectory.FullName;
            string letter = root.TrimEnd('\\');
            string fileSystem = SafeString(() => drive.DriveFormat, "Unknown");
            string label = SafeString(() => drive.VolumeLabel, string.Empty);
            long? capacity = SafeLong(() => drive.TotalSize);
            long? free = SafeLong(() => drive.TotalFreeSpace);

            uint? flags = QueryPersistentVolumeState(root);
            bool isReFS = string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

            return new StorageVolume(
                root,
                letter,
                label,
                fileSystem,
                capacity ?? 0,
                free ?? 0,
                isReFS,
                (flags & DevVolumeFlag) != 0,
                (flags & TrustedVolumeFlag) != 0)
            {
                IsDevDriveStateKnown = flags.HasValue,
                IsSizeKnown = capacity.HasValue && free.HasValue,
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the volume's persistent state flags, or null when the probe could not run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is not "no flags are set" — it is "nobody asked the volume". A handle that will not
    /// open (BitLocker locked, permissions, a volume that vanished between enumeration and probe)
    /// and an FSCTL that refuses both land here, and both are indistinguishable from a plain NTFS
    /// answer of 0x0000 if they are folded into a bool.
    /// </para>
    /// <para>
    /// Mirrors <c>NativeVolumeApi.QueryPersistentVolumeState</c> in DevDriveCore, which returns
    /// <c>uint?</c> for the same reason. The declaration is duplicated rather than shared because
    /// this library deliberately depends on nothing but .NET and Win32 — see the class remarks.
    /// </para>
    /// </remarks>
    private uint? QueryPersistentVolumeState(string volumeRootPath)
    {
        try
        {
            using SafeFileHandle handle = CreateFile(
                volumeRootPath,
                FileReadAttributes,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return null;
            }

            var input = new FILE_FS_PERSISTENT_VOLUME_INFORMATION
            {
                Version = 1,
                FlagMask = QueryAllFlagsMask,
            };
            var output = default(FILE_FS_PERSISTENT_VOLUME_INFORMATION);
            int size = Marshal.SizeOf<FILE_FS_PERSISTENT_VOLUME_INFORMATION>();

            bool ok = DeviceIoControl(
                handle,
                FsctlQueryPersistentVolumeState,
                ref input, size,
                ref output, size,
                out _, IntPtr.Zero);

            return ok ? output.VolumeFlags : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string SafeString(Func<string> read, string fallback)
    {
        try
        {
            string value = read();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch (IOException)
        {
            return fallback;
        }
        catch (UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Reads a size, or null when it could not be read. Null rather than 0, because 0 is a
    /// legitimate answer to a different question — a volume with nothing free reads exactly the
    /// same as one nobody could measure.
    /// </summary>
    private static long? SafeLong(Func<long> read)
    {
        try
        {
            return Math.Max(0, read());
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
