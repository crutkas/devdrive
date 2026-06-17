namespace DevDriveCore.Abstractions;

/// <summary>
/// Thin wrapper over the native FSCTL used to detect a Dev Drive. Kept deliberately tiny so the
/// interesting part — decoding the flags into <see cref="Models.VolumeInfo"/> — lives in the
/// service and is unit-testable against a mock of this interface.
/// </summary>
public interface INativeVolumeApi
{
    /// <summary>
    /// Issues <c>FSCTL_QUERY_PERSISTENT_VOLUME_STATE</c> against the root of a volume and returns
    /// the raw <c>VolumeFlags</c>, or <c>null</c> when the state can't be queried (volume not
    /// openable, file system doesn't support the FSCTL, etc.). Works unelevated.
    /// </summary>
    /// <param name="volumeRootPath">
    /// The volume <em>root</em>, e.g. <c>G:\</c> (trailing separator required). The FSCTL must be
    /// issued against the root directory handle — not the <c>\\.\X:</c> device path, which returns
    /// ERROR_INVALID_FUNCTION.
    /// </param>
    uint? QueryPersistentVolumeState(string volumeRootPath);
}
