namespace DevDriveReclaim.Providers;

/// <summary>
/// Decides whether a gitignored path is something the machine can regenerate, or the only copy
/// of something a person configured by hand.
/// </summary>
/// <remarks>
/// <para>
/// <c>git status --porcelain</c> does not report ignored files at all, so a worktree whose only
/// unique content is an ignored <c>.env</c> or a signing certificate graded <b>Safe</b> and was
/// preselected for deletion. Asking git for ignored content closes that hole and opens a worse
/// one, because on a developer machine almost everything ignored is build output.
/// </para>
/// <para>
/// The obvious rule — list the regenerable things and treat the rest as precious — was measured
/// against the 53 worktrees on the machine this was written for and does not work. Build output
/// hides behind project-specific names that cannot be enumerated: alongside <c>bin/</c> and
/// <c>obj/</c> there is <c>x64/</c>, <c>Generated Files/</c>, <c>vcpkg_installed/</c>, and C++
/// intermediate directories named after their own project (<c>src/common/logger/logger/</c>).
/// Any list stops somewhere, and every miss grades a worktree Careful.
/// </para>
/// <para>
/// That failure mode is worse than the hole it closes. Careful rows are never preselected and
/// additionally demand typing DELETE, so a rule that fires on all 41 worktrees does not protect
/// anyone — it <b>teaches the habit of typing DELETE to get past the warning</b>, which is the
/// one habit this risk model cannot survive.
/// </para>
/// <para>
/// So the rule is inverted and deliberately narrow: name only the things that genuinely cannot
/// be regenerated — credentials, keys and hand-written local configuration — and treat every
/// other ignored path as build output. On the reference machine this matches <b>nothing</b>,
/// which is the correct result there: every ignored entry across all 53 worktrees really was
/// regenerable. It costs nothing until the day a worktree is carrying a <c>.env</c>.
/// </para>
/// <para>
/// Known limitation: git collapses a fully-ignored directory to a single entry, so a secret
/// inside an ignored folder is invisible here. Widening to <c>--ignored=matching</c> would see
/// it and would also enumerate every file under <c>node_modules/</c>, which is not a trade worth
/// making on a per-worktree scan.
/// </para>
/// </remarks>
public static class LocalOnlyContent
{
    /// <summary>Directory names that are private by convention wherever they appear.</summary>
    private static readonly string[] PrivateDirectories = [".ssh", ".aws", ".gnupg", ".azure"];

    /// <summary>Extensions that are always a key, a certificate, or a signing identity.</summary>
    private static readonly string[] SecretExtensions =
        [".pfx", ".p12", ".pem", ".key", ".snk", ".keystore", ".jks"];

    /// <summary>Exact file names that hold credentials or hand-written local configuration.</summary>
    private static readonly string[] SecretFileNames =
    [
        ".env",
        ".npmrc",
        ".netrc",
        ".pypirc",
        "secrets.json",
        "credentials",
        "local.settings.json",
    ];

    /// <summary>File names that are private regardless of what follows the prefix.</summary>
    private static readonly string[] SecretFilePrefixes = ["id_rsa", "id_ed25519", "id_ecdsa", ".env."];

    /// <summary>
    /// True when losing this path would lose something that exists nowhere else.
    /// </summary>
    /// <param name="ignoredPath">
    /// A repository-relative path as git reports it, with <c>/</c> separators and a trailing
    /// <c>/</c> on directories.
    /// </param>
    public static bool IsIrreplaceable(string? ignoredPath)
    {
        if (string.IsNullOrWhiteSpace(ignoredPath))
        {
            return false;
        }

        string[] segments = ignoredPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (PrivateDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // A trailing slash means git collapsed a directory; there is no file name to judge, and
        // the directory name itself was already checked above.
        if (ignoredPath.EndsWith('/') || ignoredPath.EndsWith('\\'))
        {
            return false;
        }

        string name = segments[^1];

        if (SecretFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string prefix in SecretFilePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // "appsettings.Development.local.json" and friends: a local override of a committed file.
        if (name.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(name);
        return SecretExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
