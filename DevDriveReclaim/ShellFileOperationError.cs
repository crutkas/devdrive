namespace DevDriveReclaim;

/// <summary>
/// Turns a <c>SHFileOperation</c> return code into a sentence that tells the reader what to do
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// These codes are <b>not</b> Win32 error codes. Running them through
/// <c>Marshal.GetLastWin32Error</c> or a <c>FormatMessage</c> lookup produces a confident,
/// authoritative and completely wrong description &mdash; 0x79 is <c>DE_PATHTOODEEP</c> to the
/// shell and <c>ERROR_SEM_TIMEOUT</c> to Win32. So the mapping is explicit and closed, and
/// anything not on the list is reported as a raw code rather than guessed at.
/// </para>
/// <para>
/// The sentences name the <i>obstacle</i>, not the API. "The shell could not remove it (0x79)"
/// is true and useless; "a path inside it is too deep for the shell to delete" is the same fact
/// in a form the reader can act on. This is the one message someone reads at the exact moment
/// the tool has failed them, so it is worth more than a hex code.
/// </para>
/// </remarks>
public static class ShellFileOperationError
{
    /// <summary>
    /// A plain-English description of <paramref name="code"/>, or null when the code is not one
    /// of the documented shell errors.
    /// </summary>
    public static string? Describe(int code) => code switch
    {
        // Not in the documented DE_ range. Measured, because the code alone is misleading: as a
        // Win32 code 0x20 is ERROR_SHARING_VIOLATION, which sends the reader hunting for an open
        // file that does not exist. A tree whose every file opens cleanly with FileShare.None still
        // returns 0x20 when a single process has its CURRENT DIRECTORY inside it -- a working
        // directory is not a handle on any file, so nothing shows up in a lock scan.
        //
        // This is the common case for the folders this app is pointed at. On the machine this was
        // found on, 19 processes were sitting in worktrees at once: every agent session, shell and
        // editor holds the folder it was opened in.
        0x20 => "a program has this folder open as its working directory — a terminal, editor or "
              + "agent session is probably sitting in it",

        0x71 => "the source and destination are the same file",
        0x74 => "it is the root of a drive, which the shell will not delete",
        0x75 => "the operation was cancelled",
        0x78 => "something denied access — a file inside it is most likely open in another program",
        0x79 => "a path inside it is too deep for the shell to delete",

        // Documented as DE_INVALIDFILES, "the path was invalid" — which is misleading often enough
        // to be worth contradicting. Verified empirically: a folder whose path is entirely valid,
        // enumerable by .NET and 219 characters long returns 0x7C when a single file inside it is
        // held with FileShare.None. Sending the reader off to check their path would waste the one
        // message they get. Name the cause that actually produces it.
        0x7C => "something inside it is in use — most likely a file open in another program",

        0x81 => "a file name inside it is too long for the shell to delete",
        0x85 => "it is too large for the destination",
        0xB7 => "the shell hit its own error limit partway through",

        // Not in the documented DE_ range, and by far the most common failure on a developer
        // machine: the shell's own MAX_PATH ceiling. SHFileOperation predates long-path support
        // and has no \\?\ escape, so a build tree with a deep node_modules or obj graph hits this
        // even though every individual path is legal to .NET.
        0x402 => "a path inside it is longer than the shell can handle (260 characters)",

        _ => null,
    };

    /// <summary>
    /// The sentence shown to the reader for a failed delete, always ending in the raw code so a
    /// bug report carries the number even when the description is a guess-free fallback.
    /// </summary>
    public static string Explain(int code) =>
        Describe(code) is string known
            ? $"{known} (0x{code:X})"
            : $"the shell could not remove it (0x{code:X})";
}
