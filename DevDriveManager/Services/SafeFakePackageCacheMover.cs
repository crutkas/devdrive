using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.Services;

/// <summary>
/// UI-test-only <see cref="IPackageCacheMover"/> that SIMULATES a package-cache move: it reports a few
/// progress ticks (so the live progress UI is exercised), records a reversibility entry in the shared
/// in-memory store (so "Move back" lights up exactly as it would in production), and returns a
/// realistic receipt — all WITHOUT copying a single byte, setting an environment variable, or deleting
/// anything. Selected only when <c>DDM_UITEST_SAFE_MUTATIONS=1</c>; never used in normal operation.
/// </summary>
public sealed class SafeFakePackageCacheMover : IPackageCacheMover
{
    // A small, fixed synthetic payload so the simulated result reads realistically ("Moved 24 MB …").
    private const long SimulatedBytes = 24L * 1024 * 1024;
    private const int SimulatedFiles = 8;

    private readonly IReversibilityStore _store;

    public SafeFakePackageCacheMover(IReversibilityStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public Task ApplyAsync(PackageCacheMovePlan plan, CancellationToken cancellationToken = default) =>
        MoveAsync(plan, null, null, cancellationToken);

    /// <inheritdoc />
    public async Task<PackageCacheMoveReceipt> MoveAsync(
        PackageCacheMovePlan plan,
        IProgress<CacheMoveProgress>? progress = null,
        CacheMoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Simulate a short, observable copy (off the UI thread) so the live progress bar is exercised.
        for (int file = 1; file <= SimulatedFiles; file++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            long bytes = SimulatedBytes * file / SimulatedFiles;
            progress?.Report(new CacheMoveProgress
            {
                FilesCompleted = file,
                TotalFiles = SimulatedFiles,
                BytesCompleted = bytes,
                TotalBytes = SimulatedBytes,
                CurrentFile = $"{plan.SourcePath}\\file{file}.bin",
            });
        }

        // Record the reversible entry so the coordinator's CanMoveBack + the row's Move-back work.
        string id = PackageCacheMover.ReversibilityId(plan.EnvironmentVariable);
        _store.Save(new ReversibilityEntry
        {
            Id = id,
            Kind = ReversibilityKinds.PackageCacheMove,
            TimestampUtc = DateTimeOffset.UtcNow,
            ToolName = plan.ToolName,
            EnvironmentVariable = plan.EnvironmentVariable,
            EnvironmentValueWasSet = false,
            PriorEnvironmentValue = null,
            SourcePath = plan.SourcePath,
            TargetPath = plan.TargetPath,
            SourceDeleted = false,
        });

        return new PackageCacheMoveReceipt
        {
            Success = true,
            AlreadyOnDevDrive = false,
            ToolName = plan.ToolName,
            SourcePath = plan.SourcePath,
            TargetPath = plan.TargetPath,
            EnvironmentVariable = plan.EnvironmentVariable,
            FilesCopied = SimulatedFiles,
            BytesCopied = SimulatedBytes,
            SourceDeleted = false,
            ReversibilityId = id,
        };
    }

    /// <inheritdoc />
    public Task<PackageCacheMoveReceipt> MapAsync(
        string environmentVariable,
        string toolName,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        // Record the reversible entry so the row's Move-back lights up — but copy nothing, set no real
        // environment variable, and touch no disk (this is the UI-test safe seam).
        string id = PackageCacheMover.ReversibilityId(environmentVariable);
        _store.Save(new ReversibilityEntry
        {
            Id = id,
            Kind = ReversibilityKinds.PackageCacheMove,
            TimestampUtc = DateTimeOffset.UtcNow,
            ToolName = toolName,
            EnvironmentVariable = environmentVariable,
            EnvironmentValueWasSet = false,
            PriorEnvironmentValue = null,
            SourcePath = string.Empty,
            TargetPath = targetPath,
            SourceDeleted = false,
        });

        return Task.FromResult(new PackageCacheMoveReceipt
        {
            Success = true,
            AlreadyOnDevDrive = false,
            ToolName = toolName ?? string.Empty,
            SourcePath = string.Empty,
            TargetPath = targetPath,
            EnvironmentVariable = environmentVariable,
            FilesCopied = 0,
            BytesCopied = 0,
            SourceDeleted = false,
            ReversibilityId = id,
        });
    }

    /// <inheritdoc />
    public Task<bool> RevertAsync(string reversibilityId, bool moveFilesBack = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reversibilityId);
        bool removed = _store.Remove(reversibilityId);
        return Task.FromResult(removed);
    }
}
