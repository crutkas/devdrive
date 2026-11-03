using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DevDriveStorage.Tests;

/// <summary>
/// Builds real, disposable directory trees under <see cref="Path.GetTempPath"/> for the
/// live scanner tests. Nothing here touches the developer's actual data: every path is
/// created below a unique temp root and deleted in <see cref="Dispose"/>.
/// </summary>
internal sealed class LiveScanFixture : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x80;
    private const uint FsctlSetSparse = 0x000900C4;

    private readonly List<string> _deniedDirectories = [];

    public LiveScanFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "devdrive-livescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(params string[] segments)
    {
        string path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a plain file of exactly <paramref name="bytes"/> apparent length.</summary>
    public string File(string relativePath, long bytes)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(bytes);
        return path;
    }

    /// <summary>
    /// Creates a sparse file whose apparent length is <paramref name="apparentBytes"/> but
    /// which allocates (almost) nothing on disk — the sparse/CoW case the analyzer exists for.
    /// </summary>
    public string SparseFile(string relativePath, long apparentBytes)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SafeFileHandle handle = CreateFile(
            path, GenericRead | GenericWrite, 0, IntPtr.Zero, CreateAlways, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Could not create sparse file (error {Marshal.GetLastWin32Error()}).");
        }

        if (!DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException($"FSCTL_SET_SPARSE failed (error {Marshal.GetLastWin32Error()}).");
        }

        if (!SetFilePointerEx(handle, apparentBytes, out _, 0) || !SetEndOfFile(handle))
        {
            throw new IOException($"Could not size sparse file (error {Marshal.GetLastWin32Error()}).");
        }

        return path;
    }

    /// <summary>Creates a directory junction (reparse point) at <paramref name="linkRelative"/>.</summary>
    public string Junction(string linkRelative, string target)
    {
        string link = Path.Combine(Root, linkRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new IOException("Could not start mklink.");
        process.WaitForExit(10_000);
        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new IOException("mklink /J failed: " + process.StandardError.ReadToEnd());
        }

        return link;
    }

    /// <summary>Denies the current user the right to list <paramref name="path"/>.</summary>
    public void DenyListing(string path)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("No current user SID.");
        var info = new DirectoryInfo(path);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData | FileSystemRights.Traverse,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        info.SetAccessControl(security);
        _deniedDirectories.Add(path);
    }

    public void Dispose()
    {
        foreach (string path in _deniedDirectories)
        {
            TryRemoveDenyRule(path);
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup: a locked handle should never fail the test run.
        }
    }

    private static void TryRemoveDenyRule(string path)
    {
        try
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
            var info = new DirectoryInfo(path);
            DirectorySecurity security = info.GetAccessControl();
            security.RemoveAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.ListDirectory | FileSystemRights.ReadData | FileSystemRights.Traverse,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny));
            info.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Ignore: cleanup is best-effort.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEndOfFile(SafeFileHandle hFile);
}
