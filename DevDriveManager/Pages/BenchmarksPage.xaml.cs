using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The "Benchmarks" page: the run-all hero plus one banded row per ecosystem that has a REAL workload
/// benchmark (Node / .NET / Rust). Benchmarks are intentionally demoted to on-demand here — the dashboard
/// no longer nags the user to run them. <see cref="BenchmarkCards"/> is a filtered, banded projection of
/// the shared <see cref="EcosystemsViewModel.Cards"/> collection, rebuilt whenever the shared load refreshes.
/// </summary>
public sealed partial class BenchmarksPage : Page
{
    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>Only the ecosystems with a measurable workload, banded for the eye to flow.</summary>
    public ObservableCollection<EcosystemCardViewModel> BenchmarkCards { get; } = new();

    public BenchmarksPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Ecosystems.Cards.CollectionChanged += OnCardsChanged;
        RebuildBenchmarkCards();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Ecosystems.Cards.CollectionChanged -= OnCardsChanged;
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildBenchmarkCards();

    private void RebuildBenchmarkCards()
    {
        BenchmarkCards.Clear();
        int index = 0;
        foreach (EcosystemCardViewModel card in ViewModel.Ecosystems.Cards)
        {
            if (!card.HasBenchmark)
            {
                continue;
            }

            card.BandAlt = (index++ % 2) == 1;
            BenchmarkCards.Add(card);
        }
    }
}
