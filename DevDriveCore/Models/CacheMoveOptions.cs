namespace DevDriveCore.Models;

/// <summary>
/// Tunables for a single <see cref="DevDriveCore.Services.PackageCacheMover"/> run. The defaults are
/// deliberately conservative: copies are verified by hash and the <em>source is kept</em> (so the
/// operation is trivially reversible and never destroys data unless explicitly asked).
/// </summary>
public sealed record CacheMoveOptions
{
    /// <summary>
    /// When <c>true</c> existing destination files are overwritten. Default <c>false</c>: a UI move must
    /// not clobber files already at the deterministic target. The mover also refuses a non-empty target it
    /// doesn't own (see <see cref="DevDriveCore.Services.PackageCacheMover"/>), so the common path writes
    /// into a fresh or app-owned folder where nothing is overwritten anyway.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// When <c>true</c> the source directory is deleted after every file is copied and verified
    /// (a true "move"). Default <c>false</c>: the copy is left in place so revert need only restore
    /// the environment variable.
    /// </summary>
    public bool DeleteSourceAfterVerify { get; init; }

    /// <summary>
    /// When <c>true</c> (default) each copied file's SHA-256 is compared against the source; a
    /// mismatch fails the move and triggers rollback.
    /// </summary>
    public bool VerifyHashes { get; init; } = true;

    /// <summary>The default option set.</summary>
    public static CacheMoveOptions Default => new();
}
