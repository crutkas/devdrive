namespace DevDriveStorage.Live;

/// <summary>
/// Separates the folders that are <em>expected</em> to be unreadable on every Windows machine from
/// genuine access failures.
/// </summary>
/// <remarks>
/// Without this distinction every whole-volume scan is permanently <see cref="SnapshotCompletion.Partial"/>:
/// <c>System Volume Information</c> denies an unelevated caller on every fixed volume that exists, so a
/// "some folders could not be read" warning would fire on a scan where nothing actually went wrong. A
/// warning that is always on is a warning nobody reads — and it would hide the denial that matters, the
/// one folder the user really can't see.
/// <para>
/// Elevating does not change the answer: these paths are ACL'd to SYSTEM, not to Administrators, so the
/// classification is stable regardless of the token the scan runs under. They are excluded facts, not
/// permission problems, and the UI should state them as "excluded" rather than ask the user to fix them.
/// </para>
/// </remarks>
public static class SystemPathClassifier
{
    /// <summary>
    /// Leaf folder names that are OS-owned and unreadable by design. Matched on the leaf only, and only
    /// when the folder sits directly at a volume root, so a user's own <c>C:\dev\$Recycle.Bin</c> test
    /// fixture is still reported as a real denial.
    /// </summary>
    private static readonly string[] VolumeRootExclusions =
    [
        "System Volume Information",
        "$RECYCLE.BIN",
        "$Recycle.Bin",
        "Recovery",
        "Config.Msi",
        "$WinREAgent",
        "DumpStack.log.tmp",
    ];

    /// <summary>
    /// True when <paramref name="path"/> is an OS-owned folder whose denial carries no information —
    /// it would be denied on any machine, under any token.
    /// </summary>
    public static bool IsExpectedSystemExclusion(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmed = path.TrimEnd('\\', '/');
        string? parent = Path.GetDirectoryName(trimmed);
        if (parent is null || !IsVolumeRoot(parent))
        {
            return false;
        }

        string leaf = Path.GetFileName(trimmed);
        foreach (string exclusion in VolumeRootExclusions)
        {
            if (string.Equals(leaf, exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for <c>C:\</c> and <c>\\?\Volume{...}\</c> style roots, false for any subfolder.</summary>
    private static bool IsVolumeRoot(string path)
    {
        string normalized = path.EndsWith('\\') || path.EndsWith('/') ? path : path + "\\";
        string? root = Path.GetPathRoot(normalized);
        return root is not null &&
            string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase);
    }
}
