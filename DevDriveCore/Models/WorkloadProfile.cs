namespace DevDriveCore.Models;

/// <summary>
/// Selects how large and realistic a real-workload benchmark's fixture is.
/// </summary>
/// <remarks>
/// <para><see cref="Quick"/> powers the inline per-cache "Test speed": moderate fixtures that run in a
/// few seconds — big enough to show a real, non-trivial delta but responsive.</para>
/// <para><see cref="Thorough"/> powers the global "Real workload test" card: realistic fixtures that
/// churn enough small files to expose the Dev Drive's advantage. For the build workloads these are real,
/// Microsoft-owned projects snapped to a release (a small Microsoft npm package set, the System.Reactive
/// library built for net6.0, and microsoft/edit built with cargo); git clone stays a synthetic
/// ~15,000-file tree so it needs no network. Longer runs are acceptable — that card has live progress
/// and Cancel.</para>
/// <para><b>SAFETY:</b> both profiles stay bounded (a few hundred MB at most), live under temp/bench
/// folders, and are cleaned up. Thorough only differs in fixture size and, for npm/dotnet/cargo, a
/// one-time network seed that warms a shared, offline-from-then-on cache.</para>
/// </remarks>
public enum WorkloadProfile
{
    /// <summary>Moderate fixtures for a responsive inline test (a few seconds).</summary>
    Quick,

    /// <summary>Realistic, real-world-app fixtures for the global card (longer, cancellable).</summary>
    Thorough,
}
