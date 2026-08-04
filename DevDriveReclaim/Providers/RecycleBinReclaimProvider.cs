using System.Runtime.InteropServices;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Reports what each volume's Recycle Bin is holding, via <c>SHQueryRecycleBinW</c>.
/// </summary>
/// <remarks>
/// Queried per volume rather than as one machine-wide number because the Recycle Bin is physically
/// per-volume: emptying it frees space on the drive the files came from, and a single total would
/// promise space on a drive that never gets any.
/// <para>
/// This is the only provider whose candidates cannot themselves be recycled — they are already in
/// the bin, so <see cref="ReclaimCandidate.SupportsRecycleBin"/> is false and deletion is final.
/// That is stated in the recovery hint rather than hidden.
/// </para>
/// </remarks>
public sealed class RecycleBinReclaimProvider : IReclaimProvider
{
    public static readonly ReclaimCategory ReclaimCategory = new(
        "recycle-bin",
        "Recycle Bin",
        "Files you already deleted. They still occupy the drive they came from until the bin is emptied.",
        "\uE74D",
        order: 0);

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var candidates = new List<ReclaimCandidate>();

        foreach (string volumeRoot in context.VolumeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryQuery(volumeRoot, out long bytes, out long items) || bytes <= 0)
            {
                continue;
            }

            string letter = volumeRoot.TrimEnd('\\', '/');
            candidates.Add(new ReclaimCandidate(
                ReclaimCategory.Id,
                volumeRoot,
                $"Recycle Bin on {letter}",
                bytes,
                ReclaimRisk.Safe,
                "These files are already deleted. Emptying the bin is the step that actually returns " +
                    "the space to the drive.",
                "Nothing — emptying the Recycle Bin is final. Restore anything you still want first.",
                itemCount: (int)Math.Min(items, int.MaxValue),
                detail: $"{items:N0} item{(items == 1 ? string.Empty : "s")} waiting on {letter}",
                supportsRecycleBin: false));
        }

        progress?.Report(new ReclaimScanProgress(
            ReclaimCategory.Id, "Recycle Bin checked", candidates.Count));

        return Task.FromResult<IReadOnlyList<ReclaimCandidate>>(candidates);
    }

    private static bool TryQuery(string volumeRoot, out long bytes, out long items)
    {
        bytes = 0;
        items = 0;

        try
        {
            var info = new ShQueryRbInfo { CbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            int hr = SHQueryRecycleBinW(volumeRoot, ref info);
            if (hr != 0)
            {
                return false;
            }

            bytes = info.Size;
            items = info.NumItems;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int CbSize;
        public long Size;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);
}
