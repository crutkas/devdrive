using DevDriveCore.Models;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the pure UI-facing projections used by M4: <see cref="CacheMoveOutcome.FromReceipt"/>
/// (per-row result copy) and <see cref="CacheMoveAllOutcome.From"/> (the continue-on-error "Move all"
/// aggregation). Pure data in, deterministic strings out — no filesystem or environment involved.
/// </summary>
[TestClass]
public sealed class CacheMoveOutcomeTests
{
    private static PackageCacheMoveReceipt MovedReceipt(long bytes = 1024 * 1024, int files = 3) => new()
    {
        Success = true,
        ToolName = "npm",
        SourcePath = @"C:\Users\dev\AppData\Local\npm-cache",
        TargetPath = @"G:\packages\npm",
        EnvironmentVariable = "npm_config_cache",
        FilesCopied = files,
        BytesCopied = bytes,
        ReversibilityId = "package-cache:npm_config_cache",
    };

    [TestMethod]
    public void FromReceipt_Moved_BuildsMovedCopyWithSizeTargetAndEnv()
    {
        CacheMoveOutcome outcome = CacheMoveOutcome.FromReceipt(MovedReceipt());

        Assert.AreEqual(CacheMoveStatus.Moved, outcome.Status);
        StringAssert.Contains(outcome.ResultText, "Moved");
        StringAssert.Contains(outcome.ResultText, "1 MB");
        StringAssert.Contains(outcome.ResultText, @"G:\packages\npm");
        StringAssert.Contains(outcome.ResultText, "npm_config_cache");
        Assert.AreEqual("package-cache:npm_config_cache", outcome.ReversibilityId);
        Assert.IsTrue(outcome.CanMoveBack);
    }

    [TestMethod]
    public void FromReceipt_AlreadyOnDevDrive_BuildsIdempotentCopy()
    {
        var receipt = new PackageCacheMoveReceipt
        {
            Success = true,
            AlreadyOnDevDrive = true,
            ToolName = "npm",
            TargetPath = @"G:\packages\npm",
            EnvironmentVariable = "npm_config_cache",
        };

        CacheMoveOutcome outcome = CacheMoveOutcome.FromReceipt(receipt);

        Assert.AreEqual(CacheMoveStatus.AlreadyOnDevDrive, outcome.Status);
        Assert.IsTrue(outcome.IsOnDevDrive);
        Assert.IsTrue(outcome.CanMoveBack);
        StringAssert.Contains(outcome.ResultText, "Dev Drive");
        // The idempotent no-op still surfaces a stable id so the row can offer move-back.
        Assert.AreEqual("package-cache:npm_config_cache", outcome.ReversibilityId);
    }

    [TestMethod]
    public void Failed_And_Cancelled_AreNotSucceeded()
    {
        Assert.IsFalse(CacheMoveOutcome.Failed("npm", "disk full").Succeeded);
        StringAssert.Contains(CacheMoveOutcome.Failed("npm", "disk full").ResultText, "disk full");
        Assert.IsFalse(CacheMoveOutcome.Cancelled("npm").IsOnDevDrive);
    }

    // ---- Move-all aggregation ------------------------------------------------------------------

    [TestMethod]
    public void MoveAll_MixedResults_CountsAndListsFailures()
    {
        var outcomes = new List<CacheMoveOutcome>
        {
            CacheMoveOutcome.FromReceipt(MovedReceipt(bytes: 1024 * 1024)),               // npm, 1 MB
            CacheMoveOutcome.FromReceipt(MovedReceipt(bytes: 1024 * 1024) with { }),       // 1 MB
            new() { Status = CacheMoveStatus.AlreadyOnDevDrive, ToolName = "pip" },
            CacheMoveOutcome.Failed("Cargo", "permission denied"),
        };

        CacheMoveAllOutcome all = CacheMoveAllOutcome.From(outcomes);

        Assert.AreEqual(2, all.MovedCount);
        Assert.AreEqual(1, all.AlreadyCount);
        Assert.AreEqual(1, all.FailedCount);
        Assert.IsTrue(all.HasFailures);
        Assert.AreEqual(2L * 1024 * 1024, all.TotalBytes);
        CollectionAssert.AreEqual(new[] { "Cargo" }, all.FailedTools.ToArray());
        StringAssert.Contains(all.CombinedText, "Moved 2 caches");
        StringAssert.Contains(all.CombinedText, "2 MB");
        StringAssert.Contains(all.CombinedText, "already there");
        StringAssert.Contains(all.CombinedText, "failed: Cargo");
    }

    [TestMethod]
    public void MoveAll_AllSucceeded_NoFailureClause()
    {
        var outcomes = new List<CacheMoveOutcome>
        {
            CacheMoveOutcome.FromReceipt(MovedReceipt(bytes: 512 * 1024)),
        };

        CacheMoveAllOutcome all = CacheMoveAllOutcome.From(outcomes);

        Assert.AreEqual(1, all.MovedCount);
        Assert.IsFalse(all.HasFailures);
        StringAssert.Contains(all.CombinedText, "Moved 1 cache");
        Assert.IsFalse(all.CombinedText.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MoveAll_AllFailed_ReportsNoneMovedAndLists()
    {
        var outcomes = new List<CacheMoveOutcome>
        {
            CacheMoveOutcome.Failed("npm", "x"),
            CacheMoveOutcome.Cancelled("pip"),
        };

        CacheMoveAllOutcome all = CacheMoveAllOutcome.From(outcomes);

        Assert.AreEqual(0, all.MovedCount);
        Assert.AreEqual(2, all.FailedCount);
        StringAssert.Contains(all.CombinedText, "No caches were moved");
        StringAssert.Contains(all.CombinedText, "npm");
        StringAssert.Contains(all.CombinedText, "pip");
    }

    [TestMethod]
    public void MoveAll_Empty_IsBenign()
    {
        CacheMoveAllOutcome all = CacheMoveAllOutcome.From(Array.Empty<CacheMoveOutcome>());

        Assert.AreEqual(0, all.MovedCount);
        Assert.IsFalse(all.HasFailures);
        StringAssert.Contains(all.CombinedText, "No caches were moved");
    }
}
