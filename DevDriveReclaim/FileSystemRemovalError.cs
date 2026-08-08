namespace DevDriveReclaim;

/// <summary>
/// Turns a failed .NET delete into the same kind of sentence the shell path produces.
/// </summary>
/// <remarks>
/// <para>
/// The permanent-delete path throws rather than returning a code, and
/// <see cref="System.Exception.Message"/> on its own is not fit to show a reader: it names a
/// <c>\\?\</c> path they never typed, and for the most common failure on a developer machine it
/// says "because it is being used by another process", which sends them hunting for an open file
/// that frequently does not exist.
/// </para>
/// <para>
/// Measured: a folder that is another process's <b>current directory</b> cannot be deleted, even
/// though every file inside it opens cleanly with <c>FileShare.None</c>. A working directory is not
/// a handle on any file, so it never appears in a lock scan. That is the case named first here.
/// </para>
/// </remarks>
public static class FileSystemRemovalError
{
    /// <summary>The Win32 facility bits on an <c>IOException</c> raised from a Win32 error.</summary>
    private const int Win32Facility = unchecked((int)0x80070000);

    /// <summary>
    /// A sentence describing why a delete failed, or the exception's own message when the cause is
    /// not one this knows how to improve on.
    /// </summary>
    public static string Explain(Exception exception)
    {
        int code = Win32CodeOf(exception);

        return Describe(code) is string known
            ? known
            : Clean(exception.Message);
    }

    /// <summary>
    /// A plain description of <paramref name="win32Code"/>, or null when there is nothing better
    /// to say than what the framework already said.
    /// </summary>
    public static string? Describe(int win32Code) => win32Code switch
    {
        // ERROR_SHARING_VIOLATION. Leads with the working directory because it is both the most
        // common cause for the folders this app targets and the one a reader will never guess --
        // an open file at least announces itself in every tool they own.
        32 => "a program has this folder open as its working directory, or is holding a file "
            + "inside it — a terminal, editor or agent session is probably sitting in it",

        // ERROR_ACCESS_DENIED.
        5 => "access was denied — something inside it is read-only, or it belongs to another user",

        // ERROR_DIR_NOT_EMPTY. Surprising on a recursive delete, and it means the delete is racing
        // something that is still writing.
        145 => "something is still writing into it, so it refilled while it was being emptied",

        _ => null,
    };

    private static int Win32CodeOf(Exception exception) =>
        (exception.HResult & unchecked((int)0xFFFF0000)) == Win32Facility
            ? exception.HResult & 0xFFFF
            : 0;

    /// <summary>
    /// Strips the extended-length prefix the framework puts on paths, which is an implementation
    /// detail of how the delete was issued and not something the reader typed or can act on.
    /// </summary>
    private static string Clean(string message) => message.Replace(@"\\?\", string.Empty);
}
