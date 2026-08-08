using System.Runtime.InteropServices;

namespace DevDriveReclaim;

/// <summary>
/// The real one. Moves candidates to the Recycle Bin where that is possible, deletes them where it
/// is not, and empties a volume's bin when the candidate is a bin.
/// </summary>
/// <remarks>
/// Three decisions shape this class.
/// <para>
/// <b>Nesting is resolved before anything is removed.</b> The selection is reduced to containment
/// roots, exactly as the totals are. Without that, deleting a worktree and then the <c>obj</c> folder
/// that used to be inside it fails on the second call, and the run reports a failure for something
/// that in fact succeeded.
/// </para>
/// <para>
/// <b>The Recycle Bin is preferred and its limits are not hidden.</b> <c>FOF_ALLOWUNDO</c> silently
/// degrades to a permanent delete when an item is too large for the bin or the volume has recycling
/// disabled. Recycling <em>moves</em> the item into <c>$Recycle.Bin</c>, so the original path is gone
/// in both cases and cannot tell them apart; the bin's item count is compared across the operation
/// instead. The row says which actually happened rather than which was asked for.
/// </para>
/// <para>
/// <b>Recycle Bins are emptied before anything is recycled into them.</b> A bin candidate's path is
/// the volume root, so ordering by depth alone would empty it last — destroying, permanently and
/// silently, every item the same run had just moved there under a promise of restorability.
/// </para>
/// <para>
/// <b>The whole batch runs on one STA thread.</b> <c>SHFileOperation</c> is a shell API and is
/// documented to require an initialised apartment. It usually appears to work from a pool thread,
/// which is the worst property a delete call can have — it means the failure mode is rare, timing
/// dependent, and discovered on someone else's machine.
/// </para>
/// </remarks>
public sealed class ReclaimExecutor : IReclaimExecutor
{
    public Task<ReclaimOutcome> ExecuteAsync(
        IReadOnlyList<ReclaimCandidate> selection,
        IProgress<ReclaimExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Count == 0)
        {
            return Task.FromResult(ReclaimOutcome.Empty);
        }

        var completion = new TaskCompletionSource<ReclaimOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Run(selection, progress, cancellationToken));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Reclaim",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    private static ReclaimOutcome Run(
        IReadOnlyList<ReclaimCandidate> selection,
        IProgress<ReclaimExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ReclaimCandidate> roots = ReclaimOverlapResolver.Roots(selection);
        IReadOnlyDictionary<string, string> containers = ReclaimOverlapResolver.ContainerByPath(selection);
        var rootPaths = new HashSet<string>(roots.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<ReclaimItemOutcome>(selection.Count);
        var rootOutcomes = new Dictionary<string, ReclaimItemOutcome>(StringComparer.OrdinalIgnoreCase);
        long freed = 0;
        int completed = 0;
        bool cancelled = false;

        // Recycle Bins FIRST, then deepest-first for everything else.
        //
        // The bin's candidate path IS the volume root, so ordering purely by descending length puts
        // it dead last — after every other item on that volume has been recycled INTO it. Emptying
        // then destroys, permanently and silently, the very items the confirmation promised were
        // restorable. Both are Safe-graded and both are ticked by the automatic SelectSafe after
        // every scan, so that was the default path, not an edge case.
        //
        // Emptying first is also what the user asked for: "get rid of what is in the bin" and "put
        // these in the bin" are two requests, and doing them in that order honours both.
        //
        // Deepest-first for the remainder still holds, so a run cancelled halfway has removed leaves
        // rather than trunks, which is the less surprising half-finished state to be left in.
        IOrderedEnumerable<ReclaimCandidate> ordered = roots
            .OrderByDescending(c => IsRecycleBin(c))
            .ThenByDescending(c => c.Path.Length);

        foreach (ReclaimCandidate candidate in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                ReclaimItemOutcome stopped = new(
                    candidate, ReclaimItemStatus.Cancelled, 0, "not reached — the run was stopped");
                outcomes.Add(stopped);
                rootOutcomes[candidate.Path] = stopped;
                continue;
            }

            ReclaimItemOutcome outcome = Remove(candidate);
            outcomes.Add(outcome);
            rootOutcomes[candidate.Path] = outcome;

            if (outcome.Removed)
            {
                freed += outcome.BytesFreed;
            }

            completed++;
            progress?.Report(new ReclaimExecutionProgress(
                completed, roots.Count, candidate.DisplayName, freed));
        }

        // Everything that was not a root went with its container — IF the container actually went.
        // Reporting "absorbed" unconditionally would turn a guard refusal into a reported success and
        // make the ViewModel delete rows for folders still sitting on disk, recoverable only by a
        // full rescan. A child inherits its container's fate.
        foreach (ReclaimCandidate candidate in selection.Where(c => !rootPaths.Contains(c.Path)))
        {
            string parent = containers.TryGetValue(candidate.Path, out string? container)
                ? container
                : "another selected item";

            bool containerRemoved =
                container is not null &&
                rootOutcomes.TryGetValue(container, out ReclaimItemOutcome? containerOutcome) &&
                containerOutcome.Removed;

            outcomes.Add(containerRemoved
                ? new ReclaimItemOutcome(candidate, ReclaimItemStatus.Absorbed, 0, $"removed with {parent}")
                : new ReclaimItemOutcome(
                    candidate,
                    cancelled ? ReclaimItemStatus.Cancelled : ReclaimItemStatus.Failed,
                    0,
                    $"still here — {parent} could not be removed"));
        }

        return new ReclaimOutcome(outcomes, cancelled);
    }

    private static bool IsRecycleBin(ReclaimCandidate candidate) =>
        string.Equals(candidate.CategoryId, "recycle-bin", StringComparison.OrdinalIgnoreCase);

    private static ReclaimItemOutcome Remove(ReclaimCandidate candidate)
    {
        ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(candidate);

        switch (verdict.Decision)
        {
            case ReclaimGuardDecision.Refuse:
                return new ReclaimItemOutcome(
                    candidate, ReclaimItemStatus.Refused, 0, verdict.Explanation);

            case ReclaimGuardDecision.AlreadyGone:
                return new ReclaimItemOutcome(
                    candidate,
                    ReclaimItemStatus.AlreadyGone,
                    0,
                    "already gone — something else removed it since the scan");
        }

        string path = verdict.CanonicalPath!;

        try
        {
            if (verdict.Kind == ReclaimTargetKind.RecycleBin)
            {
                return EmptyRecycleBin(candidate, path);
            }

            return candidate.SupportsRecycleBin
                ? Recycle(candidate, path)
                : DeletePermanently(candidate, path, verdict.Kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Failed, 0, FileSystemRemovalError.Explain(exception));
        }
    }

    private static ReclaimItemOutcome EmptyRecycleBin(ReclaimCandidate candidate, string volumeRoot)
    {
        const uint NoConfirmation = 0x1;
        const uint NoProgressUi = 0x2;
        const uint NoSound = 0x4;

        int hr = SHEmptyRecycleBinW(
            IntPtr.Zero, volumeRoot, NoConfirmation | NoProgressUi | NoSound);

        // S_OK, or "the bin was already empty" — which is a success from the user's point of view
        // even though the scan that found bytes there was reading a moment that has passed.
        const int AlreadyEmpty = unchecked((int)0x8000FFFF);

        return hr is 0 or AlreadyEmpty
            ? new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Emptied, candidate.SizeBytes,
                $"emptied the Recycle Bin on {volumeRoot.TrimEnd('\\')}")
            : new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Failed, 0,
                $"the shell would not empty the Recycle Bin (0x{hr:X8})");
    }

    private static ReclaimItemOutcome Recycle(ReclaimCandidate candidate, string path)
    {
        const uint Delete = 0x0003;
        const ushort Silent = 0x0004;
        const ushort NoConfirmation = 0x0010;
        const ushort AllowUndo = 0x0040;
        const ushort NoConfirmMkDir = 0x0200;
        const ushort NoErrorUi = 0x0400;

        var operation = new ShFileOpStruct
        {
            Func = Delete,

            // SHFileOperation takes a double-null-terminated list. One trailing null comes from the
            // marshaller; the other has to be written here, and without it the API reads past the
            // string looking for the terminator.
            From = path + '\0' + '\0',
            Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoConfirmMkDir | NoErrorUi),
        };

        long? before = TryCountRecycleBin(path);

        int result = SHFileOperationW(ref operation);

        long? after = TryCountRecycleBin(path);

        if (result != 0 || operation.AnyOperationsAborted != 0)
        {
            // These are shell codes, not Win32 codes, so they are translated by an explicit closed
            // mapping rather than FormatMessage — which would describe 0x79 as ERROR_SEM_TIMEOUT.
            return new ReclaimItemOutcome(
                candidate,
                ReclaimItemStatus.Failed,
                0,
                operation.AnyOperationsAborted != 0
                    ? "the shell stopped partway through"
                    : ShellFileOperationError.Explain(result));
        }

        // A sanity check, not the discriminator: "the shell returned success and the thing is still
        // sitting there" is odd enough to be worth catching, but it says nothing about which kind of
        // removal happened.
        if (Directory.Exists(path) || File.Exists(path))
        {
            return new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Failed, 0, "the shell reported success but it is still there");
        }

        // FOF_ALLOWUNDO is a request, not a guarantee: an item too large for the bin, or a volume
        // with recycling turned off, is deleted outright and still reported as success. Combined with
        // FOF_NOCONFIRMATION and no FOF_WANTNUKEWARNING, the shell auto-answers its own "this is too
        // big for the Recycle Bin, delete permanently?" prompt with yes.
        //
        // The original path is gone in BOTH cases — recycling MOVES the item into $Recycle.Bin — so
        // "did the path disappear" cannot tell them apart. Counting the bin across the operation can.
        bool recycled = after is long a && before is long b && a > b;

        return recycled
            ? new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Recycled, candidate.SizeBytes, "moved to the Recycle Bin")
            : new ReclaimItemOutcome(
                candidate,
                ReclaimItemStatus.Deleted,
                candidate.SizeBytes,
                before is null || after is null
                    ? "removed — could not confirm it reached the Recycle Bin"
                    : "too large for the Recycle Bin, so it was removed permanently");
    }

    /// <summary>
    /// How many items the Recycle Bin serving <paramref name="path"/>'s volume currently holds, or
    /// null when that cannot be determined.
    /// </summary>
    /// <remarks>
    /// Null is treated as "assume permanent" by the caller. Reporting an item as permanently deleted
    /// when it is really recoverable is a pleasant surprise; the reverse is the failure this whole
    /// subsystem exists to avoid.
    /// </remarks>
    private static long? TryCountRecycleBin(string path) => RecycleBinQuery.TryCountFor(path);

    private static ReclaimItemOutcome DeletePermanently(
        ReclaimCandidate candidate, string path, ReclaimTargetKind kind)
    {
        if (kind == ReclaimTargetKind.Directory)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }

        return new ReclaimItemOutcome(
            candidate, ReclaimItemStatus.Deleted, candidate.SizeBytes, "deleted permanently");
    }

    /// <summary>
    /// The shell's file-operation request block.
    /// </summary>
    /// <remarks>
    /// <b>No <c>Pack</c>.</b> The widely-copied declaration of this struct carries <c>Pack = 1</c>,
    /// which is a survival from 32-bit Windows and is wrong on x64: the native
    /// <c>SHFILEOPSTRUCTW</c> uses default alignment, so <c>pFrom</c> sits at offset 16 behind four
    /// bytes of padding after <c>wFunc</c>. Packing tightly moves it to offset 12, the shell reads a
    /// pointer straddling two fields, and the process dies with an access violation inside
    /// shell32 — no managed exception, no chance to report anything.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? rootPath, uint flags);
}
