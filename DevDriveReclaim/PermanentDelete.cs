using DevDriveStorage.Live;

namespace DevDriveReclaim;

/// <summary>How a permanent delete of a folder tree went.</summary>
/// <param name="Whole">Everything went, the folder too.</param>
/// <param name="EntriesRemoved">Files, links and folders deleted.</param>
/// <param name="BytesFreed">What the deleted files occupied, added up as each went (links count nothing).</param>
/// <param name="EntriesLeft">Files, links and folders that couldn't be deleted (not counting the folders that hold them).</param>
/// <param name="FirstFailure">Why the first of those couldn't be; null when <paramref name="Whole"/>.</param>
internal sealed record PermanentDeleteResult(bool Whole, int EntriesRemoved, long BytesFreed, int EntriesLeft, Exception? FirstFailure);

/// <summary>
/// Deletes a folder tree for good: every file and folder, read-only ones included, and every link as a link.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Directory.Delete(string, bool)"/> stops at the first file it can't delete. git writes every object file
/// read-only, so deleting a repository that way removed its working files and then stopped inside <c>.git</c>: the
/// source was gone, the history stayed, and the run reported nothing removed and nothing freed. So the read-only
/// attribute is cleared on each file and folder before it goes.
/// </para>
/// <para>
/// A junction or symbolic link is removed as a link, never followed, as <see cref="Directory.Delete(string, bool)"/> does:
/// what it points to is somewhere this scan never measured. Its attributes are left alone too, so nothing is set on a
/// link's target.
/// </para>
/// <para>
/// A file that still won't go (another program holds it) doesn't stop the rest: the user agreed to delete all of it, and
/// what's left afterwards is only what can't be deleted yet. The result says how much went, measured as it went (the
/// allocation each file's directory entry reports, as the scan measures it), so a delete that stopped part-way is
/// reported as that, with the bytes it really freed.
/// </para>
/// </remarks>
internal static class PermanentDelete
{
    /// <summary>Deletes <paramref name="folder"/> and everything in it. Never throws for a file or folder that won't go.</summary>
    public static PermanentDeleteResult Folder(string folder)
    {
        var tally = new Tally();
        bool whole = DeleteTree(Path.TrimEndingDirectorySeparator(folder), tally);
        return new PermanentDeleteResult(whole, tally.Removed, tally.Bytes, tally.Left, whole ? null : tally.FirstFailure);
    }

    private static bool DeleteTree(string folder, Tally tally)
    {
        bool whole = true;
        if (FastDirectoryWalker.TryList(folder, out IReadOnlyList<FastDirEntry> entries))
        {
            foreach (FastDirEntry entry in entries)
            {
                string path = Path.Combine(folder, entry.Name);
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    whole &= DeleteTree(path, tally);
                    continue;
                }

                try
                {
                    if (entry.IsDirectory)
                    {
                        // A junction or a linked folder: removing it removes the link, not what it points to.
                        Directory.Delete(path);
                    }
                    else
                    {
                        if (!entry.IsReparsePoint)
                        {
                            Writable(path, entry.Attributes);
                        }

                        File.Delete(path);
                    }

                    tally.Gone(entry.IsReparsePoint ? 0 : entry.AllocatedBytes);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    tally.Fail(exception);
                    whole = false;
                }
            }
        }

        // A folder whose contents didn't all go can't go either; that isn't another failure to report.
        if (!whole)
        {
            return false;
        }

        try
        {
            Writable(folder, File.GetAttributes(folder));
            Directory.Delete(folder);
            tally.Gone(0);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Includes a folder that couldn't be listed: deleting it says why.
            tally.Fail(exception);
            return false;
        }
    }

    private static void Writable(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private sealed class Tally
    {
        public int Removed;
        public long Bytes;
        public int Left;
        public Exception? FirstFailure;

        public void Gone(long bytes)
        {
            Removed++;
            Bytes += bytes;
        }

        public void Fail(Exception exception)
        {
            Left++;
            FirstFailure ??= exception;
        }
    }
}
