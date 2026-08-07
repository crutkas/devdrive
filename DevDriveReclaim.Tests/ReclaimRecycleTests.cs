using System.Runtime.InteropServices;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The recycle path, actually recycled.
/// </summary>
/// <remarks>
/// This class exists because of a crash. Every test in <see cref="ReclaimExecutorTests"/> defaults
/// <c>supportsRecycleBin: false</c> to keep the developer's Recycle Bin clean, so all of them ran
/// through <c>DeletePermanently</c> — and the shell path, which is the one nearly every provider
/// actually asks for, was never once executed by a test. It contained an incorrect struct packing
/// that killed the process with an access violation on the first real run.
/// <para>
/// An interop signature is not verified by a test that never calls it. These tests pay the price of
/// touching the real Recycle Bin, and clean up after themselves on a best-effort basis, because the
/// alternative is a marshalling bug that no amount of unit testing around it can find.
/// </para>
/// </remarks>
[TestClass]
public sealed class ReclaimRecycleTests
{
    private static ReclaimCandidate Candidate(string path) =>
        new(
            "build-output",
            path,
            Path.GetFileName(path.TrimEnd('\\', '/')),
            1024,
            ReclaimRisk.Safe,
            "test",
            "test",
            supportsRecycleBin: true);

    /// <summary>
    /// The regression test for the crash: a real folder, through the real shell call. Before the
    /// packing fix this did not fail — it terminated the test host.
    /// </summary>
    [TestMethod]
    public async Task AFolderIsRecycledWithoutTearingDownTheProcess()
    {
        using ReclaimFixture fixture = new();
        string folder = fixture.Dir("obj");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "x");

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync(
            [Candidate(folder)], null, CancellationToken.None);

        try
        {
            Assert.IsFalse(Directory.Exists(folder), "The folder should have left the disk.");
            Assert.AreEqual(1, outcome.RemovedCount);
            Assert.AreEqual(0, outcome.FailedCount);
        }
        finally
        {
            PurgeFromRecycleBin(Path.GetFileName(folder));
        }
    }

    [TestMethod]
    public async Task AFileIsRecycledWithoutTearingDownTheProcess()
    {
        using ReclaimFixture fixture = new();
        string file = fixture.File("blob.bin", 4096);

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync(
            [Candidate(file)], null, CancellationToken.None);

        try
        {
            Assert.IsFalse(System.IO.File.Exists(file), "The file should have left the disk.");
            Assert.AreEqual(1, outcome.RemovedCount);
        }
        finally
        {
            PurgeFromRecycleBin(Path.GetFileName(file));
        }
    }

    /// <summary>
    /// The status has to say which of the two things happened, because the confirmation promised the
    /// Recycle Bin and <c>FOF_ALLOWUNDO</c> silently degrades to a permanent delete when the shell
    /// feels like it. A temp file small enough to fit the bin must come back as
    /// <see cref="ReclaimItemStatus.Recycled"/>, never <see cref="ReclaimItemStatus.Deleted"/>.
    /// </summary>
    [TestMethod]
    public async Task ASmallItemReportsRecycledRatherThanDeleted()
    {
        using ReclaimFixture fixture = new();
        string file = fixture.File("small.bin", 64);

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync(
            [Candidate(file)], null, CancellationToken.None);

        try
        {
            Assert.AreEqual(ReclaimItemStatus.Recycled, outcome.Items[0].Status);
        }
        finally
        {
            PurgeFromRecycleBin(Path.GetFileName(file));
        }
    }

    /// <summary>
    /// The guard runs ahead of the shell call, so a refused path must never reach it. Worth asserting
    /// on the recycle path specifically: the permanent path would merely throw, whereas handing the
    /// shell something it should not have is how a delete tool destroys a system directory.
    /// </summary>
    [TestMethod]
    public async Task AProtectedPathNeverReachesTheShell()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync(
            [Candidate(windows)], null, CancellationToken.None);

        Assert.AreEqual(0, outcome.RemovedCount);
        Assert.AreEqual(ReclaimItemStatus.Refused, outcome.Items[0].Status);
        Assert.IsTrue(Directory.Exists(windows));
    }

    /// <summary>
    /// Best-effort removal of what a test just recycled, so the suite does not slowly fill the
    /// developer's Recycle Bin. Failure is deliberately ignored: leaving a 4 KB test fixture in the
    /// bin is a nuisance, whereas failing a passing test over cleanup would be a lie about the code.
    /// </summary>
    private static void PurgeFromRecycleBin(string name)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return;
            }

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            try
            {
                // 10 == ssfBITBUCKET, the Recycle Bin.
                object? bin = shellType.InvokeMember(
                    "NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [10]);
                if (bin is null)
                {
                    return;
                }

                object? items = bin.GetType().InvokeMember(
                    "Items", System.Reflection.BindingFlags.InvokeMethod, null, bin, null);
                if (items is null)
                {
                    return;
                }

                int count = (int)(items.GetType().InvokeMember(
                    "Count", System.Reflection.BindingFlags.GetProperty, null, items, null) ?? 0);

                for (int i = count - 1; i >= 0; i--)
                {
                    object? item = items.GetType().InvokeMember(
                        "Item", System.Reflection.BindingFlags.InvokeMethod, null, items, [i]);
                    if (item is null)
                    {
                        continue;
                    }

                    string? itemName = item.GetType().InvokeMember(
                        "Name", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;

                    if (!string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Marshal.ReleaseComObject(item);
                        continue;
                    }

                    string? path = item.GetType().InvokeMember(
                        "Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;

                    if (path is not null)
                    {
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        else if (System.IO.File.Exists(path))
                        {
                            System.IO.File.Delete(path);
                        }
                    }

                    Marshal.ReleaseComObject(item);
                    break;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch
        {
            // See the summary: cleanup is a courtesy, not an assertion.
        }
    }
}
