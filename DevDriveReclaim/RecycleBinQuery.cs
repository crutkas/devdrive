using System.Runtime.InteropServices;

namespace DevDriveReclaim;

/// <summary>
/// The one declaration of <c>SHQueryRecycleBin</c> in this assembly.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because two copies of an interop signature are two chances to get
/// it wrong and one chance to notice. This subsystem has already lost a process to a struct
/// declaration that was subtly wrong on x64, and the two callers here — the provider that reports
/// how much the bin is holding, and the executor that checks whether an item genuinely reached it —
/// must agree about the layout or they will disagree about reality.
/// <para>
/// <b>No <c>Pack</c>.</b> The header does not pack <c>SHQUERYRBINFO</c> <em>for 64-bit targets</em>:
/// the two <c>__int64</c> fields are 8-byte aligned, so <c>i64Size</c> sits at offset 8 behind four
/// bytes of padding. <c>shellapi.h</c> does apply <c>pshpack1</c> under
/// <c>#if !defined(_WIN64)</c>, so this declaration would need <c>Pack = 1</c> if x86 were ever
/// added to <c>&lt;Platforms&gt;</c>. <c>cbSize</c> is filled from
/// <see cref="Marshal.SizeOf{T}()"/> so the declared size can never drift from the actual layout.
/// </para>
/// <para>
/// <b>Apartment.</b> <see cref="ReclaimExecutor"/> goes to the trouble of a dedicated STA thread
/// because <c>SHFileOperation</c> is documented to need an initialised apartment. These queries
/// deliberately do not, and the distinction is worth stating rather than leaving to be rediscovered:
/// this class is reached from both sides — the executor's STA thread, and the provider's plain
/// thread-pool thread during a scan. It is a read-only query that returns an HRESULT rather than
/// driving shell UI, and <see cref="TryQuery"/> degrades to <c>false</c> on any non-zero result, so
/// the worst case is a Recycle Bin category that reports nothing rather than a wrong number.
/// </para>
/// </remarks>
internal static class RecycleBinQuery
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int CbSize;
        public long Size;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    /// <summary>
    /// Reads what the Recycle Bin on <paramref name="volumeRoot"/> is holding.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing. A volume with no bin, a removable drive that has gone
    /// away, and a sandbox that refuses the call are all ordinary conditions on a real machine, and
    /// none of them is a reason to fail a scan or abandon a delete.
    /// </remarks>
    public static bool TryQuery(string volumeRoot, out long bytes, out long items)
    {
        bytes = 0;
        items = 0;

        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            return false;
        }

        try
        {
            ShQueryRbInfo info = new() { CbSize = Marshal.SizeOf<ShQueryRbInfo>() };

            if (SHQueryRecycleBinW(volumeRoot, ref info) != 0)
            {
                return false;
            }

            bytes = info.Size;
            items = info.NumItems;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How many items the bin serving <paramref name="path"/>'s volume holds, or null when that
    /// cannot be determined.
    /// </summary>
    public static long? TryCountFor(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && TryQuery(root, out _, out long items) ? items : null;
        }
        catch
        {
            return null;
        }
    }
}
