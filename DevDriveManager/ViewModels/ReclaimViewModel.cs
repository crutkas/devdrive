using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore;
using DevDriveManager.Controls;
using DevDriveReclaim;
using DevDriveStorage;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Byte formatting for reclaim rows.
/// </summary>
/// <remarks>
/// Sizes are <see cref="long"/> throughout the reclaim subsystem while
/// <see cref="DevDriveCore.ByteSizeFormatter"/> takes <see cref="ulong"/>. Clamping at zero here
/// keeps that conversion in one place rather than scattering casts through the bindings, where a
/// stray negative would wrap to an absurd number instead of showing "0 B".
/// <para>
/// The formatter is named in full because <c>DevDriveStorage</c> has one too, and the two disagree:
/// storage counts in powers of 1000, core in powers of 1024. Reclaim totals and the volume bars sit
/// side by side in this room, so they must come from the same one.
/// </para>
/// </remarks>
internal static class ReclaimFormat
{
    public static string Bytes(long value) =>
        DevDriveCore.ByteSizeFormatter.Format(value <= 0 ? 0UL : (ulong)value);
}

/// <summary>One selectable candidate row.</summary>
/// <remarks>
/// Selection lives here rather than in a parallel set because the row is what the user ticks, and a
/// checkbox that can disagree with the model behind it is how a delete tool removes the wrong thing.
/// </remarks>
public sealed partial class ReclaimRowViewModel(ReclaimCandidate candidate) : ObservableObject
{
    public ReclaimCandidate Candidate { get; } = candidate;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Set when another selected row already contains this one, so its bytes are being counted
    /// against the parent instead. A row silently contributing nothing has to be able to say why.
    /// </summary>
    [ObservableProperty]
    public partial string? AbsorbedBy { get; set; }

    public bool IsAbsorbed => AbsorbedBy is not null;

    public string DisplayName => Candidate.DisplayName;

    public string Path => Candidate.Path;

    public string SizeText => ReclaimFormat.Bytes(Candidate.SizeBytes);

    public string RiskText => Candidate.Risk switch
    {
        ReclaimRisk.Safe => "Safe",
        ReclaimRisk.Check => "Check",
        _ => "Careful",
    };

    /// <summary>
    /// The risk tier as a colour role. Deleting is the one thing in this app the user cannot undo,
    /// so the tier is carried by a word and a colour rather than colour alone — colour is the fast
    /// read, the word is the one that survives a colour-blind user or a greyscale screenshot.
    /// </summary>
    public StatusEmphasis RiskEmphasis => Candidate.Risk switch
    {
        ReclaimRisk.Safe => StatusEmphasis.Good,
        ReclaimRisk.Check => StatusEmphasis.Warn,
        _ => StatusEmphasis.Bad,
    };

    /// <summary>
    /// What a screen reader hears for the whole row. Built here rather than in XAML so the order
    /// of the facts is a decision made once: what it is, how big, how risky.
    /// </summary>
    public string AccessibleDescription =>
        $"{DisplayName}, {SizeText}, {RiskText}"
        + (DetailText.Length > 0 ? $", {DetailText}" : string.Empty);

    public string DetailText => Candidate.Detail ?? string.Empty;

    /// <summary>Why this is or is not safe. Flattened onto the row so the inspector never has to
    /// reach through two objects to ask a question the row already knows the answer to.</summary>
    public string ReasonText => Candidate.Reason;

    public string RecoveryText => Candidate.RecoveryHint;

    public string IdleText => Candidate.DaysSinceLastUse is int days
        ? $"{days:N0} days"
        : "—";

    /// <summary>
    /// Stable across processes, unlike <see cref="string.GetHashCode()"/>, which .NET randomises per
    /// process — an automation id that changes every launch is not something a test can name. Also
    /// avoids <c>Math.Abs(int.MinValue)</c>, which throws.
    /// </summary>
    public string AutomationId =>
        $"ReclaimRow_{Candidate.CategoryId}_{StableHash(Candidate.Path):X8}";

    private static uint StableHash(string value)
    {
        uint hash = 2166136261u;
        foreach (char c in value)
        {
            hash = (hash ^ char.ToUpperInvariant(c)) * 16777619u;
        }

        return hash;
    }

    partial void OnAbsorbedByChanged(string? value) => OnPropertyChanged(nameof(IsAbsorbed));
}

/// <summary>One category in the rail.</summary>
public sealed partial class ReclaimCategoryViewModel(ReclaimCategory category) : ObservableObject
{
    public ReclaimCategory Category { get; } = category;

    public string Title => Category.Title;

    public string Glyph => Category.Glyph;

    public ObservableCollection<ReclaimRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Waiting";

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string? FailureReason { get; set; }

    [ObservableProperty]
    public partial long TotalBytes { get; set; }

    public string TotalText => TotalBytes > 0 ? ReclaimFormat.Bytes(TotalBytes) : "—";

    public string AutomationId => $"ReclaimCategory_{Category.Id}";

    /// <summary>
    /// What a screen reader announces for this row.
    /// </summary>
    /// <remarks>
    /// Without it the list item falls back to <c>ToString()</c> and announces
    /// "DevDriveManager.ViewModels.ReclaimCategoryViewModel" — the rail is unusable without sight.
    /// The size is included because it is the number that decides whether the category is worth
    /// opening, and it is otherwise carried only by a sibling TextBlock the item's name never reaches.
    /// </remarks>
    public string AutomationName => $"{Title}, {StatusText}, {TotalText}";

    partial void OnTotalBytesChanged(long value)
    {
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}

/// <summary>
/// The Reclaim room's state.
/// </summary>
/// <remarks>
/// Two properties of a real scan drive this design. It is I/O bound and slow — 174 s warm and 497 s
/// cold across two volumes on the machine this was built against — so results stream in per category
/// and cancellation is a first-class command rather than an afterthought. And categories overlap, so
/// every total a user reads goes through <see cref="ReclaimOverlapResolver"/>; a build-output row
/// inside a selected worktree is shown as absorbed rather than added twice.
/// </remarks>
public sealed partial class ReclaimViewModel : ObservableObject
{
    private readonly ReclaimEngine _engine;
    private readonly Func<ReclaimScanContext> _contextFactory;
    private readonly IVolumeProvider _volumeProvider;
    private CancellationTokenSource? _scanCts;

    /// <summary>
    /// Suppresses the per-row recompute while a bulk selection is in flight. Without it, ticking the
    /// Safe tier runs a full overlap resolve once per row — 277 resolves over 411 candidates on the
    /// machine this was measured against, which is quadratic work for one click.
    /// </summary>
    private bool _suspendRecompute;

    public ReclaimViewModel()
        : this(ReclaimRegistry.CreateDefaultEngine(), () => ReclaimRegistry.CreateMachineContext())
    {
    }

    public ReclaimViewModel(
        ReclaimEngine engine,
        Func<ReclaimScanContext> contextFactory,
        IVolumeProvider? volumeProvider = null)
    {
        _engine = engine;
        _contextFactory = contextFactory;
        _volumeProvider = volumeProvider ?? new SystemVolumeProvider();

        foreach (ReclaimCategory category in engine.Categories)
        {
            Categories.Add(new ReclaimCategoryViewModel(category));
        }

        SelectedCategory = Categories.FirstOrDefault();
        RefreshVolumeStrip();
    }

    public ObservableCollection<ReclaimCategoryViewModel> Categories { get; } = [];

    [ObservableProperty]
    public partial ReclaimCategoryViewModel? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial ReclaimRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial bool HasScanned { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Nothing scanned yet.";

    [ObservableProperty]
    public partial string? ScanError { get; set; }

    /// <summary>Bytes the current selection actually returns, nesting resolved.</summary>
    [ObservableProperty]
    public partial long SelectedBytes { get; set; }

    public string SelectedBytesText => ReclaimFormat.Bytes(SelectedBytes);

    /// <summary>Everything found, nesting resolved. The honest headline.</summary>
    [ObservableProperty]
    public partial long FoundBytes { get; set; }

    public string FoundBytesText => ReclaimFormat.Bytes(FoundBytes);

    /// <summary>
    /// The one line that says where the user stands: how much of what was found is currently ticked.
    /// Kept as prose rather than two numbers in two corners because the ratio is the decision, and a
    /// ratio the reader has to compute themselves is one they will not compute.
    /// </summary>
    public string SelectionSummary => !HasScanned
        ? "Nothing scanned yet"
        : FoundBytes <= 0
            ? "Nothing found"
            : SelectedBytes <= 0
                ? $"Nothing selected of {FoundBytesText} found"
                : $"{SelectedBytesText} selected of {FoundBytesText} found";

    public ObservableCollection<ReclaimVolumeImpact> VolumeImpacts { get; } = [];

    /// <summary>
    /// How everything found splits across the three risk tiers. Shown under the category list,
    /// because "218 GB reclaimable" and "131 GB of it is regenerable with no decision to make" are
    /// very different facts and only the second one tells you whether to keep reading.
    /// </summary>
    [ObservableProperty]
    public partial ReclaimRiskMix FoundRiskMix { get; set; } = ReclaimRiskMix.Empty;

    /// <summary>The same split over the current selection — what you are actually about to do.</summary>
    [ObservableProperty]
    public partial ReclaimRiskMix SelectedRiskMix { get; set; } = ReclaimRiskMix.Empty;

    /// <summary>Where the selected bytes live, e.g. "105.1 GB on G: · 26.5 GB on C:".</summary>
    [ObservableProperty]
    public partial string VolumeSplitText { get; set; } = string.Empty;

    /// <summary>
    /// How many rows are ticked. Paired with <see cref="SelectedBytesText"/> in the totals card,
    /// where the tier cards' own counts cannot be summed — tiers overlap, the total does not.
    /// </summary>
    public string SelectedCountText
    {
        get
        {
            int count = SelectedRows.Count();
            return count == 1 ? "1 item queued" : $"{count:N0} items queued";
        }
    }

    /// <summary>
    /// The volume context strip. Reclaimable bytes track the current <i>selection</i>, not everything
    /// found, so the bars answer "what will this machine look like if I press the button" rather than
    /// "what did the scan turn up" — the latter is already the headline number.
    /// </summary>
    public ObservableCollection<VolumeStripEntry> VolumeStripEntries { get; } = [];

    public IEnumerable<ReclaimRowViewModel> AllRows => Categories.SelectMany(c => c.Rows);

    public IEnumerable<ReclaimRowViewModel> SelectedRows => AllRows.Where(r => r.IsSelected);

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;

        IsScanning = true;
        ScanError = null;
        StatusText = "Scanning…";

        foreach (ReclaimCategoryViewModel category in Categories)
        {
            category.Rows.Clear();
            category.IsScanning = true;
            category.FailureReason = null;
            category.StatusText = "Scanning…";
            category.TotalBytes = 0;
        }

        RecomputeTotals();

        var progress = new Progress<ReclaimScanProgress>(OnProgress);

        try
        {
            ReclaimResult result = await Task.Run(
                () => _engine.ScanAsync(_contextFactory(), progress, token), token);

            Apply(result);
            HasScanned = true;
            StatusText = result.FailedCategories.Length > 0
                ? $"Found {ReclaimFormat.Bytes(result.TotalBytes)} — {result.FailedCategories.Length} " +
                  "category could not be checked, so this is a floor."
                : $"Found {ReclaimFormat.Bytes(result.TotalBytes)}.";
        }
        catch (OperationCanceledException)
        {
            // Cancelling is a normal outcome on a scan this long, not an error. Whatever streamed in
            // before the cancel stays on screen; throwing it away would punish the user for stopping.
            StatusText = "Scan cancelled.";
            foreach (ReclaimCategoryViewModel category in Categories.Where(c => c.IsScanning))
            {
                category.StatusText = "Cancelled";
            }
        }
        catch (Exception exception)
        {
            ScanError = exception.Message;
            StatusText = "The scan could not finish.";
        }
        finally
        {
            IsScanning = false;
            foreach (ReclaimCategoryViewModel category in Categories)
            {
                category.IsScanning = false;
            }
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>Ticks exactly the Safe tier. Never Check, never Careful.</summary>
    [RelayCommand]
    private void SelectSafe() =>
        SetSelection(row => row.Candidate.Risk == ReclaimRisk.Safe);

    [RelayCommand]
    private void ClearSelection() => SetSelection(_ => false);

    private void SetSelection(Func<ReclaimRowViewModel, bool> predicate)
    {
        _suspendRecompute = true;
        try
        {
            foreach (ReclaimRowViewModel row in AllRows)
            {
                row.IsSelected = predicate(row);
            }
        }
        finally
        {
            _suspendRecompute = false;
        }

        RecomputeTotals();
    }

    private void OnProgress(ReclaimScanProgress progress)
    {
        ReclaimCategoryViewModel? category =
            Categories.FirstOrDefault(c => c.Category.Id == progress.CategoryId);

        if (category is not null)
        {
            category.StatusText = progress.Status;
        }
    }

    private void Apply(ReclaimResult result)
    {
        foreach (ReclaimCategoryResult categoryResult in result.Categories)
        {
            ReclaimCategoryViewModel? category =
                Categories.FirstOrDefault(c => c.Category.Id == categoryResult.Category.Id);

            if (category is null)
            {
                continue;
            }

            category.Rows.Clear();
            foreach (ReclaimCandidate candidate in categoryResult.Candidates)
            {
                var row = new ReclaimRowViewModel(candidate);
                row.PropertyChanged += OnRowPropertyChanged;
                category.Rows.Add(row);
            }

            category.TotalBytes = categoryResult.TotalBytes;
            category.FailureReason = categoryResult.FailureReason;
            category.StatusText = categoryResult.Succeeded
                ? $"{categoryResult.Candidates.Length:N0} found · {categoryResult.Elapsed.TotalSeconds:N1}s"
                : "Could not be checked";
        }

        FoundBytes = result.TotalBytes;
        SelectSafe();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReclaimRowViewModel.IsSelected))
        {
            RecomputeTotals();
        }
    }

    /// <summary>
    /// Recomputes every number the user reads, through the overlap resolver. Selecting a worktree and
    /// the obj folder inside it must show the worktree's size once — the arithmetic that made a real
    /// scan claim 812 GB when only 493 GB was reclaimable.
    /// </summary>
    /// <summary>
    /// What each risk tier would give you, nesting resolved <i>within</i> each tier.
    /// </summary>
    /// <remarks>
    /// Deliberately not a strict partition of the headline total. Resolving across tiers would
    /// attribute a safe <c>obj</c> folder's bytes to the careful worktree that contains it, and the
    /// safe tier would then read smaller than what "Select safe only" actually hands you — the found
    /// summary would be understating the primary action by a factor of nearly two on this machine.
    /// <para>
    /// Resolving inside each tier instead makes every tier answer the question the user is really
    /// asking of it: if I took this tier and nothing else, what do I get. That is the number the
    /// button delivers, so that is the number to show. The cost is that tiers can overlap and need
    /// not sum to the headline, which is why the bar is labelled as a comparison rather than as a
    /// breakdown.
    /// </para>
    /// </remarks>
    private static ReclaimRiskMix RiskMixOf(IEnumerable<ReclaimCandidate> candidates)
    {
        List<ReclaimCandidate> all = [.. candidates];

        (long Bytes, int Count) Tier(ReclaimRisk risk)
        {
            IReadOnlyList<ReclaimCandidate> roots =
                ReclaimOverlapResolver.Roots(all.Where(c => c.Risk == risk));
            return (roots.Sum(r => r.SizeBytes), roots.Count);
        }

        (long safe, int safeCount) = Tier(ReclaimRisk.Safe);
        (long check, int checkCount) = Tier(ReclaimRisk.Check);
        (long careful, int carefulCount) = Tier(ReclaimRisk.Careful);

        return new ReclaimRiskMix(safe, check, careful, safeCount, checkCount, carefulCount);
    }

    private void RecomputeTotals()
    {
        if (_suspendRecompute)
        {
            return;
        }

        List<ReclaimRowViewModel> selected = [.. SelectedRows];
        List<ReclaimCandidate> candidates = [.. selected.Select(r => r.Candidate)];

        SelectedBytes = ReclaimOverlapResolver.ReclaimableBytes(candidates);
        OnPropertyChanged(nameof(SelectedBytesText));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectionSummary));

        SelectedRiskMix = RiskMixOf(candidates);
        FoundRiskMix = RiskMixOf(AllRows.Select(r => r.Candidate));

        IReadOnlyDictionary<string, string> containers =
            ReclaimOverlapResolver.ContainerByPath(candidates);

        foreach (ReclaimRowViewModel row in AllRows)
        {
            row.AbsorbedBy = row.IsSelected && containers.TryGetValue(row.Path, out string? parent)
                ? parent
                : null;
        }

        IReadOnlyDictionary<string, long> byVolume =
            ReclaimOverlapResolver.ReclaimableBytesByVolume(candidates);

        // "105.1 GB on G: · 26.5 GB on C:" — the split matters because freeing 130 GB spread over two
        // volumes does not solve a problem that lives on one of them.
        VolumeSplitText = byVolume.Count == 0
            ? string.Empty
            : string.Join(
                " · ",
                byVolume
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{ReclaimFormat.Bytes(kv.Value)} on {kv.Key.TrimEnd('\\')}"));

        RefreshVolumeStrip(byVolume);
    }

    /// <summary>
    /// Rebuilds the context strip and the per-volume impact rows. Volumes are re-enumerated each time
    /// rather than cached because a reclaim run changes free space, and a strip showing pre-scan free
    /// space beside post-scan findings would be quietly self-contradictory. The impact rows are built
    /// here, from the same enumeration, so the after-picture bars can never disagree with the strip
    /// above them about how big a volume is.
    /// </summary>
    private void RefreshVolumeStrip(IReadOnlyDictionary<string, long>? reclaimableByVolume = null)
    {
        IReadOnlyList<StorageVolume> volumes;
        try
        {
            volumes = _volumeProvider.GetFixedVolumes();
        }
        catch (IOException)
        {
            // A volume disappearing mid-session is a real event, not a bug. The room is still usable
            // without its strip, so this degrades rather than taking the page down.
            RebuildImpacts(reclaimableByVolume, []);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            RebuildImpacts(reclaimableByVolume, []);
            return;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        VolumeStripEntries.Clear();
        foreach (VolumeStripEntry entry in VolumeStrip.Build(volumes, systemRoot, reclaimableByVolume))
        {
            VolumeStripEntries.Add(entry);
        }

        RebuildImpacts(reclaimableByVolume, volumes);
    }

    /// <summary>
    /// A volume the enumeration could not describe is dropped rather than drawn with invented
    /// geometry — a bar with no capacity behind it is a lie told confidently, which is worse in a
    /// delete tool than a missing row.
    /// </summary>
    private void RebuildImpacts(
        IReadOnlyDictionary<string, long>? reclaimableByVolume,
        IReadOnlyList<StorageVolume> volumes)
    {
        VolumeImpacts.Clear();

        // Driven by the volumes, not by the selection. A volume that would free nothing still belongs
        // in the after-picture: "C: is unchanged by this" is a fact the user needs when deciding
        // whether the selection solves their actual problem, and a row that appears and disappears as
        // boxes are ticked makes the card jump under the cursor.
        foreach (StorageVolume volume in volumes.OrderBy(v => v.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            if (volume.CapacityBytes <= 0)
            {
                continue;
            }

            long bytes = 0;
            reclaimableByVolume?.TryGetValue(volume.RootPath, out bytes);

            long freed = Math.Clamp(bytes, 0, volume.UsedBytes);
            long stillUsed = Math.Max(0, volume.UsedBytes - freed);
            long freeAfter = volume.FreeBytes + freed;

            VolumeImpacts.Add(new ReclaimVolumeImpact(
                volume.RootPath,
                string.IsNullOrEmpty(volume.DriveLetter) ? "?" : volume.DriveLetter,
                volume.DisplayName,
                VolumeCapacity.CaptionFor(volume),
                freed,
                VolumeCapacity.AfterReclaim(volume, freed),
                ReclaimFormat.Bytes(volume.FreeBytes),
                ReclaimFormat.Bytes(freeAfter),
                $"{ReclaimFormat.Bytes(stillUsed)} still in use of {ReclaimFormat.Bytes(volume.CapacityBytes)}"));
        }
    }

    partial void OnFoundBytesChanged(long value)
    {
        OnPropertyChanged(nameof(FoundBytesText));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    partial void OnHasScannedChanged(bool value) => OnPropertyChanged(nameof(SelectionSummary));
}

/// <summary>
/// One volume's after-picture: what it looks like once the current selection is reclaimed.
/// </summary>
/// <remarks>
/// A single bar, not a before and an after. The freed band sits between the space still in use and
/// the space that was already free — exactly where those bytes are about to move — so the reader
/// sees the delta in place instead of diffing two pictures stacked on top of each other.
/// </remarks>
public sealed record ReclaimVolumeImpact(
    string VolumeRoot,
    string Letter,
    string Name,
    string Caption,
    long Bytes,
    VolumeCapacityResult Bar,
    string FreeBeforeText,
    string FreeAfterText,
    string StillInUseText)
{
    public string BytesText => ReclaimFormat.Bytes(Bytes);

    /// <summary>Free space before and after, as one phrase. The arrow is the whole point of the row.</summary>
    public string FreeTransitionText => $"{FreeBeforeText} → {FreeAfterText} free";

    /// <summary>
    /// Right-hand end of the footer line. Says nothing selected rather than "+0 B", because zero
    /// bytes freed is a state the user chose, not a measurement.
    /// </summary>
    public string GainText => Bytes > 0 ? $"+{BytesText} freed" : "nothing selected here";

    /// <summary>Reads as one sentence to a screen reader, which cannot see a bar.</summary>
    public string AccessibleDescription =>
        $"{Name} ({Letter}:), {StillInUseText}, {FreeBeforeText} free before, {FreeAfterText} free after";

    public string AutomationId => $"ReclaimImpact_{VolumeRoot.TrimEnd('\\', ':')}";
}

/// <summary>
/// A pile of reclaimable bytes broken down by risk tier, with item counts.
/// </summary>
/// <remarks>
/// Counts travel with the bytes because the two together are what makes a tier actionable: "17.4 GB
/// careful" is worrying, "17.4 GB careful across 5 items" is a short afternoon.
/// </remarks>
public sealed record ReclaimRiskMix(
    long SafeBytes,
    long CheckBytes,
    long CarefulBytes,
    int SafeCount,
    int CheckCount,
    int CarefulCount)
{
    public static readonly ReclaimRiskMix Empty = new(0, 0, 0, 0, 0, 0);

    public long TotalBytes => SafeBytes + CheckBytes + CarefulBytes;

    public int TotalCount => SafeCount + CheckCount + CarefulCount;

    public bool HasAny => TotalBytes > 0;

    public VolumeCapacityResult Bar => VolumeCapacity.ByRisk(SafeBytes, CheckBytes, CarefulBytes);

    public string SafeText => ReclaimFormat.Bytes(SafeBytes);

    public string CheckText => ReclaimFormat.Bytes(CheckBytes);

    public string CarefulText => ReclaimFormat.Bytes(CarefulBytes);

    public string SafeCountText => Items(SafeCount);

    public string CheckCountText => Items(CheckCount);

    public string CarefulCountText => Items(CarefulCount);

    /// <summary>
    /// The bar's label. States that this compares tiers rather than dividing a total, because the
    /// tiers can overlap — a safe folder inside a careful one is genuinely available under either
    /// choice, and pretending otherwise would understate the safe tier.
    /// </summary>
    public string Headline => HasAny ? "What each tier would give you" : "Nothing found yet";

    private static string Items(int count) => count == 1 ? "1 item" : $"{count:N0} items";
}
