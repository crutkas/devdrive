namespace DevDriveReclaim.Tests;

/// <summary>
/// A row's bytes are on the volume its path really leads to, not the one it's spelled on (Dev Config #343). A source root that
/// is a junction to another volume made the plan and the run check C:'s Recycle Bin for an item the guard then recycled into
/// D:'s, and promised C: the space.
/// </summary>
/// <remarks>
/// A test can't count on a second volume, so the choice of volume is tested with a resolver that says where a path leads, and
/// the resolving itself with a real junction (which needs no elevation) inside a <see cref="ReclaimFixture"/>.
/// </remarks>
[TestClass]
public sealed class ReclaimVolumeTests
{
    /// <summary>A local drive every test machine has, to spell paths on.</summary>
    private static readonly string Local = Path.GetPathRoot(Path.GetTempPath())!;

    /// <summary>Resolves paths under <paramref name="link"/> to the same place under <paramref name="target"/>; nothing else.</summary>
    private static Func<string, string?> Through(string link, string target) => path =>
        path.StartsWith(link, StringComparison.OrdinalIgnoreCase) ? target + path[link.Length..] : null;

    [TestMethod]
    public void APathThroughALinkIsOnTheVolumeTheLinkLeadsTo()
    {
        string link = Local + "src";

        Assert.AreEqual(@"Q:\", ReclaimVolume.RootOf(Path.Combine(link, "app", "bin"), Through(link, @"Q:\src")));
    }

    [TestMethod]
    public void APathThatCantBeOpenedIsPlacedByItsNearestAncestorThatCan()
    {
        string link = Local + "src";
        string gone = Path.Combine(link, "app", "bin", "gone.dll");
        string app = Path.Combine(link, "app");
        var asked = new List<string>();

        string root = ReclaimVolume.RootOf(gone, path =>
        {
            asked.Add(path);
            return string.Equals(path, app, StringComparison.OrdinalIgnoreCase) ? @"Q:\src\app" : null;
        });

        Assert.AreEqual(@"Q:\", root);
        CollectionAssert.AreEqual(new[] { gone, Path.GetDirectoryName(gone)!, app }, asked.ToArray(), "nearest first, and no further");
    }

    [TestMethod]
    public void APathNothingResolvesStaysOnTheVolumeItsSpelledOn()
    {
        Assert.AreEqual(Local, ReclaimVolume.RootOf(Local + @"src\app\bin", _ => null));
    }

    [TestMethod]
    public void ANetworkPathIsNeverOpened()
    {
        string root = ReclaimVolume.RootOf(
            @"\\build-server\share\src\bin",
            path => throw new AssertFailedException($"opened {path}, which could wait on the network"));

        Assert.AreEqual(@"\\build-server\share", root);
    }

    [TestMethod]
    public void ADriveThatIsntThereIsNeverOpened()
    {
        char? free = null;
        for (char letter = 'Z'; letter >= 'D' && free is null; letter--)
        {
            if (new DriveInfo($"{letter}:\\").DriveType == DriveType.NoRootDirectory)
            {
                free = letter;
            }
        }

        if (free is not char letterFree)
        {
            Assert.Inconclusive("every drive letter is in use");
            return;
        }

        string root = ReclaimVolume.RootOf(
            $@"{letterFree}:\projects\bin", path => throw new AssertFailedException($"opened {path}"));

        Assert.AreEqual($@"{letterFree}:\", root);
    }

    /// <summary>
    /// The real thing: a junction resolves to its target, as the guard resolves it before it acts, and the row's volume is the
    /// target's. On one volume both are the same root, so the resolving is what this pins.
    /// </summary>
    [TestMethod]
    public void AJunctionIsFollowedToItsTarget()
    {
        using var fixture = new ReclaimFixture();
        string bin = fixture.Dir("target", "repo", "bin");
        string link = fixture.Junction("link", Path.Combine(fixture.Root, "target"));
        string spelled = Path.Combine(link, "repo", "bin");
        if (!Directory.Exists(spelled))
        {
            Assert.Inconclusive("the junction wasn't made");
        }

        string? final = ReclaimVolume.FinalPath(spelled);

        Assert.IsNotNull(final);
        Assert.AreEqual(ReclaimVolume.FinalPath(bin), final, ignoreCase: true, "the link's path leads to the target");
        Assert.DoesNotContain(@"\link\", final, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(ReclaimPathGuard.Inspect(Row(spelled)).CanonicalPath, final, ignoreCase: true, "the guard acts on the same path");

        Assert.AreEqual(Path.GetPathRoot(final), Row(spelled).VolumeRoot);
    }

    [TestMethod]
    public void AGoneItemUnderAJunctionIsPlacedThroughTheJunction()
    {
        using var fixture = new ReclaimFixture();
        string repo = fixture.Dir("target", "repo");
        string link = fixture.Junction("link", Path.Combine(fixture.Root, "target"));
        if (!Directory.Exists(Path.Combine(link, "repo")))
        {
            Assert.Inconclusive("the junction wasn't made");
        }

        string? resolvedBy = null;
        ReclaimVolume.RootOf(Path.Combine(link, "repo", "bin", "gone.dll"), path =>
        {
            string? final = ReclaimVolume.FinalPath(path);
            resolvedBy ??= final;
            return final;
        });

        Assert.AreEqual(ReclaimVolume.FinalPath(repo), resolvedBy, ignoreCase: true);
    }

    /// <summary>The volume is worked out once and kept on the row; keeping it mustn't make two equal rows unequal.</summary>
    [TestMethod]
    public void WorkingOutTheVolumeDoesntChangeWhatTheRowEquals()
    {
        ReclaimCandidate asked = Row(Local + @"src\app\bin");
        ReclaimCandidate notAsked = Row(Local + @"src\app\bin");

        Assert.AreEqual(Local, asked.VolumeRoot);
        Assert.AreEqual(notAsked, asked);
        Assert.AreEqual(notAsked.GetHashCode(), asked.GetHashCode());
    }

    private static ReclaimCandidate Row(string path) =>
        new("build-outputs", path, "bin", 1024, ReclaimRisk.Safe, "generated", "rebuild");
}
