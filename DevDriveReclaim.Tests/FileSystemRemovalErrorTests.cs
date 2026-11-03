using DevDriveReclaim;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The permanent-delete path fails by throwing, and the framework's own message is not fit to show
/// a reader. These tests pin what replaces it.
/// </summary>
[TestClass]
public sealed class FileSystemRemovalErrorTests
{
    private const int SharingViolation = 32;
    private const int AccessDenied = 5;
    private const int DirectoryNotEmpty = 145;

    private static IOException Win32(int code, string message) =>
        new(message) { HResult = unchecked((int)0x80070000) | code };

    [TestMethod]
    public void TheSharingViolationNamesTheWorkingDirectory()
    {
        // Same measured cause as the shell path's 0x20, reached a completely different way: here
        // Directory.Delete throws rather than returning a code. .NET says "because it is being used
        // by another process", which is true of an open file and equally true of a process merely
        // sitting in the folder -- and only one of those shows up in a lock scan.
        string? described = FileSystemRemovalError.Describe(SharingViolation);

        Assert.IsNotNull(described);
        StringAssert.Contains(described, "working directory", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(AccessDenied, "denied")]
    [DataRow(DirectoryNotEmpty, "still writing")]
    public void ADescribedCodeNamesTheObstacle(int code, string expected)
    {
        string? described = FileSystemRemovalError.Describe(code);

        Assert.IsNotNull(described, $"{code} should be described");
        StringAssert.Contains(described, expected, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnExceptionWeCannotImproveOnKeepsItsOwnMessage()
    {
        // Replacing a specific framework message with a vaguer one of our own would be a downgrade.
        // Only the codes we can genuinely say more about are intercepted.
        string explained = FileSystemRemovalError.Explain(
            Win32(0x4D4D, "the disk shelf caught fire"));

        StringAssert.Contains(explained, "the disk shelf caught fire", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheExtendedLengthPrefixIsStrippedFromAPassedThroughMessage()
    {
        // \\?\ is an implementation detail of how this app issues the delete. Nobody typed it, and
        // pasting it back into Explorer does not work -- so showing it invites a wasted detour.
        string explained = FileSystemRemovalError.Explain(
            Win32(0x4D4D, @"could not delete '\\?\C:\src\obj'"));

        StringAssert.Contains(explained, @"'C:\src\obj'", StringComparison.Ordinal);
        Assert.IsFalse(explained.Contains(@"\\?\", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnExceptionThatIsNotAWin32FailureIsNotMisread()
    {
        // A managed HResult (COR_E_IO) happens to have 0x0020 in its low word. Reading that as a
        // sharing violation would put a confident, invented sentence in front of the reader.
        var managed = new IOException("a managed failure") { HResult = unchecked((int)0x80131620) };

        StringAssert.Contains(
            FileSystemRemovalError.Explain(managed), "a managed failure", StringComparison.Ordinal);
    }
}
