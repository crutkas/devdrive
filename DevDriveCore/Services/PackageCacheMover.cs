using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// REAL implementation of the <see cref="IPackageCacheMover"/> seam. Given a
/// <see cref="PackageCacheMovePlan"/> it: creates the target folder on the Dev Drive, copies the
/// cache (verifying each file by SHA-256 and reporting progress), sets the per-user environment
/// variable via <see cref="IEnvironmentWriter"/>, and records a <see cref="ReversibilityEntry"/> via
/// <see cref="IReversibilityStore"/> so the move can be undone.
/// </summary>
/// <remarks>
/// <para><b>COMPOSED BY THE APP (M4), GATED BEHIND AN EXPLICIT USER CONFIRMATION.</b> In normal
/// operation the app composes this real mover via <see cref="PackageCacheMoveCoordinator"/>
/// (<c>MutationComposition.CreatePackageCacheMoveCoordinator</c>) and runs it only after a per-row
/// confirm. Under the UI-test seam <c>DDM_UITEST_SAFE_MUTATIONS=1</c> a SAFE fake mover is substituted
/// instead, so the automated suite never touches a real cache. Unit tests inject an in-memory filesystem
/// + fake environment writer + fake store (or, for integration tests, the real <see cref="SystemFileSystem"/>
/// against a throwaway temp directory).</para>
/// <para><b>SAFETY:</b> every filesystem and environment operation flows through an injected
/// abstraction, so no test touches a real cache (e.g. the real npm cache) or a real environment
/// variable. The default options keep the source in place (no deletion) for trivial revert.</para>
/// </remarks>
public sealed class PackageCacheMover : IPackageCacheMover
{
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentWriter _environment;
    private readonly IReversibilityStore _reversibility;

    /// <summary>
    /// F4: name of the small receipt file the mover drops into a target it creates/claims. Its presence
    /// marks the folder as app-managed, so a later revert/move-back may recursively delete ONLY a target
    /// the app owns — a pre-existing folder the user populated (no marker) is never recursively deleted,
    /// and a non-empty unowned target is refused up front rather than clobbered.
    /// </summary>
    public const string OwnershipMarkerFileName = ".devdrivemanager-cache";

    /// <summary>Creates a mover over the supplied filesystem, environment writer, and reversibility store.</summary>
    public PackageCacheMover(IFileSystem fileSystem, IEnvironmentWriter environment, IReversibilityStore reversibility)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _reversibility = reversibility ?? throw new ArgumentNullException(nameof(reversibility));
    }

    /// <summary>
    /// Convenience factory wiring the real platform implementations. The app's production path composes an
    /// equivalent real mover inline via <see cref="PackageCacheMoveCoordinator.CreateDefault"/> (sharing one
    /// reversibility store); this factory is retained for tests and direct callers.
    /// </summary>
    public static PackageCacheMover CreateDefault() =>
        new(new SystemFileSystem(), new UserEnvironmentWriter(),
            new JsonFileReversibilityStore(JsonFileReversibilityStore.DefaultPath));

    /// <summary>Builds the stable reversibility id for a move that targets <paramref name="environmentVariable"/>.</summary>
    public static string ReversibilityId(string environmentVariable) => $"package-cache:{environmentVariable}";

    /// <inheritdoc />
    /// <remarks>The basic seam runs a full move with default options and no progress reporting.</remarks>
    public Task ApplyAsync(PackageCacheMovePlan plan, CancellationToken cancellationToken = default) =>
        MoveAsync(plan, progress: null, options: null, cancellationToken);

    /// <summary>
    /// Performs the move with progress reporting and tunable <paramref name="options"/>, returning a
    /// receipt describing what happened. On a mid-copy failure it rolls back the partial copy and
    /// leaves the environment variable untouched.
    /// </summary>
    public Task<PackageCacheMoveReceipt> MoveAsync(
        PackageCacheMovePlan plan,
        IProgress<CacheMoveProgress>? progress = null,
        CacheMoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.TargetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.EnvironmentVariable);

        CacheMoveOptions effectiveOptions = options ?? CacheMoveOptions.Default;
        return Task.Run(() => MoveCore(plan, progress, effectiveOptions, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// "Map path": points the per-user variable at <paramref name="targetPath"/> WITHOUT copying any
    /// files. Records a reversibility entry (so "Move back" restores the prior value) but writes NO
    /// ownership marker — the folder is the user's, so a later revert restores only the variable and
    /// preserves the folder. Idempotent when the variable already points there.
    /// </summary>
    public Task<PackageCacheMoveReceipt> MapAsync(
        string environmentVariable,
        string toolName,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return Task.Run(() => MapCore(environmentVariable, toolName ?? string.Empty, targetPath), cancellationToken);
    }

    /// <summary>Reverts a recorded move (by id): restores the prior environment value and removes the Dev Drive copy.</summary>
    /// <returns><c>true</c> when an entry was found and reverted; otherwise <c>false</c>.</returns>
    public async Task<bool> RevertAsync(string reversibilityId, bool moveFilesBack = false, CancellationToken cancellationToken = default)
    {
        ReversibilityEntry? entry = _reversibility.TryGet(reversibilityId);
        if (entry is null || entry.Kind != ReversibilityKinds.PackageCacheMove)
        {
            return false;
        }

        await RevertAsync(entry, moveFilesBack, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reverts a recorded move (by entry): restores the prior environment value and removes the Dev
    /// Drive copy. When the original move deleted the source (recorded in
    /// <see cref="ReversibilityEntry.SourceDeleted"/>) the files are copied back to the source first.
    /// </summary>
    public Task RevertAsync(ReversibilityEntry entry, bool moveFilesBack = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => RevertCore(entry, moveFilesBack), cancellationToken);
    }

    private PackageCacheMoveReceipt MoveCore(
        PackageCacheMovePlan plan, IProgress<CacheMoveProgress>? progress, CacheMoveOptions options, CancellationToken cancellationToken)
    {
        string source = plan.SourcePath ?? string.Empty;
        string target = plan.TargetPath;
        string envVar = plan.EnvironmentVariable;

        // Capture prior environment state up front, for reversibility + idempotency.
        string? priorEnv = _environment.GetUserVariable(envVar);
        bool priorEnvSet = !string.IsNullOrEmpty(priorEnv);

        // Idempotency: the variable already points at the target — nothing to do.
        if (priorEnvSet && PathsEqual(priorEnv!, target))
        {
            return new PackageCacheMoveReceipt
            {
                Success = true,
                AlreadyOnDevDrive = true,
                ToolName = plan.ToolName,
                SourcePath = source,
                TargetPath = target,
                EnvironmentVariable = envVar,
                PriorEnvironmentValueWasSet = true,
                PriorEnvironmentValue = priorEnv,
            };
        }

        bool sourceExists = !string.IsNullOrEmpty(source) && _fileSystem.DirectoryExists(source);
        bool sameLocation = !string.IsNullOrEmpty(source) && PathsEqual(source, target);
        List<string> sourceFiles = sourceExists && !sameLocation
            ? _fileSystem.EnumerateFiles(source).ToList()
            : new List<string>();

        long totalBytes = 0;
        foreach (string file in sourceFiles)
        {
            totalBytes += SafeLength(file);
        }

        bool targetPreexisted = _fileSystem.DirectoryExists(target);
        string markerPath = Path.Combine(target, OwnershipMarkerFileName);
        bool appOwnsTarget = targetPreexisted && _fileSystem.FileExists(markerPath);

        // F4: never clobber pre-existing user data at the deterministic target. If the target already
        // exists, holds files, and lacks our ownership marker, it belongs to the user — refuse rather than
        // copy into (and later delete) it. A fresh target, an empty pre-existing folder, or one we already
        // own is fine. The coordinator turns this throw into a clean "failed" outcome for the UI.
        if (targetPreexisted && !appOwnsTarget && TargetHasContent(target))
        {
            throw new IOException(
                $"Refusing to move the {plan.ToolName} cache into '{target}': it already contains files this app didn't create. " +
                "Move or remove them first, or choose a different target.");
        }

        var copiedFiles = new List<string>();
        int filesCopied = 0;
        long bytesCopied = 0;

        progress?.Report(new CacheMoveProgress
        {
            FilesCompleted = 0,
            TotalFiles = sourceFiles.Count,
            BytesCompleted = 0,
            TotalBytes = totalBytes,
            CurrentFile = string.Empty,
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.CreateDirectory(target);

            // F4: stamp the ownership marker so revert can safely (recursively) delete ONLY a target the app
            // created/claimed. Written OUTSIDE the copy loop so it never counts toward FilesCopied or emits a
            // progress report. On a rollback of a target we created, the recursive delete removes it too.
            if (!_fileSystem.FileExists(markerPath))
            {
                _fileSystem.WriteAllText(markerPath, OwnershipMarkerContents());
            }

            foreach (string file in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relative = Path.GetRelativePath(source, file);
                string destination = Path.Combine(target, relative);

                _fileSystem.CopyFile(file, destination, options.Overwrite);
                copiedFiles.Add(destination);

                if (options.VerifyHashes)
                {
                    string sourceHash = _fileSystem.ComputeSha256(file);
                    string destinationHash = _fileSystem.ComputeSha256(destination);
                    if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
                    {
                        throw new IOException($"Integrity check failed: '{destination}' does not match source '{file}'.");
                    }
                }

                filesCopied++;
                bytesCopied += SafeLength(destination);
                progress?.Report(new CacheMoveProgress
                {
                    FilesCompleted = filesCopied,
                    TotalFiles = sourceFiles.Count,
                    BytesCompleted = bytesCopied,
                    TotalBytes = totalBytes,
                    CurrentFile = file,
                });
            }
        }
        catch
        {
            // Roll back the partial copy. The environment variable was NOT changed yet, so there is
            // nothing to undo there, and no reversibility entry was recorded.
            RollbackCopy(copiedFiles, target, targetPreexisted);
            throw;
        }

        // Copy verified (or nothing to copy). C6: commit in a machine-safe order. Persist the revert
        // record FIRST — before any machine mutation — so a Save failure happens BEFORE the env var is
        // changed or the source is deleted, never leaving the machine mutated with no way back.
        var entry = new ReversibilityEntry
        {
            Id = ReversibilityId(envVar),
            Kind = ReversibilityKinds.PackageCacheMove,
            ToolName = plan.ToolName,
            TimestampUtc = DateTimeOffset.UtcNow,
            EnvironmentVariable = envVar,
            EnvironmentValueWasSet = priorEnvSet,
            PriorEnvironmentValue = priorEnv,
            SourcePath = source,
            TargetPath = target,
            SourceDeleted = false,
        };
        _reversibility.Save(entry);

        // Now mutate the machine: point the per-user variable at the Dev Drive copy.
        _environment.SetUserVariable(envVar, target);

        bool sourceDeleted = false;
        if (options.DeleteSourceAfterVerify && sourceExists && !sameLocation)
        {
            // F12 (data-loss guard): record that the source is ABOUT to be deleted BEFORE deleting it.
            // RevertCore copies the target back to the source whenever SourceDeleted is set, so if the
            // process dies between this Save and the delete (or the delete itself throws) the receipt
            // already describes a recoverable state. Persisting only AFTER the delete would, on a crash
            // in that window, leave the source gone while the receipt still claims it exists — and the
            // copy-back on revert would never run, losing the data.
            entry = entry with { SourceDeleted = true };
            _reversibility.Save(entry);

            _fileSystem.DeleteDirectory(source, recursive: true);
            sourceDeleted = true;
        }

        return new PackageCacheMoveReceipt
        {
            Success = true,
            ToolName = plan.ToolName,
            SourcePath = source,
            TargetPath = target,
            EnvironmentVariable = envVar,
            PriorEnvironmentValueWasSet = priorEnvSet,
            PriorEnvironmentValue = priorEnv,
            FilesCopied = filesCopied,
            BytesCopied = bytesCopied,
            SourceDeleted = sourceDeleted,
            ReversibilityId = entry.Id,
        };
    }

    private PackageCacheMoveReceipt MapCore(string envVar, string toolName, string targetPath)
    {
        string? priorEnv = _environment.GetUserVariable(envVar);
        bool priorEnvSet = !string.IsNullOrEmpty(priorEnv);

        // Idempotent: the variable already points where the user asked — nothing to mutate or record.
        if (priorEnvSet && PathsEqual(priorEnv!, targetPath))
        {
            return new PackageCacheMoveReceipt
            {
                Success = true,
                AlreadyOnDevDrive = true,
                ToolName = toolName,
                SourcePath = string.Empty,
                TargetPath = targetPath,
                EnvironmentVariable = envVar,
                PriorEnvironmentValueWasSet = true,
                PriorEnvironmentValue = priorEnv,
                ReversibilityId = ReversibilityId(envVar),
            };
        }

        // C6 machine-safe order: persist the revert record BEFORE the env mutation, so a Save failure
        // happens before the machine changes. No ownership marker is written — the folder is the user's,
        // so RevertCore (no marker present) restores only the variable and PRESERVES the folder.
        var entry = new ReversibilityEntry
        {
            Id = ReversibilityId(envVar),
            Kind = ReversibilityKinds.PackageCacheMove,
            ToolName = toolName,
            TimestampUtc = DateTimeOffset.UtcNow,
            EnvironmentVariable = envVar,
            EnvironmentValueWasSet = priorEnvSet,
            PriorEnvironmentValue = priorEnv,
            SourcePath = string.Empty,
            TargetPath = targetPath,
            SourceDeleted = false,
        };
        _reversibility.Save(entry);

        _environment.SetUserVariable(envVar, targetPath);

        return new PackageCacheMoveReceipt
        {
            Success = true,
            ToolName = toolName,
            SourcePath = string.Empty,
            TargetPath = targetPath,
            EnvironmentVariable = envVar,
            PriorEnvironmentValueWasSet = priorEnvSet,
            PriorEnvironmentValue = priorEnv,
            FilesCopied = 0,
            BytesCopied = 0,
            SourceDeleted = false,
            ReversibilityId = entry.Id,
        };
    }

    private void RevertCore(ReversibilityEntry entry, bool moveFilesBack)
    {
        // 1. Restore the environment variable to its prior state.
        if (!string.IsNullOrEmpty(entry.EnvironmentVariable))
        {
            _environment.SetUserVariable(
                entry.EnvironmentVariable,
                entry.EnvironmentValueWasSet ? entry.PriorEnvironmentValue : null);
        }

        // 2. C1 + F4: remove the Dev Drive copy so reverting never orphans it — but recursively delete ONLY
        //    a target THIS app created/claimed (ownership marker present). A target the app didn't create
        //    (no marker) is PRESERVED: we never recursively delete user data on revert. The copy-back
        //    decision is driven by the AUTHORITATIVE recorded state (entry.SourceDeleted), not the caller
        //    flag: when the move deleted the source, copy the files back to the source FIRST.
        _ = moveFilesBack; // Retained for API compatibility; revert behavior is recorded-state driven.
        if (!string.IsNullOrEmpty(entry.TargetPath) && _fileSystem.DirectoryExists(entry.TargetPath))
        {
            string markerPath = Path.Combine(entry.TargetPath, OwnershipMarkerFileName);
            bool appOwnsTarget = _fileSystem.FileExists(markerPath);

            if (entry.SourceDeleted && !string.IsNullOrEmpty(entry.SourcePath))
            {
                foreach (string file in _fileSystem.EnumerateFiles(entry.TargetPath))
                {
                    string relative = Path.GetRelativePath(entry.TargetPath, file);
                    if (IsOwnershipMarker(relative))
                    {
                        continue; // never copy our marker back into the restored source
                    }

                    string destination = Path.Combine(entry.SourcePath, relative);
                    _fileSystem.CopyFile(file, destination, overwrite: true);
                }
            }

            if (appOwnsTarget)
            {
                _fileSystem.DeleteDirectory(entry.TargetPath, recursive: true);
            }
        }

        _reversibility.Remove(entry.Id);
    }

    private void RollbackCopy(List<string> copiedFiles, string target, bool targetPreexisted)
    {
        foreach (string file in copiedFiles)
        {
            try
            {
                _fileSystem.DeleteFile(file);
            }
            catch
            {
                // Best-effort rollback.
            }
        }

        if (!targetPreexisted)
        {
            try
            {
                _fileSystem.DeleteDirectory(target, recursive: true);
            }
            catch
            {
                // Best-effort rollback.
            }
        }
    }

    private long SafeLength(string path)
    {
        try
        {
            return Math.Max(0L, _fileSystem.GetFileLength(path));
        }
        catch
        {
            return 0L;
        }
    }

    // F4: true when the target holds any file other than our own ownership marker (i.e. real user data).
    private bool TargetHasContent(string target) =>
        _fileSystem.EnumerateFiles(target).Any(f => !IsOwnershipMarker(f));

    private static bool IsOwnershipMarker(string pathOrName) =>
        string.Equals(Path.GetFileName(pathOrName), OwnershipMarkerFileName, StringComparison.OrdinalIgnoreCase);

    private static string OwnershipMarkerContents() =>
        $"Created by DevDriveManager at {DateTimeOffset.UtcNow:O}. This file marks the folder as an " +
        "app-managed package-cache target so DevDriveManager can safely remove it on revert. Delete it " +
        "only if you no longer want DevDriveManager to manage this cache.";

    private static bool PathsEqual(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');
}
