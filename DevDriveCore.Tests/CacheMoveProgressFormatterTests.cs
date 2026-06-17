using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>Tests for the pure <see cref="CacheMoveProgressFormatter"/> used by the M4 move UI.</summary>
[TestClass]
public sealed class CacheMoveProgressFormatterTests
{
    [TestMethod]
    public void Describe_ZeroFiles_ShowsPreparing()
    {
        Assert.AreEqual("Preparing\u2026", CacheMoveProgressFormatter.Describe(new CacheMoveProgress()));
    }

    [TestMethod]
    public void Describe_InProgress_ShowsFilesAndBytes()
    {
        var progress = new CacheMoveProgress
        {
            FilesCompleted = 12,
            TotalFiles = 40,
            BytesCompleted = 3 * 1024 * 1024,
            TotalBytes = 10 * 1024 * 1024,
        };

        string text = CacheMoveProgressFormatter.Describe(progress);

        StringAssert.Contains(text, "12/40 files");
        StringAssert.Contains(text, "3 MB");
        StringAssert.Contains(text, "10 MB");
    }

    [TestMethod]
    public void Percent_HalfByBytes_Is50()
    {
        var progress = new CacheMoveProgress { TotalFiles = 2, BytesCompleted = 50, TotalBytes = 100 };
        Assert.AreEqual(50d, CacheMoveProgressFormatter.Percent(progress), 0.001);
    }
}
