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
/// <b>The Recycle Bin is preferred, and a promise of it is kept.</b> <c>FOF_ALLOWUNDO</c> silently
/// degrades to a permanent delete when an item is too large for the bin or the volume has recycling
/// disabled. So every item that asks for the bin is checked against the volume's bin settings first
/// (<see cref="RecycleBinPreflight"/>), and one the shell would not recycle is <em>left in place</em>
/// and reported, never nuked: the confirmation told the user it could be restored, and only
/// <see cref="ReclaimPlan.ConsentedSelection"/> — which marks items the confirmation named as permanent —
/// licenses a permanent delete. For the cases no setting predicts, <c>FOF_WANTNUKEWARNING</c> makes the
/// shell ask before destroying anything it could not recycle. Recycling <em>moves</em> the item into
/// <c>$Recycle.Bin</c>, so the original path is gone either way; the bin's item count is compared
/// across the operation to say which actually happened.
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
    private readonly Func<string, RecycleBinVolumeSettings?> _readBinSettings;

    public ReclaimExecutor()
        : this(RecycleBinPreflight.ReadSettings)
    {
    }

    /// <param name="readBinSettings">
    /// Where the Recycle Bin settings come from. Injectable so tests can describe a bin that would
    /// refuse an item without changing the developer's real settings.
    /// </param>
    public ReclaimExecutor(Func<string, RecycleBinVolumeSettings?> readBinSettings)
    {
        ArgumentNullException.ThrowIfNull(readBinSettings);
        _readBinSettings = readBinSettings;
    }

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

        // Fresh per run: the settings are read again rather than trusted from the confirmation, in
        // case the bin was resized or switched off while the dialog was open.
        Func<ReclaimCandidate, RecycleBinCheck> preflight = RecycleBinPreflight.CreateChecker(_readBinSettings);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Run(selection, preflight, progress, cancellationToken));
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
        Func<ReclaimCandidate, RecycleBinCheck> preflight,
        IProgress<ReclaimExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ReclaimCandidate> roots = ReclaimOverlapResolver.Roots(selection);
        IReadOnlyDictionary<string, string> containers = ReclaimOverlapResolver.ContainerByPath(selection);
        var rootPaths = new HashSet<string>(roots.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<ReclaimItemOutcome>(selection.Count);
        var rootOutcomes = new Dictionary<string, ReclaimItemOutcome>(StringComparer.OrdinalIgnoreCase);
        long freed = 0;
        long recycled = 0;
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

            ReclaimItemOutcome outcome = Remove(candidate, preflight);
            outcomes.Add(outcome);
            rootOutcomes[candidate.Path] = outcome;

            freed += outcome.BytesFreed;
            recycled += outcome.BytesRecycled;

            completed++;
            progress?.Report(new ReclaimExecutionProgress(
                completed, roots.Count, candidate.DisplayName, freed, recycled));
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

            ReclaimItemOutcome? containerOutcome =
                container is not null &&
                rootOutcomes.TryGetValue(container, out ReclaimItemOutcome? found)
                    ? found
                    : null;

            // Its container's fate, not the run's. Reading the run-wide flag meant a child whose
            // container was reached and genuinely failed got labelled Cancelled purely because some
            // unrelated item later in the ordering was never reached — which drops it out of
            // outcome.Failures, so the row keeps no failure text and stays ticked while the executor
            // knew the exact reason all along.
            outcomes.Add(containerOutcome switch
            {
                { Removed: true } => new ReclaimItemOutcome(
                    candidate, ReclaimItemStatus.Absorbed, 0, $"removed with {parent}"),
                { Status: ReclaimItemStatus.Cancelled } => new ReclaimItemOutcome(
                    candidate, ReclaimItemStatus.Cancelled, 0, "not reached — the run was stopped"),
                not null => new ReclaimItemOutcome(
                    candidate, ReclaimItemStatus.Failed, 0,
                    $"still here — {parent} could not be removed"),
                null => new ReclaimItemOutcome(
                    candidate,
                    cancelled ? ReclaimItemStatus.Cancelled : ReclaimItemStatus.Failed,
                    0,
                    $"still here — {parent} could not be removed"),
            });
        }

        return new ReclaimOutcome(outcomes, cancelled);
    }

    private static bool IsRecycleBin(ReclaimCandidate candidate) =>
        string.Equals(candidate.CategoryId, "recycle-bin", StringComparison.OrdinalIgnoreCase);

    private static ReclaimItemOutcome Remove(
        ReclaimCandidate candidate, Func<ReclaimCandidate, RecycleBinCheck> preflight)
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

            if (!candidate.SupportsRecycleBin)
            {
                return DeletePermanently(candidate, path, verdict.Kind);
            }

            // The confirmation promised this one could be restored. If the bin would not take it,
            // keeping it is the only answer that honours that promise; deleting it is not ours to
            // decide after the fact.
            RecycleBinCheck check = preflight(candidate);
            if (check.WillDeletePermanently)
            {
                return new ReclaimItemOutcome(
                    candidate,
                    ReclaimItemStatus.Failed,
                    0,
                    $"kept — {check.Reason}, and it was promised to go to the Recycle Bin");
            }

            return Recycle(candidate, path);
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

        if (hr == 0)
        {
            return new ReclaimItemOutcome(
                candidate, ReclaimItemStatus.Emptied, candidate.SizeBytes,
                $"emptied the Recycle Bin on {volumeRoot.TrimEnd('\\')}");
        }

        // 0x8000FFFF is E_UNEXPECTED, the generic catastrophic-failure HRESULT. SHEmptyRecycleBin
        // documents S_OK for success and "a COM-defined error value" otherwise; there is no
        // documented code for "it was already empty", so reading this one as success was a guess —
        // and an expensive one, because Emptied credits the bin's whole size to the run's headline
        // and this is the one path here with no undo. Ask instead of assume: the sibling Recycle
        // path already verifies its claim with SHQueryRecycleBin, and this borrows the same check.
        bool empty = RecycleBinQuery.TryQuery(volumeRoot, out _, out long remaining) && remaining == 0;

        return empty
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

        // The backstop behind the preflight. FOF_NOCONFIRMATION on its own makes the shell answer its
        // own "this is too big for the Recycle Bin — delete permanently?" prompt with yes. This flag
        // partially overrides that, for that one question: anything the preflight did not predict is
        // put to the user before it is destroyed, rather than destroyed and reported afterwards.
        const ushort WantNukeWarning = 0x4000;

        var operation = new ShFileOpStruct
        {
            Func = Delete,

            // SHFileOperation takes a double-null-terminated list. One trailing null comes from the
            // marshaller; the other has to be written here, and without it the API reads past the
            // string looking for the terminator.
            From = path + '\0' + '\0',
            Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoConfirmMkDir | NoErrorUi | WantNukeWarning),
        };

        long? before = TryCountRecycleBin(path);

        int result = SHFileOperationW(ref operation);

        long? after = TryCountRecycleBin(path);

        if (result != 0 || operation.AnyOperationsAborted != 0)
        {
            // These are shell codes, not Win32 codes, so they are translated by an explicit closed
            // mapping rather than FormatMessage — which would describe 0x79 as ERROR_SEM_TIMEOUT.
            // Declining the shell's permanent-delete prompt lands here too, as an aborted operation.
            return new ReclaimItemOutcome(
                candidate,
                ReclaimItemStatus.Failed,
                0,
                operation.AnyOperationsAborted != 0
                    ? "kept — the operation was stopped (declining Windows' permanent-delete prompt does this)"
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

        // The original path is gone in BOTH cases — recycling MOVES the item into $Recycle.Bin — so
        // "did the path disappear" cannot tell them apart. Counting the bin across the operation can.
        // A permanent delete reaching this point went through the shell's own prompt and was agreed to.
        bool recycled = after is long a && before is long b && a > b;

        return recycled
            ? new ReclaimItemOutcome(
                candidate,
                ReclaimItemStatus.Recycled,
                candidate.SizeBytes,
                "moved to the Recycle Bin — frees space when the bin is emptied")
            : new ReclaimItemOutcome(
                candidate,
                ReclaimItemStatus.Deleted,
                candidate.SizeBytes,
                before is null || after is null
                    ? "removed — could not confirm it reached the Recycle Bin"
                    : "could not go to the Recycle Bin, so it was removed permanently after Windows asked");
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
    /// <para>
    /// That is true <em>because this app is 64-bit only</em>. <c>shellapi.h</c> wraps this struct in
    /// <c>#include &lt;pshpack1.h&gt;</c> under <c>#if !defined(_WIN64)</c>, so the copied
    /// <c>Pack = 1</c> is correct on x86 and only wrong here. Every project pins
    /// <c>&lt;Platforms&gt;x64;ARM64&lt;/Platforms&gt;</c>; if x86 is ever added, this declaration
    /// and <c>SHQUERYRBINFO</c> both need <c>Pack = 1</c> under that target.
    /// </para>
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
