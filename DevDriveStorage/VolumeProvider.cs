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
    public long UsedBytes => Math.Max(0, CapacityBytes - FreeBytes);

    /// <summary>Short label for pickers, e.g. "Dev Drive (D:)" or "Windows (C:)".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? DriveLetter : $"{Label} ({DriveLetter})";

    public string ClassificationDisplay => IsDevDrive
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
            long capacity = SafeLong(() => drive.TotalSize);
            long free = SafeLong(() => drive.TotalFreeSpace);

            (bool isDev, bool isTrusted) = QueryDevDriveFlags(root);
            bool isReFS = string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

            return new StorageVolume(
                root, letter, label, fileSystem, capacity, free, isReFS, isDev, isTrusted);
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

    private (bool IsDevDrive, bool IsTrusted) QueryDevDriveFlags(string volumeRootPath)
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
                return (false, false);
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

            if (!ok)
            {
                return (false, false);
            }

            bool isDev = (output.VolumeFlags & DevVolumeFlag) != 0;
            bool isTrusted = (output.VolumeFlags & TrustedVolumeFlag) != 0;
            return (isDev, isTrusted);
        }
        catch (DllNotFoundException)
        {
            return (false, false);
        }
        catch (EntryPointNotFoundException)
        {
            return (false, false);
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

    private static long SafeLong(Func<long> read)
    {
        try
        {
            return Math.Max(0, read());
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
