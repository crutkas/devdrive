using DevDriveCore.Abstractions;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveManager.Services;

/// <summary>
/// Composition seam for the real, reversible mutation engines (M4 package-cache move). In normal
/// operation it returns the PRODUCTION coordinator — real filesystem + per-user environment + a
/// persistent JSON reversibility store. When the UI-test seam env var
/// <c>DDM_UITEST_SAFE_MUTATIONS=1</c> is set, it returns a coordinator backed by SAFE fakes over an
/// in-memory store, so the automated UI suite can exercise the full Move → progress → Move-back flow
/// WITHOUT touching a real cache, environment variable, or settings file.
/// </summary>
/// <remarks>
/// The ViewModel always drives the same Core coordinator (<see cref="PackageCacheMoveCoordinator"/>);
/// only the underlying mover and store differ. The seam is read at composition time from the
/// <em>process</em> environment OR the <em>user</em> environment (<c>HKCU\Environment</c>). The
/// user-scope read matters because a packaged (MSIX) app activated by the shell does NOT inherit a
/// volatile child-process environment from the launching shell — but it can read the user-scope value
/// directly from the registry. The UI-test harness sets the user-scope value before launch and removes
/// it afterwards.
/// </remarks>
public static class MutationComposition
{
    /// <summary>Env var that switches mutations to the safe in-memory fakes (set only by the UI test harness).</summary>
    public const string SafeMutationEnvVar = "DDM_UITEST_SAFE_MUTATIONS";

    /// <summary>True when the UI-test safe-mutation seam is active (mutations are simulated; nothing real changes).</summary>
    public static bool IsSafeMutationMode =>
        IsEnabled(Environment.GetEnvironmentVariable(SafeMutationEnvVar)) || IsEnabled(ReadUserScope());

    private static bool IsEnabled(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

    // Read the user-scope value (HKCU\Environment) directly, so a packaged app picks up the seam even
    // though it didn't inherit the launching shell's volatile process environment. Defensive: a sandbox
    // that blocks the registry read just falls back to "production" (real, but still safe + reversible).
    private static string? ReadUserScope()
    {
        try
        {
            return Environment.GetEnvironmentVariable(SafeMutationEnvVar, EnvironmentVariableTarget.User);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The production package-cache move coordinator, or a SAFE-fake one under the UI-test seam.</summary>
    public static PackageCacheMoveCoordinator CreatePackageCacheMoveCoordinator()
    {
        if (IsSafeMutationMode)
        {
            var store = new InMemoryReversibilityStore();
            return new PackageCacheMoveCoordinator(new SafeFakePackageCacheMover(store), store);
        }

        return PackageCacheMoveCoordinator.CreateDefault();
    }

    /// <summary>The production Dev Drive creation service, or a SAFE-fake one under the UI-test seam.</summary>
    public static DevDriveCreationService CreateDevDriveCreationService()
    {
        if (IsSafeMutationMode)
        {
            // Simulated VHDX provisioning — no real disk is created, attached, or recorded.
            return new DevDriveCreationService(new SafeFakeVhdProvisioner());
        }

        return DevDriveCreationService.CreateDefault();
    }

    /// <summary>
    /// The production volume resizer (talks to the elevated helper), or a SAFE-fake one under the
    /// UI-test seam. The fake simulates feasibility/execute with no helper launch and no disk I/O, so
    /// the automated UI suite can drive the resize flow without touching a real disk.
    /// </summary>
    public static IVolumeResizer CreateVolumeResizer()
    {
        if (IsSafeMutationMode)
        {
            // Simulated shrink/repartition/format — nothing is queried, shrunk, partitioned, or formatted.
            return new SafeFakeVolumeResizer();
        }

        return VolumeResizer.CreateDefault();
    }
}
