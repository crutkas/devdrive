namespace DevDriveCore.Abstractions;

/// <summary>
/// Read-only filesystem probes used by the package-cache and source-location services. Isolated
/// behind an interface so callers can be unit-tested without touching a real disk.
/// </summary>
public interface IFileSystemProbe
{
    /// <summary>True when <paramref name="path"/> is a non-empty path that exists as a directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Computes the total size (bytes) of a directory tree, capped at <paramref name="timeBudget"/>.
    /// Resilient to large/deep trees and access errors (inaccessible entries are skipped, not thrown);
    /// returns the best-effort partial total if the budget or <paramref name="cancellationToken"/> expires.
    /// </summary>
    Task<ulong> GetDirectorySizeAsync(string path, TimeSpan timeBudget, CancellationToken cancellationToken = default);
}
