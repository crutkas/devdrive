namespace DevDriveManager.Services;

/// <summary>
/// Feature gate for the DESTRUCTIVE real-resize execute path (shrink &#8594; repartition &#8594;
/// <c>Format-Volume -DevDrive</c>). DEFAULT-OFF in this prototype: the destructive sequence is never
/// reachable from the default UI. The read-only <c>--whatif</c> feasibility preview is always allowed;
/// only the irreversible execute step is gated here, on top of an explicit user confirmation and UAC
/// elevation.
/// </summary>
/// <remarks>
/// Backed by an <see cref="AppContext"/> switch that defaults to <c>false</c>. Deliberate self-hosting
/// builds set it through <c>/p:EnableRealResizeExecute=true</c>; normal builds and automated tests do not.
/// </remarks>
public static class ResizeFeatureGate
{
    /// <summary>The <see cref="AppContext"/> switch name that, when explicitly set <c>true</c>, unlocks the execute path.</summary>
    public const string EnableRealResizeExecuteSwitch = "DevDriveManager.EnableRealResizeExecute";

    /// <summary>
    /// <c>true</c> only when the opt-in switch is explicitly enabled. Defaults to <c>false</c> (switch
    /// absent) so the prototype never one-click-repartitions the system drive.
    /// </summary>
    public static bool EnableRealResizeExecute =>
        AppContext.TryGetSwitch(EnableRealResizeExecuteSwitch, out bool enabled) && enabled;
}
