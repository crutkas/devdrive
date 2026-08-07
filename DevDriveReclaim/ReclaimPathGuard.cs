using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevDriveReclaim;

/// <summary>What a resolved candidate turned out to be, and therefore how it must be removed.</summary>
public enum ReclaimTargetKind
{
    /// <summary>A folder tree.</summary>
    Directory,

    /// <summary>A single file.</summary>
    File,

    /// <summary>
    /// A volume's Recycle Bin. Emptied through the shell, never deleted as a path — the candidate's
    /// path is the volume root, which is the one path on the machine that must never be handed to a
    /// delete call.
    /// </summary>
    RecycleBin,
}

/// <summary>The three things the guard can conclude about a candidate.</summary>
public enum ReclaimGuardDecision
{
    /// <summary>Safe to act on. <see cref="ReclaimGuardVerdict.CanonicalPath"/> is what to act on.</summary>
    Allow,

    /// <summary>Must not be touched. <see cref="ReclaimGuardVerdict.Explanation"/> says why.</summary>
    Refuse,

    /// <summary>
    /// Nothing is there any more. Not a failure — a scan takes minutes and the world moves
    /// underneath it, so a candidate deleted by a build, a rebase, or a previous run is an expected
    /// outcome rather than an error to report.
    /// </summary>
    AlreadyGone,
}

/// <summary>The guard's answer for one candidate.</summary>
public sealed record ReclaimGuardVerdict(
    ReclaimGuardDecision Decision,
    string? CanonicalPath,
    ReclaimTargetKind Kind,
    string Explanation);

/// <summary>
/// The single gate every deletion passes through. Resolves a candidate's path to what the
/// filesystem will actually act on, then refuses the ones that must never be acted on at all.
/// </summary>
/// <remarks>
/// A scanner's path is a string it built while walking; a deletion needs to know what that string
/// resolves to <em>now</em>. Those differ in ways that matter here and nowhere else in the app: 8.3
/// short names, a <c>..</c> segment, a trailing separator, a case that does not match the disk, a
/// junction pointing somewhere the scan never looked. Every one of those makes a path that looks
/// fine in a table and deletes something else.
/// <para>
/// The rules are deliberately about <em>categories of path</em> rather than a blocklist of known-bad
/// strings. A blocklist only stops the mistakes someone already thought of; refusing "any path that
/// is a volume root", "any path that contains a protected directory", and "any path that is a
/// reparse point" also stops the ones nobody has thought of yet — including a future provider with
/// a bug in it, which is the failure this app is one line away from at all times.
/// </para>
/// </remarks>
public static class ReclaimPathGuard
{
    /// <summary>
    /// Directories that must survive, plus every ancestor of each. A candidate is refused if it
    /// <em>is</em> one of these or <em>contains</em> one, which is what makes the ancestor case work:
    /// nothing needs to list <c>C:\</c> or <c>C:\Users</c> for them to be protected, because both
    /// contain the profile directory.
    /// </summary>
    private static readonly string[] ProtectedDirectories = BuildProtectedDirectories();

    /// <summary>
    /// Resolves and vets one candidate. Never throws for a bad path: an unusable path is a verdict,
    /// because a delete run that aborts halfway through leaves the user worse off than one that
    /// skips a row and says so.
    /// </summary>
    public static ReclaimGuardVerdict Inspect(ReclaimCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        string raw = candidate.Path.Trim().Trim('"');
        if (raw.Length == 0)
        {
            return Refuse("the candidate has no path");
        }

        // The Recycle Bin is the one candidate whose path is a volume root, because the bin has no
        // single folder to point at. It is resolved first and by category, so the root check below
        // can be absolute: past this point, a path that is a volume root is always a bug.
        if (IsVolumeRoot(raw))
        {
            return string.Equals(candidate.CategoryId, "recycle-bin", StringComparison.Ordinal)
                ? new ReclaimGuardVerdict(
                    ReclaimGuardDecision.Allow,
                    RootWithSeparator(raw),
                    ReclaimTargetKind.RecycleBin,
                    "empties the Recycle Bin on this volume")
                : Refuse($"'{raw}' is a whole volume, and nothing this tool removes is ever a volume");
        }

        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse($"the path could not be resolved ({exception.Message})");
        }

        // Re-checked after resolution, because C:\anything\.. is a volume root and did not look like
        // one a moment ago.
        if (IsVolumeRoot(full))
        {
            return Refuse($"'{raw}' resolves to the volume root {full}");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReclaimGuardVerdict(
                ReclaimGuardDecision.AlreadyGone, full, ReclaimTargetKind.Directory, "already gone");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refuse($"the path could not be read ({exception.Message})");
        }

        // GetAttributes does not follow the link, so this is the one chance to notice. A junction is
        // refused rather than unlinked: its bytes live somewhere this scan never measured, so
        // removing it reclaims almost nothing while risking everything on the other side of it.
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Refuse(
                "it is a junction, symbolic link or mount point — its contents live elsewhere, so " +
                "removing it would free almost nothing and could reach a folder this scan never saw");
        }

        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);

        // Only now, on a real non-link path, is it worth opening a handle. This expands 8.3 short
        // names and corrects case to what the volume actually stores, so the protected-directory
        // comparison below is against the truth rather than against whatever the provider typed.
        string canonical = TryResolveFinalPath(full, isDirectory) ?? full;

        if (IsVolumeRoot(canonical))
        {
            return Refuse($"'{raw}' resolves to the volume root {canonical}");
        }

        foreach (string protectedPath in ProtectedDirectories)
        {
            if (SamePath(canonical, protectedPath))
            {
                return Refuse($"{canonical} is a protected system location");
            }

            if (ReclaimOverlapResolver.Contains(canonical, protectedPath))
            {
                return Refuse($"{canonical} contains the protected location {protectedPath}");
            }
        }

        return new ReclaimGuardVerdict(
            ReclaimGuardDecision.Allow,
            canonical,
            isDirectory ? ReclaimTargetKind.Directory : ReclaimTargetKind.File,
            isDirectory ? "removes this folder and everything in it" : "removes this file");
    }

    private static ReclaimGuardVerdict Refuse(string explanation) =>
        new(ReclaimGuardDecision.Refuse, null, ReclaimTargetKind.Directory, explanation);

    /// <summary>
    /// True when a path names a whole volume — <c>C:</c>, <c>C:\</c>, or <c>C:/</c>. Compared against
    /// <see cref="Path.GetPathRoot(string)"/> rather than by length, so a UNC share root
    /// (<c>\\server\share</c>) is caught by the same rule.
    /// </summary>
    private static bool IsVolumeRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && SamePath(path, root);
    }

    private static string RootWithSeparator(string path) =>
        path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    private static bool SamePath(string left, string right) =>
        string.Equals(RootWithSeparator(left), RootWithSeparator(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The locations that must survive any reclaim run, resolved through the same handle-based
    /// canonicalisation applied to candidates so the two are comparable.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.UserProfile"/> is here rather than a list of the folders
    /// inside it because protecting the profile protects <c>C:\Users</c> and <c>C:\</c> for free —
    /// both contain it, and the containment rule refuses a candidate that contains a protected path.
    /// <para>
    /// The application's own directory is included so a run cannot delete the process executing it.
    /// A build-output detector pointed at the repo this app was built from will find it.
    /// </para>
    /// </remarks>
    private static string[] BuildProtectedDirectories()
    {
        var paths = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }

        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile,
        })
        {
            try
            {
                Add(Environment.GetFolderPath(folder));
            }
            catch (ArgumentException)
            {
                // A folder this OS does not define is not a folder anyone can delete either.
            }
        }

        try
        {
            Add(AppContext.BaseDirectory);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
        }

        return [.. paths
            .Select(p => TryResolveFinalPath(p, isDirectory: true) ?? p)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Asks the filesystem what a path really is, via <c>GetFinalPathNameByHandle</c>. Returns null
    /// when the path cannot be opened, in which case the caller falls back to
    /// <see cref="Path.GetFullPath(string)"/> — a weaker answer, but the protected-directory rules
    /// still run against it.
    /// </summary>
    private static string? TryResolveFinalPath(string path, bool isDirectory)
    {
        const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        const uint OpenExisting = 3;
        const uint FileFlagBackupSemantics = 0x02000000;
        const uint VolumeNameDos = 0x0;

        // dwDesiredAccess of 0 asks for metadata only, which is all this needs and is the difference
        // between resolving a path and being denied one that is in use.
        using SafeFileHandle handle = CreateFileW(
            path,
            0,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            isDirectory ? FileFlagBackupSemantics : 0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        uint length = GetFinalPathNameByHandleW(handle, null, 0, VolumeNameDos);
        if (length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        uint written = GetFinalPathNameByHandleW(handle, buffer, length, VolumeNameDos);
        if (written == 0 || written >= length)
        {
            return null;
        }

        string resolved = new(buffer, 0, (int)written);

        // The API always returns the \\?\ form. Stripping it keeps the string comparable with the
        // paths every other part of the app carries; the extended form is only needed at call time.
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + resolved[8..];
        }

        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        char[]? lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
