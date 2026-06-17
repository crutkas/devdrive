namespace DevDriveCore.Abstractions;

/// <summary>
/// Thin wrapper over the <c>virtdisk.dll</c> operations needed to create and surface a virtual disk,
/// kept deliberately tiny so the interesting part — the orchestration and reversibility in
/// <see cref="DevDriveCore.Services.VhdProvisioner"/> — is unit-testable against a mock of this
/// interface.
/// </summary>
/// <remarks>
/// <para>The real implementation (<see cref="DevDriveCore.Platform.NativeVhdApi"/>) P/Invokes
/// <c>CreateVirtualDisk</c>, <c>AttachVirtualDisk</c>, <c>GetVirtualDiskPhysicalPath</c> and
/// <c>DetachVirtualDisk</c>. The app composes it (M8) behind an explicit user confirmation, but it is
/// never invoked by the test suite.</para>
/// <para><b>SAFETY:</b> every unit test injects a <em>mock</em> <see cref="INativeVhdApi"/> and the
/// UI-test seam substitutes a SAFE fake provisioner. No test ever creates, attaches, or detaches a real
/// VHD/VHDX.</para>
/// </remarks>
public interface INativeVhdApi
{
    /// <summary>
    /// Creates a new <c>.vhdx</c> at <paramref name="path"/> with the given maximum size. When
    /// <paramref name="dynamicallyExpanding"/> is <c>true</c> the backing file grows on demand;
    /// otherwise it is fixed-size. Throws on failure.
    /// </summary>
    void CreateVirtualDisk(string path, ulong maximumSizeBytes, bool dynamicallyExpanding);

    /// <summary>
    /// Attaches (surfaces) the <c>.vhdx</c> at <paramref name="path"/> and returns its physical
    /// device path, e.g. <c>\\.\PhysicalDrive7</c>. Throws on failure.
    /// </summary>
    string AttachVirtualDisk(string path);

    /// <summary>Detaches (removes) the <c>.vhdx</c> at <paramref name="path"/>. Throws on failure.</summary>
    void DetachVirtualDisk(string path);
}
