using DevDriveCore;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.Services;

/// <summary>
/// UI-test-only <see cref="IVolumeResizer"/> that SIMULATES the shrink-to-create-a-Dev-Drive flow: it
/// produces a plausible feasibility verdict and a simulated execute outcome WITHOUT launching the
/// elevated helper, querying Storage, or touching a disk &#8212; nothing is shrunk, partitioned, or
/// formatted. Selected only when <c>DDM_UITEST_SAFE_MUTATIONS=1</c>; never used in normal operation.
/// </summary>
/// <remarks>
/// The preview runs the REAL <see cref="ResizeGuard"/> over a synthetic in-memory snapshot describing a
/// healthy, resizable data volume, so the verdict, steps, and numbers read realistically while staying
/// completely offline. The execute path returns a simulated success and never reports a real mutation.
/// </remarks>
public sealed class SafeFakeVolumeResizer : IVolumeResizer
{
    /// <inheritdoc />
    public Task<ResizeFeasibility?> PreviewAsync(ResizePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        // Evaluate the real guard against a synthetic, safe snapshot — no Storage query, no disk I/O.
        ResizeFeasibility feasibility = ResizeGuard.Evaluate(plan, SyntheticSnapshot(plan));
        return Task.FromResult<ResizeFeasibility?>(feasibility);
    }

    /// <inheritdoc />
    public Task<ResizeExecuteOutcome> ExecuteAsync(ResizePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        ResizeFeasibility feasibility = ResizeGuard.Evaluate(plan, SyntheticSnapshot(plan));
        char source = char.ToUpperInvariant(plan.SourceVolumeLetter);
        char target = char.ToUpperInvariant(plan.NewDriveLetter);

        return Task.FromResult(new ResizeExecuteOutcome
        {
            Success = true,
            Executed = true,
            Message = $"Simulated resize (safe fake): would shrink {source}: by " +
                      $"{ByteSizeFormatter.Format(feasibility.AlignedShrinkBytes)} and create the Dev Drive at " +
                      $"{target}:. No real disk was touched.",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
            CompletedSteps = feasibility.Steps,
        });
    }

    // A synthetic snapshot of a healthy, resizable Basic NTFS data volume on a fixed NVMe disk, sized so
    // any plan that satisfies the app's own 50 GiB minimum passes the guard. Pure in-memory data.
    private static DiskLayoutSnapshot SyntheticSnapshot(ResizePlan plan)
    {
        // Make the partition comfortably larger than the request and leave generous reclaimable space.
        ulong partitionSize = checked(plan.ShrinkBytes + (2UL * ResizeGuard.MinimumDevDriveBytes) + plan.ShrinkBytes);
        ulong supportedMin = ResizeGuard.MinimumDevDriveBytes; // reclaimable = partitionSize - supportedMin (large).

        return new DiskLayoutSnapshot
        {
            SourceResolved = true,
            SourceVolumeLetter = char.ToUpperInvariant(plan.SourceVolumeLetter),
            FileSystem = "NTFS",
            PartitionType = "Basic",
            GptType = string.Empty,
            IsSystemPartition = false,
            IsActivePartition = false,
            PartitionSizeBytes = partitionSize,
            SupportedSizeMinBytes = supportedMin,
            SupportedSizeMaxBytes = partitionSize,
            DiskNumber = 0,
            PartitionStyle = "GPT",
            IsDiskOffline = false,
            IsDiskReadOnly = false,
            IsRemovable = false,
            BusType = "NVMe",
            PartitionAlignmentBytes = 0,
        };
    }
}
