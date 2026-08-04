using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore;
using DevDriveReclaim;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Byte formatting for reclaim rows.
/// </summary>
/// <remarks>
/// Sizes are <see cref="long"/> throughout the reclaim subsystem while
/// <see cref="ByteSizeFormatter"/> takes <see cref="ulong"/>. Clamping at zero here keeps that
/// conversion in one place rather than scattering casts through the bindings, where a stray negative
/// would wrap to an absurd number instead of showing "0 B".
/// </remarks>
internal static class ReclaimFormat
{
    public static string Bytes(long value) =>
        ByteSizeFormatter.Format(value <= 0 ? 0UL : (ulong)value);
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

    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(TotalText));
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

    public ReclaimViewModel(ReclaimEngine engine, Func<ReclaimScanContext> contextFactory)
    {
        _engine = engine;
        _contextFactory = contextFactory;

        foreach (ReclaimCategory category in engine.Categories)
        {
            Categories.Add(new ReclaimCategoryViewModel(category));
        }

        SelectedCategory = Categories.FirstOrDefault();
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

    public ObservableCollection<ReclaimVolumeImpact> VolumeImpacts { get; } = [];

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

        VolumeImpacts.Clear();
        foreach ((string volume, long bytes) in byVolume.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            VolumeImpacts.Add(new ReclaimVolumeImpact(volume, bytes));
        }
    }

    partial void OnFoundBytesChanged(long value) => OnPropertyChanged(nameof(FoundBytesText));
}

/// <summary>What one volume gets back from the current selection.</summary>
public sealed record ReclaimVolumeImpact(string VolumeRoot, long Bytes)
{
    public string BytesText => ReclaimFormat.Bytes(Bytes);

    public string AutomationId => $"ReclaimImpact_{VolumeRoot.TrimEnd('\\', ':')}";
}
