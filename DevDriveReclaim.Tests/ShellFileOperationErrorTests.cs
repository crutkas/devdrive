using DevDriveReclaim;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The message shown when a delete fails is the one sentence someone reads at the exact moment
/// the tool has let them down. These tests pin that it says something they can act on.
/// </summary>
[TestClass]
public sealed class ShellFileOperationErrorTests
{
    [TestMethod]
    [DataRow(0x78, "open in another program")]
    [DataRow(0x79, "too deep")]
    [DataRow(0x81, "too long")]
    [DataRow(0x402, "260 characters")]
    [DataRow(0x74, "root of a drive")]
    public void ADocumentedCodeNamesTheObstacle(int code, string expected)
    {
        string? described = ShellFileOperationError.Describe(code);

        Assert.IsNotNull(described, $"0x{code:X} should be described");
        StringAssert.Contains(
            described,
            expected,
            StringComparison.Ordinal,
            $"0x{code:X} should tell the reader what is in the way");
    }

    [TestMethod]
    public void TheInvalidFilesCodeBlamesALockRatherThanThePath()
    {
        // Measured, not read off the documentation. A real 90 MB build output at a valid 219-char
        // path returned 0x7C from SHFileOperation for one reason only: a single file inside it was
        // held with FileShare.None. The documented name (DE_INVALIDFILES, "the path was invalid")
        // would send the reader to inspect a path that has nothing wrong with it.
        string? described = ShellFileOperationError.Describe(0x7C);

        Assert.IsNotNull(described);
        StringAssert.Contains(described, "in use", StringComparison.Ordinal);
        Assert.IsFalse(
            described.Contains("invalid", StringComparison.OrdinalIgnoreCase),
            "0x7C in practice means something is locked, not that the path is malformed");
    }

    [TestMethod]
    public void AnUnknownCodeIsNotGuessedAt()
    {
        // Inventing a description for a code we do not recognise is worse than admitting we do
        // not know it: a confident wrong sentence sends someone looking in the wrong place.
        Assert.IsNull(ShellFileOperationError.Describe(0x5A5A));
    }

    [TestMethod]
    public void EveryExplanationCarriesTheRawCode()
    {
        // Whether or not we can describe it, the number has to survive into the message — it is
        // the only part of the sentence that is useful in a bug report.
        StringAssert.Contains(ShellFileOperationError.Explain(0x79), "0x79", StringComparison.Ordinal);
        StringAssert.Contains(ShellFileOperationError.Explain(0x5A5A), "0x5A5A", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnUnknownCodeStillProducesASentence()
    {
        string explained = ShellFileOperationError.Explain(0x5A5A);

        StringAssert.Contains(explained, "could not remove", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheShellCodesAreNotTreatedAsWin32Codes()
    {
        // 0x79 is DE_PATHTOODEEP to the shell and ERROR_SEM_TIMEOUT to Win32. If anyone ever
        // swaps the explicit mapping for a FormatMessage lookup, this is what catches it.
        string explained = ShellFileOperationError.Explain(0x79);

        StringAssert.Contains(explained, "too deep", StringComparison.Ordinal);
        Assert.IsFalse(
            explained.Contains("semaphore", StringComparison.OrdinalIgnoreCase),
            "a Win32 lookup would describe 0x79 as a semaphore timeout");
    }
}
