using DevDriveReclaim.Providers;
using DevDriveReclaim.Providers.Models;

namespace DevDriveReclaim.Tests;

/// <summary>
/// A duplicate is another file, not another name for the same one, and nothing in a <c>.git</c> is a copy (Dev Config #348).
/// Two hard links of one file were offered as copies of each other, so removing one freed nothing while the receipt counted
/// it, and a local clone's hard-linked pack file was offered from inside its <c>.git</c>.
/// </summary>
/// <remarks>Nothing is removed here: these tests only scan, inside a <see cref="ReclaimFixture"/>.</remarks>
[TestClass]
public sealed class DuplicateHardLinkTests
{
    private const int Mb = 1024 * 1024;

    private static byte[] Payload()
    {
        byte[] bytes = new byte[5 * Mb];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static async Task<IReadOnlyList<ReclaimCandidate>> Duplicates(string root) =>
        await new DuplicateFileReclaimProvider().ScanAsync(
            new ReclaimScanContext([Path.GetPathRoot(root)!], [root], minimumCandidateBytes: 1), null, CancellationToken.None);

    private static string Linked(ReclaimFixture fixture, string relativePath, string target)
    {
        string link = fixture.HardLink(relativePath, target);
        if (ModelFiles.LinkCount(target) < 2)
        {
            Assert.Inconclusive("the hard link wasn't made");
        }

        return link;
    }

    [TestMethod]
    public async Task TwoNamesOfOneFileAreNotCopiesOfEachOther()
    {
        using var fixture = new ReclaimFixture();
        string store = fixture.FileWithContent(@"store\big.bin", Payload());
        Linked(fixture, @"project\node_modules\pkg\big.bin", store);

        Assert.IsEmpty(await Duplicates(fixture.Root), "removing one name of a file with two frees nothing");
    }

    [TestMethod]
    public async Task AFileWithAnotherNameIsKeptAndTheRealCopyIsOffered()
    {
        using var fixture = new ReclaimFixture();
        byte[] data = Payload();
        string scan = fixture.Dir("scan");

        // The file's other name is outside the scan, so inside it the file looks like any other copy, at the longer path.
        string outside = fixture.FileWithContent(@"outside\big.bin", data);
        string linked = Linked(fixture, @"scan\deep\nested\packages\big.bin", outside);
        string copy = fixture.FileWithContent(@"scan\c.bin", data);

        ReclaimCandidate row = (await Duplicates(scan)).Single();

        Assert.AreEqual(copy, row.Path, "the file with another name was offered, and removing it frees nothing");
        Assert.AreEqual(linked, row.KeptCopy);
        Assert.IsGreaterThan(0, row.SizeBytes);
    }

    [TestMethod]
    public async Task NothingUnderDotGitIsOfferedOrKept()
    {
        using var fixture = new ReclaimFixture();
        byte[] pack = Payload();
        fixture.FileWithContent(@"one\.git\objects\pack\pack-1.pack", pack);
        fixture.FileWithContent(@"two\.git\objects\pack\pack-1.pack", pack);

        byte[] data = Payload();
        fixture.FileWithContent(@"r\.git\x.bin", data);
        string first = fixture.FileWithContent(@"work\one\data.bin", data);
        string second = fixture.FileWithContent(@"work\two\data.bin", data);

        ReclaimCandidate row = (await Duplicates(fixture.Root)).Single();

        CollectionAssert.AreEquivalent(new[] { first, second }, new[] { row.Path, row.KeptCopy! });
    }

    [TestMethod]
    public async Task ALocalClonesHardLinkedPackIsNotOffered()
    {
        if (!TestGit.IsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string original = TestGit.Repo(fixture, "a");
        fixture.FileWithContent(@"a\data.bin", Payload());
        TestGit.Run(original, "add data.bin");
        TestGit.Run(original, "commit -m data");
        TestGit.Run(original, "gc -q");
        TestGit.Run(fixture.Root, "clone -q a second-clone-of-a");

        IReadOnlyList<ReclaimCandidate> found = await Duplicates(fixture.Root);

        Assert.IsFalse(
            found.Any(c => c.Path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) ||
                c.KeptCopy!.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", found.Select(c => c.Path)));

        // The two checkouts of data.bin are two files, so one of them still is a copy.
        Assert.AreEqual("data.bin", found.Single().DisplayName);
    }
}
