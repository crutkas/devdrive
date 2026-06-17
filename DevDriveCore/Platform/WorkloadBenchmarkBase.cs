using System.Diagnostics;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Shared scaffolding for the real-workload benchmarks: bounded temp fixtures, a per-drive working
/// area, a per-drive package-cache directory (re-populated cold each iteration), and robust cleanup.
/// </summary>
/// <remarks>
/// <para><b>SAFETY:</b> every directory the benchmark creates is tracked and deleted in
/// <see cref="Cleanup"/> (called from the orchestrator's <c>finally</c>), and each measured iteration
/// deletes its own working copy. Fixtures are small and a free-space guard skips the benchmark when a
/// drive is low. No machine-config, env-var, or partition/format changes are ever made — only files
/// under the seed (<c>%TEMP%</c>) and per-drive <c>DevDriveManagerWorkloadBench</c> folders.</para>
/// <para><b>F9 (ownership):</b> a per-drive bench <c>&lt;Slug&gt;</c> folder is only ever deleted
/// wholesale when this benchmark created it (stamping a <see cref="BenchOwnershipMarkerFileName"/>
/// receipt) or a prior run left that receipt. A predictable pre-existing bench folder that lacks the
/// marker is treated as foreign user data: it is never recursively deleted — only the sub-directories
/// the benchmark itself creates under it are removed, and the folder is dropped only if it ends up
/// empty.</para>
/// </remarks>
public abstract class WorkloadBenchmarkBase : IWorkloadBenchmark
{
    // F11: directories THIS benchmark created and may delete wholesale (its own Slug subfolders).
    private readonly HashSet<string> _ownedDirectories = new(StringComparer.OrdinalIgnoreCase);

    // F11: shared FIXED PARENTS (e.g. the per-drive bench root and the %TEMP% seed root) that sibling
    // benchmarks also use. These are NEVER deleted wholesale — only removed if they end up empty.
    private readonly HashSet<string> _sharedParents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// F9: small ownership receipt dropped into a per-drive bench <c>&lt;Slug&gt;</c> directory this
    /// benchmark creates. Its presence marks the folder as app-managed, so a later run (or
    /// <see cref="Cleanup"/>) may delete it wholesale; a predictable pre-existing bench folder WITHOUT it
    /// is treated as foreign user data and is never recursively deleted.
    /// </summary>
    public const string BenchOwnershipMarkerFileName = ".devdrivemanager-bench";

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    protected WorkloadBenchmarkBase(IWorkloadProcessRunner runner) =>
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>The process runner used to launch tools.</summary>
    protected IWorkloadProcessRunner Runner { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Detail { get; }

    /// <inheritdoc />
    public WorkloadProfile Profile { get; set; } = WorkloadProfile.Quick;

    /// <inheritdoc />
    public abstract string RequiredTool { get; }

    /// <summary>Folder-safe identifier used for this benchmark's fixture/working folders.</summary>
    protected abstract string Slug { get; }

    /// <summary>Minimum free space required on each drive; below this the benchmark skips (default 2 GiB).</summary>
    protected virtual long MinFreeBytesPerDrive => 2L * 1024 * 1024 * 1024;

    /// <summary>The shared seed folder for this benchmark (under <c>%TEMP%</c>), set in <see cref="Prepare"/>.</summary>
    protected string SeedDirectory { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Prepare(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();

        GuardFreeSpace(environment.SystemRoot);
        GuardFreeSpace(environment.DevRoot);

        SeedDirectory = Path.Combine(environment.SeedRoot, Slug);
        ResetDirectory(SeedDirectory);

        // F11: this benchmark owns only its own Slug subfolder. The shared seed root is a FIXED PARENT
        // that sibling benchmarks also stage fixtures under, so never delete it wholesale — at Cleanup
        // remove it only if it has become empty.
        TrackSharedParent(environment.SeedRoot);
        Track(SeedDirectory);

        PrepareCore(environment, cancellationToken);
    }

    /// <inheritdoc />
    public double MeasureOnce(string driveRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        cancellationToken.ThrowIfCancellationRequested();

        string benchTop = Path.Combine(driveRoot, WorkloadPaths.BenchFolderName);
        string benchRoot = Path.Combine(benchTop, Slug);

        // F9: only ever wholesale-delete benchRoot when THIS app created it or a prior run left our
        // ownership marker. A predictable pre-existing benchRoot WITHOUT the marker is foreign user data:
        // treat it as a fixed parent (removed only if empty) and confine wholesale deletion to the
        // sub-directories we create under it (the cache + each per-iteration run dir).
        bool ownsBenchRoot = TryClaimOwnedDirectory(benchRoot);

        // The per-drive package-cache directory. It lives on the SAME drive under test so every timed
        // run is the honest "everything on this drive" case (cache + source + build output co-located).
        // Each iteration RE-populates it cold from the warm seed (see MeasureCore / PopulateColdCache),
        // so a measured run is a realistic COLD first-build rather than a warm steady-state iteration.
        string cacheDir = Path.Combine(benchRoot, "cache");
        Directory.CreateDirectory(cacheDir);

        // F11: benchTop is shared by sibling benchmarks, so it is a FIXED PARENT we never delete wholesale
        // (removed only if empty).
        TrackSharedParent(benchTop);
        if (ownsBenchRoot)
        {
            Track(benchRoot); // app-owned (we created it / it carries our marker): safe to delete wholesale.
        }
        else
        {
            // F9: foreign pre-existing bench dir — never recurse-delete it. Remove it only if it becomes
            // empty, and wholesale-delete only the sub-directories we own under it.
            TrackSharedParent(benchRoot);
            Track(cacheDir);
        }

        string workDir = Path.Combine(benchRoot, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            return MeasureCore(driveRoot, workDir, cacheDir, cancellationToken);
        }
        finally
        {
            DeleteTree(workDir);
        }
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        // Delete the directories THIS benchmark owns (deepest first so parents are empty when removed).
        foreach (string dir in _ownedDirectories.OrderByDescending(d => d.Length).ToArray())
        {
            DeleteTree(dir);
        }

        _ownedDirectories.Clear();

        // F11: shared FIXED PARENTS may still hold a concurrently-running sibling benchmark's fixtures —
        // remove each only if it is now empty, never wholesale, so we never destroy another benchmark's
        // (or a pre-existing) data.
        foreach (string parent in _sharedParents.OrderByDescending(d => d.Length).ToArray())
        {
            TryRemoveIfEmpty(parent);
        }

        _sharedParents.Clear();
    }

    /// <summary>Seeds the bounded fixture(s) once. Throw <see cref="WorkloadUnavailableException"/> to skip.</summary>
    protected abstract void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one timed iteration on <paramref name="driveRoot"/>. <paramref name="workDir"/> is a fresh
    /// per-iteration directory (deleted afterwards); <paramref name="cacheDir"/> is the per-drive package
    /// cache directory (on the same drive under test). A workload re-populates <paramref name="cacheDir"/>
    /// cold from its warm seed at the start of every iteration via <see cref="PopulateColdCache"/>, so a
    /// measured run is a realistic COLD first-build with cache + source + output all on this drive. Both
    /// directories are removed at <see cref="Cleanup"/>.
    /// </summary>
    protected abstract double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken);

    /// <summary>
    /// Freshly (re)populates the per-drive <paramref name="cacheDir"/> with a clean copy of the warm
    /// <paramref name="seedCache"/>, so the timed build that follows reads a COLD, just-written cache on
    /// the SAME drive it is testing. This is untimed SETUP — call it at the very start of
    /// <see cref="MeasureCore"/>, before <see cref="TimeProcess"/>. The reset makes every iteration
    /// independent (no carry-over warming), and the copy preserves source file timestamps (required by
    /// tools such as cargo that fingerprint on mtime). Everything lives under the tracked per-drive bench
    /// root, so it is deleted wholesale at <see cref="Cleanup"/>.
    /// </summary>
    protected static void PopulateColdCache(string seedCache, string cacheDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedCache);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        ResetDirectory(cacheDir);
        CopyDirectory(seedCache, cacheDir);
    }

    /// <summary>Records a run-owned directory to delete wholesale at <see cref="Cleanup"/>.</summary>
    protected void Track(string directory)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            _ownedDirectories.Add(directory);
        }
    }

    /// <summary>
    /// F11: records a shared FIXED PARENT directory. At <see cref="Cleanup"/> it is removed only if it
    /// has become empty (never deleted wholesale), so a sibling benchmark's fixtures are never destroyed.
    /// </summary>
    protected void TrackSharedParent(string directory)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            _sharedParents.Add(directory);
        }
    }

    /// <summary>
    /// F9: claims <paramref name="directory"/> as app-owned (safe to wholesale-delete) when this benchmark
    /// is creating it now — stamping a <see cref="BenchOwnershipMarkerFileName"/> receipt — or when a prior
    /// run already left that receipt. Returns <c>false</c> for a predictable pre-existing directory that
    /// lacks the marker: foreign data the benchmark must never recursively delete. Best-effort — if the
    /// marker cannot be written, a later run safely degrades to treating the directory as foreign.
    /// </summary>
    private bool TryClaimOwnedDirectory(string directory)
    {
        string markerPath = Path.Combine(directory, BenchOwnershipMarkerFileName);
        if (Directory.Exists(directory))
        {
            // Pre-existing: app-owned only if a prior run left our marker; otherwise it is foreign.
            return File.Exists(markerPath);
        }

        // We are creating it now — stamp the marker so a later run (and this run's Cleanup) recognise it
        // as app-owned and may delete it wholesale.
        Directory.CreateDirectory(directory);
        TryWriteMarker(markerPath);
        return true;
    }

    /// <summary>Best-effort write of the bench ownership marker; never throws.</summary>
    private static void TryWriteMarker(string markerPath)
    {
        try
        {
            File.WriteAllText(
                markerPath,
                "Created by DevDriveManager's workload benchmark. This folder is temporary and is removed "
                    + "automatically after benchmarking. Delete this marker only if you want the app to "
                    + "leave the folder in place.");
        }
        catch
        {
            // Best-effort: if the marker can't be written, a later run treats the dir as foreign (safer default).
        }
    }

    /// <summary>Removes a directory only when it contains no entries; never throws.</summary>
    private static void TryRemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch
        {
            // Best-effort; a concurrent run may be writing under it — leave it in place.
        }
    }

    /// <summary>Runs a process, throwing <see cref="WorkloadUnavailableException"/> on failure, and returns its wall-clock seconds.</summary>
    protected double TimeProcess(WorkloadProcessRequest request, CancellationToken cancellationToken, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        ProcessRunResult result = Runner.Run(request, cancellationToken);
        stopwatch.Stop();
        EnsureSuccess(result, what);
        return stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>Runs a (non-measured) process, throwing <see cref="WorkloadUnavailableException"/> on failure.</summary>
    protected ProcessRunResult RunChecked(WorkloadProcessRequest request, CancellationToken cancellationToken, string what)
    {
        ProcessRunResult result = Runner.Run(request, cancellationToken);
        EnsureSuccess(result, what);
        return result;
    }

    /// <summary>Throws <see cref="WorkloadUnavailableException"/> when a process did not run, timed out, or exited non-zero.</summary>
    protected static void EnsureSuccess(ProcessRunResult? result, string what)
    {
        if (result is null)
        {
            throw new WorkloadUnavailableException($"{what} did not run");
        }

        if (result.TimedOut)
        {
            throw new WorkloadUnavailableException($"{what} timed out");
        }

        if (result.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"{what} failed (exit {result.ExitCode}){FirstLine(result)}");
        }
    }

    /// <summary>Guards a drive's free space, skipping the benchmark when it is below <see cref="MinFreeBytesPerDrive"/>.</summary>
    protected void GuardFreeSpace(string root)
    {
        try
        {
            string? normalized = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            var drive = new DriveInfo(normalized);
            if (drive.IsReady && drive.AvailableFreeSpace < MinFreeBytesPerDrive)
            {
                throw new WorkloadUnavailableException(
                    $"not enough free space on {normalized} (needs about {MinFreeBytesPerDrive / (1024 * 1024 * 1024)} GB)");
            }
        }
        catch (WorkloadUnavailableException)
        {
            throw;
        }
        catch
        {
            // DriveInfo can throw on exotic roots; proceed best-effort.
        }
    }

    /// <summary>Deletes a directory and recreates it empty.</summary>
    protected static void ResetDirectory(string directory)
    {
        DeleteTree(directory);
        Directory.CreateDirectory(directory);
    }

    /// <summary>Recursively copies <paramref name="source"/> into <paramref name="destination"/>, optionally skipping directories by name.</summary>
    protected static void CopyDirectory(string source, string destination, Func<string, bool>? skipDirectory = null)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string sub in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(sub);
            if (skipDirectory is not null && skipDirectory(name))
            {
                continue;
            }

            CopyDirectory(sub, Path.Combine(destination, name), skipDirectory);
        }
    }

    /// <summary>Robustly deletes a directory tree, clearing read-only attributes (git pack files) and retrying once. Never throws.</summary>
    protected static void DeleteTree(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                ClearReadOnly(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch
            {
                // Retry once after clearing attributes; then give up (fixtures are bounded and in temp/bench areas).
            }
        }
    }

    private static void ClearReadOnly(string directory)
    {
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
        catch
        {
            // Enumeration can fail mid-delete; ignore.
        }
    }

    private static string FirstLine(ProcessRunResult result)
    {
        string source = !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError : result.StandardOutput;
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        string? line = source
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        return line.Length > 160 ? $": {line[..160]}…" : $": {line}";
    }
}
