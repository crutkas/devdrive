using System.Text.RegularExpressions;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Orchestrates creation of a virtual hard disk (.vhdx) on top of an injected
/// <see cref="INativeVhdApi"/>: create the file, surface it (attach) so Windows assigns a physical
/// disk, parse the resulting disk number, and record a <see cref="ReversibilityEntry"/> so it can be
/// detached and deleted.
/// </summary>
/// <remarks>
/// <para><b>COMPOSED BY THE APP (M8), GATED BEHIND AN EXPLICIT USER CONFIRMATION — AND NEVER EXERCISED
/// AGAINST A REAL DISK IN TESTS.</b> The default factory composes the real <see cref="NativeVhdApi"/>
/// (virtdisk.dll P/Invoke); the app reaches it through <see cref="DevDriveCreationService.CreateDefault"/>
/// (via <c>MutationComposition.CreateDevDriveCreationService</c>) after the user confirms creation. Under
/// the UI-test seam <c>DDM_UITEST_SAFE_MUTATIONS=1</c> a SAFE fake provisioner is substituted, so the
/// automated suite never creates a real disk. Every unit test injects a <em>mock</em>
/// <see cref="INativeVhdApi"/>, so no test creates, mounts, or deletes a real VHD.</para>
/// <para>This engine deliberately has no UI dependency; surfacing the new disk to the rest of the app
/// (e.g. initializing the disk and formatting it as a Dev Drive) is a later, separately-wired step the
/// user performs in Disk Management.</para>
/// </remarks>
public sealed class VhdProvisioner : IVhdProvisioner
{
    // Windows physical paths look like "\\.\PhysicalDrive3".
    private static readonly Regex PhysicalDriveRegex =
        new(@"PhysicalDrive(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly INativeVhdApi _vhdApi;
    private readonly IFileSystem _fileSystem;
    private readonly IReversibilityStore _reversibility;

    /// <summary>Creates a provisioner over the supplied native API, filesystem, and store.</summary>
    public VhdProvisioner(INativeVhdApi vhdApi, IFileSystem fileSystem, IReversibilityStore reversibility)
    {
        _vhdApi = vhdApi ?? throw new ArgumentNullException(nameof(vhdApi));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _reversibility = reversibility ?? throw new ArgumentNullException(nameof(reversibility));
    }

    /// <summary>
    /// Convenience factory wiring the REAL <see cref="NativeVhdApi"/>. Composed by the app via
    /// <see cref="DevDriveCreationService.CreateDefault"/> when the user confirms Dev Drive creation
    /// (a SAFE fake provisioner is substituted under the UI-test seam).
    /// </summary>
    public static VhdProvisioner CreateDefault() =>
        new(new NativeVhdApi(), new SystemFileSystem(),
            new JsonFileReversibilityStore(JsonFileReversibilityStore.DefaultPath));

    /// <summary>Builds the stable reversibility id for a VHD provisioned at <paramref name="filePath"/>.</summary>
    public static string ReversibilityId(string filePath) => $"vhd:{filePath}";

    /// <inheritdoc />
    public Task<VhdProvisionResult> ProvisionAsync(VhdProvisionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.FilePath);
        if (plan.MaximumSizeBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "MaximumSizeBytes must be positive.");
        }

        return Task.Run(() => ProvisionCore(plan, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RevertAsync(string reversibilityId, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                ReversibilityEntry? entry = _reversibility.TryGet(reversibilityId);
                if (entry is null || entry.Kind != ReversibilityKinds.VhdProvision)
                {
                    return false;
                }

                RevertCore(entry);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RevertAsync(ReversibilityEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => RevertCore(entry), cancellationToken);
    }

    private VhdProvisionResult ProvisionCore(VhdProvisionPlan plan, CancellationToken cancellationToken)
    {
        string filePath = plan.FilePath;

        cancellationToken.ThrowIfCancellationRequested();

        // M1 (data-loss guard): never delete a file we did not create. If something already exists at
        // the target path, fail up-front WITHOUT touching it. This guarantees the rollback paths below
        // (which call TryDeleteFile) can only ever delete a file THIS operation just created.
        if (_fileSystem.FileExists(filePath))
        {
            throw new IOException(
                $"A file already exists at '{filePath}'. Refusing to overwrite or delete a pre-existing file.");
        }

        // C8: CreateVirtualDisk does not create intermediate directories, so ensure the parent exists.
        string? parentDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            _fileSystem.CreateDirectory(parentDirectory);
        }

        try
        {
            _vhdApi.CreateVirtualDisk(filePath, plan.MaximumSizeBytes, plan.DynamicallyExpanding);
        }
        catch
        {
            // Creation failed — best-effort delete of any partial file we created, then surface the error.
            TryDeleteFile(filePath);
            throw;
        }

        // F8 (atomicity): persist the undo receipt BEFORE attaching. AttachVirtualDisk performs a
        // PERMANENT mount, so if the process crashed between a successful attach and a later save we
        // would leave an attached disk with no record of how to revert it. The receipt only needs the
        // id/kind/target path — all known before the mount — so saving first closes that window. If the
        // save itself fails, nothing has been mounted yet: delete the file we created and surface.
        var entry = new ReversibilityEntry
        {
            Id = ReversibilityId(filePath),
            Kind = ReversibilityKinds.VhdProvision,
            TimestampUtc = DateTimeOffset.UtcNow,
            TargetPath = filePath,
        };

        try
        {
            _reversibility.Save(entry);
        }
        catch
        {
            // Couldn't even record intent — undo the creation (nothing is mounted) and surface.
            TryDeleteFile(filePath);
            throw;
        }

        string physicalPath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            physicalPath = _vhdApi.AttachVirtualDisk(filePath);
        }
        catch
        {
            // F9: AttachVirtualDisk mounts PERMANENT and then resolves the physical path, which can throw
            // AFTER the mount has already taken effect. Detach BEFORE deleting (the backing file is
            // otherwise locked by the mount), then drop the intent receipt now that we've fully rolled
            // this operation back, and surface the error.
            TryDetach(filePath);
            TryDeleteFile(filePath);
            TryRemoveReceipt(entry.Id);
            throw;
        }

        int? diskNumber = ParseDiskNumber(physicalPath);

        return new VhdProvisionResult
        {
            Success = true,
            FilePath = filePath,
            PhysicalPath = physicalPath,
            DiskNumber = diskNumber,
            ReversibilityId = entry.Id,
        };
    }

    private void RevertCore(ReversibilityEntry entry)
    {
        string? filePath = entry.TargetPath;
        if (string.IsNullOrEmpty(filePath))
        {
            _reversibility.Remove(entry.Id);
            return;
        }

        // F8 (durable revert): detach + delete must BOTH succeed before we drop the receipt. Detach
        // unlocks the permanently-mounted backing file; if it (or the delete) fails we SURFACE the error
        // and KEEP the receipt so the revert can be retried — never silently swallow it and orphan a
        // still-mounted disk with no undo record.
        _vhdApi.DetachVirtualDisk(filePath);
        if (_fileSystem.FileExists(filePath))
        {
            _fileSystem.DeleteFile(filePath);
        }

        _reversibility.Remove(entry.Id);
    }

    private void TryRemoveReceipt(string id)
    {
        try
        {
            _reversibility.Remove(id);
        }
        catch
        {
            // Best-effort cleanup of the intent receipt during rollback.
        }
    }

    private void TryDetach(string filePath)
    {
        try
        {
            _vhdApi.DetachVirtualDisk(filePath);
        }
        catch
        {
            // Best-effort rollback; the disk may already be detached.
        }
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (_fileSystem.FileExists(filePath))
            {
                _fileSystem.DeleteFile(filePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Extracts the integer disk number from a Windows physical path such as
    /// <c>\\.\PhysicalDrive3</c>. Returns <c>null</c> when the path doesn't match. Public so it can be
    /// unit-tested directly.
    /// </summary>
    public static int? ParseDiskNumber(string? physicalPath)
    {
        if (string.IsNullOrEmpty(physicalPath))
        {
            return null;
        }

        Match match = PhysicalDriveRegex.Match(physicalPath);
        return match.Success && int.TryParse(match.Groups[1].Value, out int number) ? number : null;
    }
}
