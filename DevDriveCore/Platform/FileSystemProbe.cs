using System.Diagnostics;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IFileSystemProbe"/>. Directory sizing uses <see cref="DirectoryInfo"/> enumeration
/// (which carries the file length cached from the directory scan, avoiding a stat per file) and is
/// bounded by a wall-clock budget so a huge cache (e.g. a 200k-file <c>.nuget</c> tree) can never
/// hang the UI. Access errors are swallowed per-entry rather than thrown.
/// </summary>
public sealed class FileSystemProbe : IFileSystemProbe
{
    /// <inheritdoc />
    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <inheritdoc />
    public Task<ulong> GetDirectorySizeAsync(string path, TimeSpan timeBudget, CancellationToken cancellationToken = default) =>
        Task.Run(() => ComputeSize(path, timeBudget, cancellationToken), cancellationToken);

    private static ulong ComputeSize(string path, TimeSpan timeBudget, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0UL;
        }

        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(path);
            if (!root.Exists)
            {
                return 0UL;
            }
        }
        catch
        {
            return 0UL;
        }

        var stopwatch = Stopwatch.StartNew();
        ulong total = 0UL;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested || stopwatch.Elapsed > timeBudget)
            {
                break;
            }

            DirectoryInfo current = stack.Pop();

            try
            {
                foreach (FileInfo file in current.EnumerateFiles())
                {
                    if (ct.IsCancellationRequested || stopwatch.Elapsed > timeBudget)
                    {
                        break;
                    }

                    try
                    {
                        total += (ulong)Math.Max(0L, file.Length);
                    }
                    catch
                    {
                        // Skip files we can't stat (locked, denied, reparse, etc.).
                    }
                }
            }
            catch
            {
                // Skip directories we can't enumerate.
            }

            try
            {
                foreach (DirectoryInfo sub in current.EnumerateDirectories())
                {
                    stack.Push(sub);
                }
            }
            catch
            {
                // Skip directories we can't enumerate.
            }
        }

        return total;
    }
}
