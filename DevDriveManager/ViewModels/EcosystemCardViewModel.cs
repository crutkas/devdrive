using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DevDriveCore;
using DevDriveCore.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One per-ecosystem card (Concept A). It is a pure ADAPTER over the existing engines — it does NOT
/// re-implement detection, moving, or benchmarking. It fuses, for a single language ecosystem:
/// <list type="bullet">
///   <item>the detected member tool rows (<see cref="PackageCacheRowViewModel"/>, owned by
///   <c>PackageCachesViewModel</c>, which reuse the real reversible move coordinator + the new Map/remap path),</item>
///   <item>where each cache lives (an aggregated C: / Dev Drive location chip), and</item>
///   <item>the measured speed-up — ONLY when a real workload benchmark exists for this ecosystem
///   (Node → npm, .NET → dotnet, Rust → cargo). For every other ecosystem there is no workload, so the
///   card shows the move action only plus an honest "benchmark not available" note — never a fabricated number.</item>
/// </list>
/// UI-agnostic (CommunityToolkit.Mvvm + DevDriveCore only) so the test project can link and exercise it.
/// </summary>
public partial class EcosystemCardViewModel : ObservableObject
{
    private readonly EcosystemDefinition _definition;
    private readonly char _devLetter;
    private readonly char _systemLetter;
    private readonly bool _hasDevDrive;

    public EcosystemCardViewModel(
        EcosystemDefinition definition,
        IReadOnlyList<PackageCacheRowViewModel> rows,
        PerfSuiteRowViewModel? benchmark,
        char devLetter,
        char systemLetter,
        bool hasDevDrive = true)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Rows = rows ?? Array.Empty<PackageCacheRowViewModel>();
        Benchmark = _definition.HasBenchmark ? benchmark : null;
        _devLetter = devLetter;
        _systemLetter = systemLetter;
        _hasDevDrive = hasDevDrive;

        ToolsLine = _definition.Subtitle ?? string.Join(" \u00B7 ", _definition.ToolNames);
        AutomationId = $"Ecosystem_{Token(_definition.Name)}";

        foreach (PackageCacheRowViewModel row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        Recompute();
    }

    /// <summary>Ecosystem display name, e.g. "Node".</summary>
    public string Name => _definition.Name;

    /// <summary>Segoe Fluent Icons glyph for the card header (no emoji).</summary>
    public string Glyph => _definition.Glyph;

    /// <summary>Member tool names joined for the card subtitle, e.g. "npm · Yarn · pnpm · Bun · Deno".</summary>
    public string ToolsLine { get; }

    /// <summary>Stable AutomationId for the card root, e.g. "Ecosystem_Node".</summary>
    public string AutomationId { get; }

    /// <summary>
    /// True for a benchmark-only card (Git): the package-cache UI (member rows, location chip, status
    /// line, Map path) is suppressed and only the measured benchmark is shown.
    /// </summary>
    public bool IsBenchmarkOnly => _definition.IsBenchmarkOnly;

    /// <summary>True when the card should start expanded — a benchmark-only card always, else when detected.</summary>
    public bool ShouldExpand => IsBenchmarkOnly || IsDetected;

    /// <summary>The member tool rows (real move / Map / Move &amp; remap live here — reused verbatim).</summary>
    public IReadOnlyList<PackageCacheRowViewModel> Rows { get; }

    /// <summary>
    /// The matching workload benchmark row, or <c>null</c> when this ecosystem has no workload. Bound only
    /// when <see cref="HasBenchmark"/> is true — its bars + "Run test" come straight from the suite engine.
    /// </summary>
    public PerfSuiteRowViewModel? Benchmark { get; }

    /// <summary>True only when a REAL workload benchmark backs this ecosystem (Node / .NET / Rust).</summary>
    public bool HasBenchmark => _definition.HasBenchmark && Benchmark is not null;

    /// <summary>True when there is NO benchmark — drives the honest "not available yet" note (never a fake number).</summary>
    public bool ShowNoBenchmarkNote => !HasBenchmark;

    /// <summary>Honest copy shown in place of a benchmark for ecosystems without a workload.</summary>
    public string NoBenchmarkNote { get; } = "Build benchmark not available for this tool yet.";

    // ---- Aggregated, recomputed-from-rows display ------------------------------------------------

    /// <summary>Aggregated location chip text, e.g. "On your Dev Drive" / "On C: · 1.2 GB" / "Not detected".</summary>
    [ObservableProperty]
    public partial string LocationText { get; set; } = string.Empty;

    /// <summary>"dev" | "system" | "notfound" — drives the chip colour (accent = Dev Drive, dark neutral = system drive).</summary>
    [ObservableProperty]
    public partial string LocationKind { get; set; } = "notfound";

    /// <summary>One short, honest status line for the card, e.g. "2 of 5 tools on your Dev Drive".</summary>
    [ObservableProperty]
    public partial string StatusSummary { get; set; } = string.Empty;

    /// <summary>True when at least one member tool was detected (or has since been mapped/moved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldExpand))]
    public partial bool IsDetected { get; set; }

    /// <summary>Number of member tools currently on the Dev Drive.</summary>
    [ObservableProperty]
    public partial int OnDevDriveCount { get; set; }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    private void Recompute()
    {
        int movedToDev = Rows.Count(r => r.IsSet);
        int onSystemMovable = Rows.Count(r => r.CanMove);
        int detectedOnSystem = Rows.Count(r => r.Info.Detected && !r.IsSet && !r.IsMapped);
        int mappedOnDev = Rows.Count(r => r.IsMapped && r.IsMappedToDevDrive);
        int mappedOnSystem = Rows.Count(r => r.IsMapped) - mappedOnDev;

        // "On the Dev Drive" = moved there OR mapped to a folder that lives on it; "on the system drive" =
        // still-movable on C: OR mapped to a non-Dev folder. (A mapped row is NOT inherently on the Dev
        // Drive — it points wherever the user chose.)
        int onDev = movedToDev + mappedOnDev;
        int onSystem = detectedOnSystem + mappedOnSystem;
        int tracked = Rows.Count(r => r.Info.Detected || r.IsSet || r.IsMapped);

        ulong movableBytes = 0UL;
        foreach (PackageCacheRowViewModel row in Rows.Where(r => r.CanMove))
        {
            movableBytes += row.SizeBytes;
        }

        OnDevDriveCount = onDev;
        IsDetected = tracked > 0;

        // Location chip: surface the most actionable state honestly. A still-movable cache on C: wins
        // (there's an upside to act on); then on-Dev (moved OR mapped onto the Dev Drive); then a cache
        // mapped to a non-Dev folder (on C:, nothing to move); else not detected.
        if (onSystemMovable > 0)
        {
            string size = ByteSizeFormatter.Format(movableBytes);
            LocationKind = "system";
            LocationText = movableBytes > 0UL
                ? $"On {_systemLetter}: \u00B7 {size}"
                : $"On {_systemLetter}:";
        }
        else if (onDev > 0)
        {
            LocationKind = "dev";
            LocationText = "On your Dev Drive";
        }
        else if (onSystem > 0)
        {
            LocationKind = "system";
            LocationText = $"On {_systemLetter}:";
        }
        else
        {
            LocationKind = "notfound";
            LocationText = "Not detected";
        }

        StatusSummary = BuildStatusSummary(onDev, onSystem, mappedOnDev + mappedOnSystem, tracked);
    }

    private string BuildStatusSummary(int onDev, int onSystem, int mapped, int tracked)
    {
        // Single-tool ecosystems (.NET, Rust, Java, Go, C++, Dart) read better described directly than as
        // "1 of 1"; multi-tool ecosystems (Node, Python) get the honest "N of M" fraction.
        if (Rows.Count <= 1)
        {
            if (onDev > 0)
            {
                return "On your Dev Drive.";
            }

            if (onSystem > 0)
            {
                return _hasDevDrive
                    ? $"On {_systemLetter}: \u2014 movable to your Dev Drive."
                    : "Detected on this PC.";
            }

            if (mapped > 0)
            {
                return "Mapped to the folder you chose.";
            }

            return _hasDevDrive
                ? "Not detected on this PC \u2014 map it if you use it."
                : "Not detected on this PC.";
        }

        if (tracked == 0)
        {
            return _hasDevDrive
                ? "Not detected on this PC \u2014 map any you use."
                : "Not detected on this PC.";
        }

        if (!_hasDevDrive)
        {
            return $"{tracked} tools detected on this PC.";
        }

        string fraction = $"{onDev} of {tracked} tools on your Dev Drive";
        if (mapped > 0)
        {
            return $"{fraction} ({mapped} mapped).";
        }

        return $"{fraction}.";
    }

    private static string Token(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "ecosystem" : builder.ToString();
    }
}
