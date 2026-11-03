using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DevDriveStorage.Live;

/// <summary>
/// Win32 helpers for reading the number of bytes a file actually occupies on disk
/// (its allocation) as opposed to its apparent length. The distinction is the whole
/// point of the analyzer: sparse VHDX files and ReFS block-cloned files report a far
/// smaller allocation than their logical length.
/// </summary>
internal static class NativeFileSize
{
    private const uint InvalidFileSize = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "GetCompressedFileSizeW")]
    private static extern uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);

    /// <summary>
    /// Returns the bytes actually allocated on disk for <paramref name="path"/>, or
    /// <see langword="null"/> when the value cannot be read (the caller falls back to
    /// the apparent length).
    /// </summary>
    public static long? TryGetAllocatedBytes(string path)
    {
        uint low = GetCompressedFileSize(path, out uint high);
        if (low == InvalidFileSize)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                return null;
            }
        }

        return ((long)high << 32) | low;
    }

    /// <summary>
    /// Allocated bytes for a file, falling back to <paramref name="apparentBytes"/> when
    /// the allocation query fails. Never throws.
    /// </summary>
    public static long GetAllocatedBytesOrApparent(string path, long apparentBytes)
    {
        try
        {
            return TryGetAllocatedBytes(path) ?? Math.Max(0, apparentBytes);
        }
        catch (Win32Exception)
        {
            return Math.Max(0, apparentBytes);
        }
    }
}
