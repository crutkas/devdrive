using System.Runtime.InteropServices;
using DevDriveCore.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="INativeVolumeApi"/> using P/Invoke: <c>CreateFileW</c> + <c>DeviceIoControl</c>
/// (<c>FSCTL_QUERY_PERSISTENT_VOLUME_STATE</c>) + <c>CloseHandle</c> (via <see cref="SafeFileHandle"/>).
/// </summary>
/// <remarks>
/// <para><b>Empirically verified behavior (unelevated) on Win11 26200:</b></para>
/// <list type="bullet">
///   <item>The FSCTL must target the volume <b>root directory</b> handle (<c>X:\</c>) opened with
///   <c>FILE_FLAG_BACKUP_SEMANTICS</c>. Opening the device path <c>\\.\X:</c> returns
///   ERROR_INVALID_FUNCTION for this FSCTL.</item>
///   <item>Querying with <c>FlagMask = 0xFFFFFFFF</c> works on both NTFS (returns 0x0000) and ReFS
///   (G:\ returns 0x6001 = dev | trusted | short-name-creation-disabled). A mask containing only
///   the dev/trusted bits returns ERROR_INVALID_PARAMETER on NTFS.</item>
///   <item>Both the dev (0x2000) and trusted (0x4000) flags are readable <b>without elevation</b>.</item>
/// </list>
/// </remarks>
public sealed class NativeVolumeApi : INativeVolumeApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_FS_PERSISTENT_VOLUME_INFORMATION
    {
        public uint VolumeFlags;
        public uint FlagMask;
        public uint Version;
        public uint Reserved;
    }

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
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

    /// <inheritdoc />
    public uint? QueryPersistentVolumeState(string volumeRootPath)
    {
        if (string.IsNullOrWhiteSpace(volumeRootPath))
        {
            return null;
        }

        using SafeFileHandle handle = CreateFile(
            volumeRootPath,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var input = new FILE_FS_PERSISTENT_VOLUME_INFORMATION
        {
            Version = 1,
            FlagMask = PersistentVolumeState.QueryAllFlagsMask,
        };
        var output = default(FILE_FS_PERSISTENT_VOLUME_INFORMATION);
        int size = Marshal.SizeOf<FILE_FS_PERSISTENT_VOLUME_INFORMATION>();

        bool ok = DeviceIoControl(
            handle,
            PersistentVolumeState.FsctlQuery,
            ref input, size,
            ref output, size,
            out _, IntPtr.Zero);

        return ok ? output.VolumeFlags : null;
    }
}
