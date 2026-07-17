using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Pure, deterministic safety gate for the "shrink an existing volume to carve out a Dev Drive"
/// operation. <see cref="Evaluate"/> takes a <see cref="ResizePlan"/> plus a READ-ONLY
/// <see cref="DiskLayoutSnapshot"/> and decides whether the resize is safe, returning a
/// <see cref="ResizeFeasibility"/> verdict (go/no-go + reason + what WOULD happen).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the don't-touch-the-OS firewall.</b> It runs inside the privileged helper BEFORE any
/// destructive command, and again before <see cref="ResizeMode.Execute"/>, so a crafted/garbage plan
/// is <i>rejected</i>, never executed. It refuses offline / read-only / not-initialized (RAW) /
/// removable disks; never touches MSR (reserved), recovery, EFI/system partitions; enforces the
/// 50&#160;GiB Dev Drive minimum; rounds to disk alignment; and refuses a shrink that exceeds the real
/// reclaimable space.
/// </para>
/// <para>
/// <b>Pure by construction:</b> no Windows API, no I/O, no process launch &#8212; every input arrives
/// as plain data. That makes the entire guard suite unit-testable WITHOUT a real disk: a test just
/// builds the snapshot for the scenario it wants to assert.
/// </para>
/// </remarks>
public static class ResizeGuard
{
    /// <summary>Hard platform minimum for a Dev Drive: 50&#160;GiB (matches <c>c_minimumSizeForDevVolumeInBytes</c>).</summary>
    public const ulong MinimumDevDriveBytes = 50UL * 1024UL * 1024UL * 1024UL;

    /// <summary>Default disk alignment the carved partition rounds to when the snapshot doesn't specify one (1&#160;MiB).</summary>
    public const ulong DefaultAlignmentBytes = 1024UL * 1024UL;

    // GPT partition type GUIDs we must never resize/repartition. Recognised in addition to the
    // Get-Partition "Type" string so a crafted snapshot can't slip a protected partition through by
    // mislabelling its Type.
    private const string GptEfiSystem = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    private const string GptMicrosoftReserved = "e3c9e316-0b5c-4db8-817d-f92df00215ae";
    private const string GptWindowsRecovery = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

    /// <summary>
    /// Evaluates <paramref name="plan"/> against the read-only <paramref name="snapshot"/> and returns
    /// a go/no-go feasibility verdict. Touches nothing.
    /// </summary>
    public static ResizeFeasibility Evaluate(ResizePlan plan, DiskLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);

        char source = char.ToUpperInvariant(plan.SourceVolumeLetter);
        char target = char.ToUpperInvariant(plan.NewDriveLetter);
        ulong reclaimable = ReclaimableBytes(snapshot);

        ResizeFeasibility Deny(string reason) => new()
        {
            CanProceed = false,
            Reason = reason,
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            RequestedShrinkBytes = plan.ShrinkBytes,
            ReclaimableBytes = reclaimable,
            SourceSizeBytesBefore = snapshot.PartitionSizeBytes,
            DiskNumber = snapshot.DiskNumber,
            DiskUniqueId = snapshot.DiskUniqueId,
            PartitionNumber = snapshot.PartitionNumber,
            PartitionOffsetBytes = snapshot.PartitionOffsetBytes,
            PartitionGuid = snapshot.PartitionGuid,
            IsReadOnlyProbe = true,
        };

        // 1. Plan sanity — reject garbage before looking at the disk.
        if (source is < 'A' or > 'Z')
        {
            return Deny("Invalid source volume letter.");
        }

        if (target is < 'A' or > 'Z')
        {
            return Deny("Invalid new Dev Drive letter.");
        }

        if (source == target)
        {
            return Deny("The new Dev Drive letter must differ from the source volume letter.");
        }

        // F6: refuse if the requested Dev Drive letter is already mounted. Defence in depth — the helper's
        // execute script ALSO re-checks this immediately before carving (the snapshot can go stale between
        // probe and execute). DriveLettersInUse is empty on older/unpopulated snapshots, so this stays
        // backward-compatible (an empty set never denies).
        if (!string.IsNullOrEmpty(snapshot.DriveLettersInUse) &&
            snapshot.DriveLettersInUse.ToUpperInvariant().Contains(target))
        {
            return Deny($"Drive letter {target}: is already in use.");
        }

        if (plan.ShrinkBytes == 0)
        {
            return Deny("The requested shrink size is zero.");
        }

        // 2. The source must resolve to a real partition.
        if (!snapshot.SourceResolved)
        {
            return Deny($"Couldn't find volume {source}: on this system.");
        }

        if (char.ToUpperInvariant(snapshot.SourceVolumeLetter) != source)
        {
            return Deny("The resolved source volume does not match the requested drive letter.");
        }

        if (!snapshot.SupportsDevDriveFormat)
        {
            return Deny(
                "This Windows build or its inbox Storage module does not support Dev Drive formatting. " +
                "Windows 11 build 22621.2338 or later is required.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.DiskUniqueId) ||
            snapshot.PartitionNumber <= 0 ||
            snapshot.PartitionOffsetBytes == 0)
        {
            return Deny("Windows did not provide a stable disk and partition identity for this volume.");
        }

        // An execute request must remain bound to the exact disk and partition approved by preview.
        if (plan.ExecuteAuthorized)
        {
            if (!HasExpectedPreviewIdentity(plan))
            {
                return Deny("The execute request is missing the successful preview identity.");
            }

            if (snapshot.DiskNumber != plan.ExpectedDiskNumber ||
                !IdentityEquals(snapshot.DiskUniqueId, plan.ExpectedDiskUniqueId) ||
                snapshot.PartitionNumber != plan.ExpectedPartitionNumber ||
                snapshot.PartitionOffsetBytes != plan.ExpectedPartitionOffsetBytes ||
                (!string.IsNullOrWhiteSpace(plan.ExpectedPartitionGuid) &&
                 !IdentityEquals(snapshot.PartitionGuid, plan.ExpectedPartitionGuid)))
            {
                return Deny("The source disk or partition identity changed after preview. Run the preview again.");
            }
        }

        // 3. Disk-level guards — never repartition an unsafe disk.
        if (snapshot.IsDiskOffline)
        {
            return Deny($"Disk {snapshot.DiskNumber} is offline.");
        }

        if (snapshot.IsDiskReadOnly)
        {
            return Deny($"Disk {snapshot.DiskNumber} is read-only.");
        }

        if (snapshot.IsRemovable)
        {
            return Deny("Refusing to repartition a removable disk.");
        }

        if (IsUninitialized(snapshot.PartitionStyle))
        {
            return Deny("The disk is not initialized (RAW partition table).");
        }

        // 4. Partition-type guards — never touch MSR / recovery / EFI / system partitions.
        if (IsProtectedPartition(snapshot, out string protectedKind))
        {
            return Deny($"Refusing to resize a protected partition ({protectedKind}).");
        }

        // 5. File-system guards — refuse RAW / unrecognised file systems.
        if (!IsResizableFileSystem(snapshot.FileSystem))
        {
            string fs = string.IsNullOrWhiteSpace(snapshot.FileSystem) ? "RAW/unknown" : snapshot.FileSystem;
            return Deny($"Source volume file system '{fs}' can't be safely resized (need NTFS or ReFS).");
        }

        // 6. Reclaimable guard — never shrink by more than the volume can actually give back.
        if (plan.ShrinkBytes > reclaimable)
        {
            return Deny(
                $"Requested shrink {Format(plan.ShrinkBytes)} exceeds the {Format(reclaimable)} that can be reclaimed from {source}:.");
        }

        // 7. Alignment rounding — round the carved Dev Drive DOWN to disk alignment so it never
        //    exceeds reclaimable, then re-check the 50 GiB minimum against the aligned size.
        ulong alignment = snapshot.PartitionAlignmentBytes == 0 ? DefaultAlignmentBytes : snapshot.PartitionAlignmentBytes;
        ulong aligned = RoundDownToAlignment(plan.ShrinkBytes, alignment);

        if (aligned == 0)
        {
            return Deny("The requested shrink rounds to zero after disk-alignment.");
        }

        if (aligned < MinimumDevDriveBytes)
        {
            return Deny(
                $"A Dev Drive must be at least {Format(MinimumDevDriveBytes)}; after alignment only {Format(aligned)} is available.");
        }

        if (plan.ExecuteAuthorized && plan.ExpectedAlignedShrinkBytes != aligned)
        {
            return Deny("The aligned Dev Drive size changed after preview. Run the preview again.");
        }

        // Go. Describe exactly what the real (elevated) operation would do.
        ulong before = snapshot.PartitionSizeBytes;
        ulong after = before > aligned ? before - aligned : 0UL;

        var steps = new[]
        {
            $"Shrink {source}: by {Format(aligned)} (Resize-Partition) — {Format(reclaimable)} is reclaimable.",
            $"Create a new {Format(aligned)} partition in the freed space and assign {target}: (New-Partition -AssignDriveLetter).",
            $"Format {target}: as ReFS with the Dev Drive flag (Format-Volume -DevDrive -FileSystem ReFS).",
        };

        return new ResizeFeasibility
        {
            CanProceed = true,
            Reason = $"{source}: can be shrunk by {Format(aligned)} to create a {Format(aligned)} ReFS Dev Drive at {target}:.",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            RequestedShrinkBytes = plan.ShrinkBytes,
            AlignedShrinkBytes = aligned,
            ReclaimableBytes = reclaimable,
            SourceSizeBytesBefore = before,
            SourceSizeBytesAfter = after,
            DiskNumber = snapshot.DiskNumber,
            DiskUniqueId = snapshot.DiskUniqueId,
            PartitionNumber = snapshot.PartitionNumber,
            PartitionOffsetBytes = snapshot.PartitionOffsetBytes,
            PartitionGuid = snapshot.PartitionGuid,
            Steps = steps,
            IsReadOnlyProbe = true,
        };
    }

    /// <summary>True when an execute plan carries the complete stable identity from a passing preview.</summary>
    public static bool HasExpectedPreviewIdentity(ResizePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ExpectedDiskNumber is >= 0 &&
               !string.IsNullOrWhiteSpace(plan.ExpectedDiskUniqueId) &&
               plan.ExpectedPartitionNumber is > 0 &&
               plan.ExpectedPartitionOffsetBytes is > 0 &&
               plan.ExpectedAlignedShrinkBytes is >= MinimumDevDriveBytes;
    }

    /// <summary>True when a passing preview returned the stable identity required by execution.</summary>
    public static bool HasPreviewIdentity(ResizeFeasibility feasibility)
    {
        ArgumentNullException.ThrowIfNull(feasibility);
        return feasibility.DiskNumber is >= 0 &&
               !string.IsNullOrWhiteSpace(feasibility.DiskUniqueId) &&
               feasibility.PartitionNumber is > 0 &&
               feasibility.PartitionOffsetBytes is > 0 &&
               feasibility.AlignedShrinkBytes >= MinimumDevDriveBytes;
    }

    /// <summary>
    /// Real reclaimable space on the source: current partition size minus the smallest size it can
    /// shrink to (<c>Get-PartitionSupportedSize.SizeMin</c>). Never negative.
    /// </summary>
    public static ulong ReclaimableBytes(DiskLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.PartitionSizeBytes > snapshot.SupportedSizeMinBytes
            ? snapshot.PartitionSizeBytes - snapshot.SupportedSizeMinBytes
            : 0UL;
    }

    /// <summary>Rounds <paramref name="value"/> DOWN to the nearest multiple of <paramref name="alignment"/>.</summary>
    public static ulong RoundDownToAlignment(ulong value, ulong alignment) =>
        alignment == 0 ? value : value - (value % alignment);

    private static bool IsUninitialized(string? partitionStyle) =>
        string.IsNullOrWhiteSpace(partitionStyle)
        || partitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
        || partitionStyle.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsResizableFileSystem(string? fileSystem) =>
        !string.IsNullOrWhiteSpace(fileSystem)
        && (fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            || fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase));

    // Recognise protected partitions by BOTH the Get-Partition "Type" string AND the GPT type GUID,
    // plus the IsSystem flag, so a crafted snapshot can't disguise one as a Basic data partition.
    private static bool IsProtectedPartition(DiskLayoutSnapshot snapshot, out string kind)
    {
        if (snapshot.IsSystemPartition)
        {
            kind = "EFI/system";
            return true;
        }

        string type = snapshot.PartitionType?.Trim() ?? string.Empty;
        if (type.Equals("Reserved", StringComparison.OrdinalIgnoreCase))
        {
            kind = "Microsoft Reserved (MSR)";
            return true;
        }

        if (type.Equals("Recovery", StringComparison.OrdinalIgnoreCase))
        {
            kind = "Recovery";
            return true;
        }

        if (type.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            kind = "EFI System";
            return true;
        }

        string gpt = NormalizeGuid(snapshot.GptType);
        if (gpt == GptEfiSystem)
        {
            kind = "EFI System";
            return true;
        }

        if (gpt == GptMicrosoftReserved)
        {
            kind = "Microsoft Reserved (MSR)";
            return true;
        }

        if (gpt == GptWindowsRecovery)
        {
            kind = "Recovery";
            return true;
        }

        kind = string.Empty;
        return false;
    }

    private static string NormalizeGuid(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().Trim('{', '}').ToLowerInvariant();

    private static bool IdentityEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Format(ulong bytes) => ByteSizeFormatter.Format(bytes);
}
