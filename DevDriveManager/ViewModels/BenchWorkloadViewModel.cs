using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One row of the Benchmarks room's workload table: an ecosystem's identity, its measurable workload,
/// and that workload's state rendered as the words and pill kinds the table shows.
/// </summary>
/// <remarks>
/// <para>
/// A flattening wrapper rather than binding straight to <see cref="EcosystemCardViewModel"/>, because
/// that type's <c>Benchmark</c> is nullable and <c>x:Bind</c> down a nullable path has no defined
/// value until it is given a fallback. The list is filtered to the cards that have one, so the
/// nullability is provably absent here — worth saying in the type rather than in a comment on every
/// binding.
/// </para>
/// <para>
/// It also exists because <b>an <c>x:Bind</c> function binding cannot take another function binding as
/// its argument</b>. The XAML compiler emits code referring to parameters it never declares, and the
/// build fails inside generated source with no line in any file you wrote. Anything that would have
/// been <c>Helper(Other(x))</c> in markup is a property here instead.
/// </para>
/// <para>
/// Subscribes to the wrapped row, so it must be disposed when the table is rebuilt: the row is owned
/// by the suite and outlives this wrapper, so an undisposed handler would keep every generation of
/// wrapper alive for the life of the app.
/// </para>
/// </remarks>
public sealed partial class BenchWorkloadViewModel : ObservableObject, IDisposable
{
    private readonly PerfSuiteRowViewModel _row;
    private bool _disposed;

    public BenchWorkloadViewModel(string glyph, string name, string toolsLine, PerfSuiteRowViewModel row)
    {
        Glyph = glyph;
        Name = name;
        ToolsLine = toolsLine;
        _row = row;
        _row.PropertyChanged += OnRowChanged;
    }

    /// <summary>Segoe Fluent glyph for the ecosystem.</summary>
    public string Glyph { get; }

    /// <summary>The ecosystem, as the rest of the app names it — "Node", ".NET", "Rust".</summary>
    public string Name { get; }

    /// <summary>The tools this ecosystem covers, e.g. "npm / Yarn / pnpm".</summary>
    public string ToolsLine { get; }

    /// <summary>The measurable workload. Never null.</summary>
    public PerfSuiteRowViewModel Row => _row;

    public string RowAutomationId => $"BenchRow_{_row.Id}";

    public string RowAccessibleName => $"{Name} workload";

    /// <summary>The comparison strip's id, so a test can assert one workload's bars.</summary>
    /// <summary>
    /// Id of this workload's row in the "Measured runs" card. It is carried by the row's name label, not
    /// by the row Grid: a layout-only Grid inside a DataTemplate is filtered out of the UIA control view.
    /// </summary>
    public string RunsRowAutomationId => $"BenchRuns_{_row.Id}";

    public string SystemCellAutomationId => $"BenchSystem_{_row.Id}";

    public string DevCellAutomationId => $"BenchDev_{_row.Id}";

    public string DeltaAutomationId => $"BenchDelta_{_row.Id}";

    // ---- Presentation ----------------------------------------------------------------------------

    /// <summary>
    /// The workload's own description of what it builds. Lives here so the table binds one level deep
    /// and picks up the row's notifications through this wrapper's forwarding.
    /// </summary>
    public string WhatItBuilds => _row.Sub;

    /// <summary>
    /// The system-drive figure, or an em dash. Under a column header an empty cell reads as data we
    /// failed to load, whereas a dash reads as a run that has not happened — which is what it is.
    /// </summary>
    public string SystemCell => Measured(_row.SystemValueText);

    /// <summary>
    /// The Dev Drive figure. "n/a" rather than a dash when there is no Dev Drive at all: a dash says
    /// "not yet", and on a PC without one there is no yet.
    /// </summary>
    public string DevCell => _row.HasComparison ? Measured(_row.DevValueText) : "n/a";

    /// <summary>
    /// What the DIFFERENCE pill says. A ratio only exists once both volumes have been timed, so every
    /// other state gets a word instead of a number rather than a misleading "1.0x".
    /// </summary>
    public string DeltaLabel
    {
        get
        {
            if (_row.IsRunning)
            {
                return "Running";
            }

            if (_row.IsSkipped)
            {
                return "Skipped";
            }

            return _row.ShowComparisonResult && !string.IsNullOrWhiteSpace(_row.DeltaText)
                ? _row.DeltaText
                : "Not measured";
        }
    }

    /// <summary>
    /// Which pill the difference wears. Muted until there is a comparison, because a grey pill reading
    /// "Not measured" is a fact and a green one reading the same thing is an encouragement.
    /// </summary>
    public string DeltaKind => !_row.ShowComparisonResult
        ? "mute"
        : _row.DeltaIsFavorable ? "dev" : "system";

    /// <summary>True once this workload has numbers, i.e. once the comparison card can draw bars.</summary>
    public bool IsMeasured => _row.IsDone;

    /// <summary>
    /// The Dev Drive bar exists only when there is a Dev Drive to compare against. On a PC without one
    /// the room still measures the system drive, and a second empty track would read as a failure.
    /// </summary>
    public bool ShowDevBar => _row.IsDone && _row.HasComparison;

    /// <summary>Why a workload has no bars yet, said in the place the bars would be.</summary>
    public string UnmeasuredNote
    {
        get
        {
            if (_row.IsRunning)
            {
                return "Running now\u2026";
            }

            if (_row.IsSkipped)
            {
                return _row.SkipReasonText.Length > 0 ? _row.SkipReasonText : "Skipped on this PC.";
            }

            // Deliberately just the state word. This note appears once per unrun workload, so a full
            // sentence renders four identical lines stacked on top of each other -- noise that teaches
            // the reader to skip the whole card. The Run button is already in the table above.
            return "Not measured";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _row.PropertyChanged -= OnRowChanged;
    }

    private static string Measured(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "\u2014" : value;

    /// <summary>
    /// Forwards every derived value on any row change. The alternative — a switch over a dozen source
    /// property names — buys nothing here: a room has at most a handful of workloads, and a missed case
    /// is a cell that silently stops updating.
    /// </summary>
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(WhatItBuilds));
        OnPropertyChanged(nameof(SystemCell));
        OnPropertyChanged(nameof(DevCell));
        OnPropertyChanged(nameof(DeltaLabel));
        OnPropertyChanged(nameof(DeltaKind));
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(ShowDevBar));
        OnPropertyChanged(nameof(UnmeasuredNote));
    }
}
