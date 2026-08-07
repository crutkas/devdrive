using DevDriveManager.Services;
using DevDriveManager.ViewModels;
using DevDriveReclaim;

namespace DevDriveCore.Tests;

/// <summary>
/// The confirmation is the last honest moment before data stops existing. These tests treat its
/// wording as behaviour, because the two facts it carries — how much of this cannot be undone, and
/// how bad the worst item is — are the only inputs a person has to the decision.
/// </summary>
[TestClass]
public sealed class ReclaimConfirmationTests
{
    private static ReclaimConfirmationRequest Request(
        int itemCount = 3,
        long bytes = 5_000_000_000,
        ReclaimRisk risk = ReclaimRisk.Safe,
        int carefulCount = 0,
        int permanentCount = 0,
        long permanentBytes = 0,
        IReadOnlyList<string>? permanentNames = null) =>
        new(
            itemCount,
            bytes,
            risk,
            carefulCount,
            permanentCount,
            permanentBytes,
            permanentNames ?? [],
            "G: 4.2 GB · C: 0.4 GB");

    [TestMethod]
    public void EverythingRecyclableSaysItCanBeRestored()
    {
        ReclaimConfirmationRequest request = Request();

        Assert.IsFalse(request.HasPermanent);
        StringAssert.Contains(request.RecoveryText, "restored");
        Assert.IsFalse(
            request.Headline.Contains("permanently", StringComparison.OrdinalIgnoreCase),
            "A fully recyclable batch must not carry the word 'permanently' anywhere in its headline.");
    }

    [TestMethod]
    public void AFullyPermanentBatchSaysItCannotBeUndone()
    {
        ReclaimConfirmationRequest request = Request(
            itemCount: 2,
            risk: ReclaimRisk.Check,
            permanentCount: 2,
            permanentBytes: 5_000_000_000,
            permanentNames: ["node_modules", "target"]);

        Assert.IsTrue(request.HasPermanent);
        StringAssert.Contains(request.RecoveryText, "cannot be undone");
        Assert.IsFalse(
            request.RecoveryText.Contains("restored", StringComparison.OrdinalIgnoreCase),
            "Nothing survives this batch, so the text must not offer the Recycle Bin as a way back.");
    }

    /// <summary>
    /// The mixed case is the one a person can get wrong, so it names the permanent items rather
    /// than counting them. "2 of these cannot go to the Recycle Bin" says a decision exists without
    /// saying enough to make it.
    /// </summary>
    [TestMethod]
    public void AMixedBatchNamesWhatCannotComeBack()
    {
        ReclaimConfirmationRequest request = Request(
            itemCount: 5,
            permanentCount: 2,
            permanentBytes: 900_000_000,
            permanentNames: ["Recycle Bin on G:", "huge-artifact.zip"]);

        StringAssert.Contains(request.RecoveryText, "Recycle Bin on G:");
        StringAssert.Contains(request.RecoveryText, "huge-artifact.zip");
        StringAssert.Contains(request.RecoveryText, "Everything else can be restored");
    }

    /// <summary>
    /// The irreversible part leads, because it is the fact that should change someone's mind. A
    /// headline that opens with the amount freed is an advertisement, not a confirmation.
    /// </summary>
    [TestMethod]
    public void ThePermanentAmountAppearsInTheHeadline()
    {
        ReclaimConfirmationRequest request = Request(
            bytes: 5_000_000_000,
            permanentCount: 1,
            permanentBytes: 2_000_000_000,
            permanentNames: ["Recycle Bin on C:"]);

        StringAssert.Contains(request.Headline, "permanently");
        StringAssert.Contains(request.Headline, request.PermanentBytesText);
        StringAssert.Contains(request.Headline, request.BytesText);
    }

    [TestMethod]
    [DataRow(ReclaimRisk.Safe, false)]
    [DataRow(ReclaimRisk.Check, false)]
    [DataRow(ReclaimRisk.Careful, true)]
    public void OnlyCarefulDemandsTyping(ReclaimRisk risk, bool expected)
    {
        Assert.AreEqual(expected, Request(risk: risk, carefulCount: 1).RequiresTypedConfirmation);
    }

    /// <summary>
    /// The risk line states a consequence, not a tier name. "Careful" means nothing to someone who
    /// has not read the risk model.
    /// </summary>
    [TestMethod]
    public void TheRiskLineDescribesTheConsequenceNotTheTier()
    {
        string text = Request(risk: ReclaimRisk.Careful, carefulCount: 3).RiskText;

        StringAssert.Contains(text, "exists nowhere else");
        StringAssert.Contains(text, "3 items");
        Assert.IsFalse(
            text.Contains("Careful", StringComparison.Ordinal),
            "The tier name is jargon; the dialog must spell out what it means.");
    }

    [TestMethod]
    public void ASingleCarefulItemReadsAsOneNotOneItems()
    {
        StringAssert.Contains(Request(risk: ReclaimRisk.Careful, carefulCount: 1).RiskText, "1 item can");
    }

    [TestMethod]
    [DataRow(1, "1 item")]
    [DataRow(2, "2 items")]
    [DataRow(1234, "1,234 items")]
    public void ItemCountsReadAsEnglish(int count, string expected)
    {
        Assert.AreEqual(expected, Request(itemCount: count).ItemCountText);
    }

    /// <summary>
    /// The split by volume is the one fact the confirmation carries that the room's own bars cannot,
    /// once the selection is frozen. It travels verbatim.
    /// </summary>
    [TestMethod]
    public void TheVolumeSplitIsCarried()
    {
        Assert.AreEqual("G: 4.2 GB · C: 0.4 GB", Request().VolumeSplitText);
    }
}

/// <summary>
/// The safe-mutation seam, asserted from the outside: under the test flag the app must hand out an
/// executor that removes nothing, and it must still be a real executor rather than a null object,
/// so the UI exercises the same code path a user reaches.
/// </summary>
[TestClass]
public sealed class SafeFakeReclaimExecutorTests
{
    private static ReclaimCandidate Candidate(string path, string name) =>
        new(
            categoryId: "build-output",
            path: path,
            displayName: name,
            sizeBytes: 1_000_000,
            risk: ReclaimRisk.Safe,
            reason: "test",
            recoveryHint: "rebuild",
            supportsRecycleBin: true);

    [TestMethod]
    public async Task NothingIsRemovedFromDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-safe-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "obj");
        Directory.CreateDirectory(folder);
        try
        {
            SafeFakeReclaimExecutor executor = new();

            ReclaimOutcome outcome = await executor.ExecuteAsync([Candidate(folder, "obj")], null, default);

            Assert.IsTrue(Directory.Exists(folder), "The safe fake must never touch the disk.");
            Assert.AreEqual(1, outcome.RemovedCount);
            CollectionAssert.AreEqual(new[] { folder }, executor.Removed.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The fake still runs the real guard. A build that only ever exercises the fake would otherwise
    /// never discover that a provider is emitting a protected path, and the first time anyone found
    /// out would be on a machine with the flag off.
    /// </summary>
    [TestMethod]
    public async Task AProtectedPathIsStillRefused()
    {
        SafeFakeReclaimExecutor executor = new();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        ReclaimOutcome outcome = await executor.ExecuteAsync([Candidate(windows, "Windows")], null, default);

        Assert.AreEqual(0, outcome.RemovedCount);
        Assert.AreEqual(1, outcome.FailedCount);
        Assert.IsEmpty(executor.Removed);
    }

    /// <summary>
    /// The fake reduces overlaps exactly as the real one does, so a selection that looks like two
    /// items but is really one nested inside the other cannot report double the bytes freed under
    /// test and the honest number in production.
    /// </summary>
    [TestMethod]
    public async Task NestedSelectionsCollapseToTheirRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-safe-" + Guid.NewGuid().ToString("N"));
        string outer = Path.Combine(root, "repo");
        string inner = Path.Combine(outer, "bin");
        Directory.CreateDirectory(inner);
        try
        {
            SafeFakeReclaimExecutor executor = new();

            ReclaimOutcome outcome = await executor.ExecuteAsync(
                [Candidate(outer, "repo"), Candidate(inner, "bin")],
                null,
                default);

            Assert.HasCount(1, executor.Removed);
            Assert.AreEqual(outer, executor.Removed[0]);
            Assert.AreEqual(1_000_000, outcome.BytesFreed, "Only the containing root's bytes count once.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The seam has to be honoured by the one factory the whole app goes through. Only the safe
    /// direction is asserted: the flag is an OR over process and user scope, so a machine that has
    /// the user-scope value set — which is exactly the machine the UI suite runs on — cannot
    /// observe the production branch. Asserting the production branch would in any case only prove
    /// that a constructor ran.
    /// </summary>
    [TestMethod]
    public void TheCompositionRootHandsOutTheFakeUnderTheFlag()
    {
        string? original = Environment.GetEnvironmentVariable(MutationComposition.SafeMutationEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MutationComposition.SafeMutationEnvVar, "1");

            Assert.IsInstanceOfType<SafeFakeReclaimExecutor>(MutationComposition.CreateReclaimExecutor());
        }
        finally
        {
            Environment.SetEnvironmentVariable(MutationComposition.SafeMutationEnvVar, original);
        }
    }
}
