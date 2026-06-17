using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DevDriveCore.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="INativeVhdApi"/> using P/Invoke into <c>virtdisk.dll</c>
/// (<c>CreateVirtualDisk</c> / <c>OpenVirtualDisk</c> / <c>AttachVirtualDisk</c> /
/// <c>GetVirtualDiskPhysicalPath</c> / <c>DetachVirtualDisk</c>).
/// </summary>
/// <remarks>
/// <para><b>COMPOSED BY THE APP (M8), GATED BEHIND AN EXPLICIT USER CONFIRMATION — BUT NEVER EXERCISED
/// BY THE TEST SUITE.</b> This type is the real backing for <see cref="Services.VhdProvisioner"/>, which
/// the app composes via <see cref="Services.DevDriveCreationService.CreateDefault"/> after the user
/// confirms creation. Every unit test injects a <em>mock</em> <see cref="INativeVhdApi"/>, and the
/// UI-test seam substitutes a SAFE fake provisioner, so this P/Invoke code is never executed by a test —
/// nothing in the test suite ever creates, attaches, or detaches a real virtual disk. Attaching a VHD
/// generally requires elevation.</para>
/// <para>Creation uses <c>CREATE_VIRTUAL_DISK_PARAMETERS</c> Version 1 with the VHDX device type, and
/// attach uses <c>PERMANENT_LIFETIME | NO_DRIVE_LETTER</c> so the surfaced disk survives handle close
/// without auto-assigning a letter (the user later initializes the disk and assigns a letter explicitly).</para>
/// </remarks>
public sealed class NativeVhdApi : INativeVhdApi
{
    // VIRTUAL_STORAGE_TYPE_DEVICE_VHDX and the Microsoft vendor GUID.
    private const uint VirtualStorageTypeDeviceVhdx = 3;
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    private const uint CreateVirtualDiskVersion1 = 1;
    private const uint AttachVirtualDiskVersion1 = 1;

    // VIRTUAL_DISK_ACCESS_MASK
    private const uint VirtualDiskAccessCreate = 0x00100000;
    private const uint VirtualDiskAccessAll = 0x003F0000;

    // CREATE_VIRTUAL_DISK_FLAG
    private const uint CreateVirtualDiskFlagNone = 0x0;
    private const uint CreateVirtualDiskFlagFullPhysicalAllocation = 0x1;

    // ATTACH_VIRTUAL_DISK_FLAG (public virtdisk.h): READ_ONLY = 0x1, NO_DRIVE_LETTER = 0x2,
    // PERMANENT_LIFETIME = 0x4. NO_DRIVE_LETTER is 0x2 (NOT 0x1 — that is READ_ONLY, which would
    // surface the VHDX read-only and let Windows auto-assign a letter, the opposite of intent).
    private const uint AttachVirtualDiskFlagNoDriveLetter = 0x2;
    private const uint AttachVirtualDiskFlagPermanentLifetime = 0x4;

    private const uint OpenVirtualDiskFlagNone = 0x0;
    private const uint DetachVirtualDiskFlagNone = 0x0;
    private const uint ErrorSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREATE_VIRTUAL_DISK_PARAMETERS_V1
    {
        public uint Version;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ParentPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string? SourcePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint CreateVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE virtualStorageType,
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        uint virtualDiskAccessMask,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref CREATE_VIRTUAL_DISK_PARAMETERS_V1 parameters,
        IntPtr overlapped,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE virtualStorageType,
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        uint virtualDiskAccessMask,
        uint flags,
        IntPtr parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref ATTACH_VIRTUAL_DISK_PARAMETERS parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        uint flags,
        uint providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle virtualDiskHandle,
        ref uint diskPathSizeInBytes,
        StringBuilder diskPath);

    /// <inheritdoc />
    public void CreateVirtualDisk(string path, ulong maximumSizeBytes, bool dynamicallyExpanding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VirtualStorageTypeDeviceVhdx,
            VendorId = VendorMicrosoft,
        };
        var parameters = new CREATE_VIRTUAL_DISK_PARAMETERS_V1
        {
            Version = CreateVirtualDiskVersion1,
            UniqueId = Guid.Empty,
            MaximumSize = maximumSizeBytes,
            BlockSizeInBytes = 0,   // 0 == use the provider default
            SectorSizeInBytes = 0,  // 0 == use the provider default
            ParentPath = null,
            SourcePath = null,
        };
        uint flags = dynamicallyExpanding
            ? CreateVirtualDiskFlagNone
            : CreateVirtualDiskFlagFullPhysicalAllocation;

        uint result = CreateVirtualDisk(
            ref storageType, path, VirtualDiskAccessCreate, IntPtr.Zero,
            flags, 0, ref parameters, IntPtr.Zero, out SafeFileHandle handle);

        using (handle)
        {
            ThrowIfFailed(result, nameof(CreateVirtualDisk));
        }
    }

    /// <inheritdoc />
    public string AttachVirtualDisk(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SafeFileHandle handle = OpenVhd(path, VirtualDiskAccessAll);
        var attachParameters = new ATTACH_VIRTUAL_DISK_PARAMETERS
        {
            Version = AttachVirtualDiskVersion1,
            Reserved = 0,
        };
        uint flags = AttachVirtualDiskFlagPermanentLifetime | AttachVirtualDiskFlagNoDriveLetter;

        uint result = AttachVirtualDisk(handle, IntPtr.Zero, flags, 0, ref attachParameters, IntPtr.Zero);
        ThrowIfFailed(result, nameof(AttachVirtualDisk));

        return GetPhysicalPath(handle);
    }

    /// <inheritdoc />
    public void DetachVirtualDisk(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SafeFileHandle handle = OpenVhd(path, VirtualDiskAccessAll);
        uint result = DetachVirtualDisk(handle, DetachVirtualDiskFlagNone, 0);
        ThrowIfFailed(result, nameof(DetachVirtualDisk));
    }

    private static SafeFileHandle OpenVhd(string path, uint accessMask)
    {
        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VirtualStorageTypeDeviceVhdx,
            VendorId = VendorMicrosoft,
        };

        uint result = OpenVirtualDisk(
            ref storageType, path, accessMask, OpenVirtualDiskFlagNone, IntPtr.Zero, out SafeFileHandle handle);
        if (result != ErrorSuccess)
        {
            handle.Dispose();
            throw new Win32Exception((int)result, $"OpenVirtualDisk failed (0x{result:X8}).");
        }

        return handle;
    }

    private static string GetPhysicalPath(SafeFileHandle handle)
    {
        uint sizeInBytes = 1024;
        var buffer = new StringBuilder((int)(sizeInBytes / sizeof(char)));
        uint result = GetVirtualDiskPhysicalPath(handle, ref sizeInBytes, buffer);
        ThrowIfFailed(result, nameof(GetVirtualDiskPhysicalPath));
        return buffer.ToString();
    }

    private static void ThrowIfFailed(uint result, string api)
    {
        if (result != ErrorSuccess)
        {
            throw new Win32Exception((int)result, $"{api} failed (0x{result:X8}).");
        }
    }
}
