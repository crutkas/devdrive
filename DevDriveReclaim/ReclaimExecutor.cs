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
/// disabled, so afterwards the path is checked: still there means the shell recycled it, gone means
/// the shell deleted it. The row says which actually happened rather than which was asked for.
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
        long freed = 0;
        int completed = 0;
        bool cancelled = false;

        // Deepest first. The shell is perfectly happy either way once nesting is resolved, but a run
        // that is cancelled halfway has then removed leaves rather than trunks, which is the less
        // surprising half-finished state to be left in.
        foreach (ReclaimCandidate candidate in roots.OrderByDescending(c => c.Path.Length))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                outcomes.Add(new ReclaimItemOutcome(
                    candidate, ReclaimItemStatus.Cancelled, 0, "not reached — the run was stopped"));
                continue;
            }

            ReclaimItemOutcome outcome = Remove(candidate);
            outcomes.Add(outcome);

            if (outcome.Removed)
            {
                freed += outcome.BytesFreed;
            }

            completed++;
            progress?.Report(new ReclaimExecutionProgress(
                completed, roots.Count, candidate.DisplayName, freed));
        }

        // Everything that was not a root went with its container. Reported rather than dropped: the
        // user ticked it, so the run owes it an answer, and "absorbed" is a different fact from
        // "deleted" even though both end with the bytes gone.
        foreach (ReclaimCandidate candidate in selection.Where(c => !rootPaths.Contains(c.Path)))
        {
            string parent = containers.TryGetValue(candidate.Path, out string? container)
                ? container
                : "another selected item";

            outcomes.Add(new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Absorbed, 0, $"removed with {parent}"));
        }

        return new ReclaimOutcome(outcomes, cancelled);
    }

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
            return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Failed, 0, exception.Message);
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

        int result = SHFileOperationW(ref operation);

        if (result != 0 || operation.AnyOperationsAborted != 0)
        {
            // 0x71 (DE_SAMEFILE) and friends are shell-specific and not Win32 errors, so the code is
            // reported raw rather than run through a message lookup that would invent a description.
            return new ReclaimItemOutcome(
                candidate,
                ReclaimItemStatus.Failed,
                0,
                operation.AnyOperationsAborted != 0
                    ? "the shell stopped partway through"
                    : $"the shell could not remove it (0x{result:X})");
        }

        // FOF_ALLOWUNDO is a request, not a guarantee: an item too large for the bin, or a volume
        // with recycling turned off, is deleted outright and reported as success. Which one happened
        // is only knowable by looking, and the user is owed the truth about whether this is undoable.
        bool stillThere = Directory.Exists(path) || File.Exists(path);

        return stillThere
            ? new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Failed, 0, "the shell reported success but it is still there")
            : new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Recycled, candidate.SizeBytes, "moved to the Recycle Bin");
    }

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
