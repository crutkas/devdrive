using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>Which PATH folders <see cref="PathProbe"/> looks in.</summary>
[TestClass]
public sealed class PathProbeTests
{
    [TestMethod]
    [DataRow(@"\\192.0.2.10\tools\bin", true)]
    [DataRow(@"\\no-such-host\x", true)]
    [DataRow(@"\\?\UNC\server\share\bin", true)]
    [DataRow(@"\\?\C:\tools", false)]
    [DataRow(@"\\.\pipe\x", false)]
    [DataRow(@"C:\Windows\System32", false)]
    [DataRow("bin", false)]
    [DataRow("", false)]
    public void AFolderOnAnotherComputerIsANetworkFolder(string folder, bool network)
    {
        Assert.AreEqual(network, PathProbe.IsNetworkFolder(folder));
    }

    [TestMethod]
    public void ResolveWaitsBrieflyOnAShareThatDoesNotAnswerAndFindsTheToolAfterIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "devdrive-pathprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string tool = Path.Combine(folder, "devdrive-probe-tool.exe");
        File.WriteAllBytes(tool, []);
        try
        {
            // 192.0.2.0/24 is for documentation: nothing answers there, so a lookup on it would wait on the network.
            var probe = new PathProbe(() => @"\\192.0.2.10\tools\bin;" + folder);
            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.AreEqual(tool, probe.Resolve("devdrive-probe-tool.exe"));
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void AShareThatDoesNotAnswerIsWaitedOnOnce()
    {
        string share = $@"\\devdrive-test-{Guid.NewGuid():N}\tools";
        TimeSpan limit = TimeSpan.FromMilliseconds(200);
        using var never = new ManualResetEventSlim();
        int probes = 0;
        bool Hangs(string _)
        {
            Interlocked.Increment(ref probes);
            never.Wait(TimeSpan.FromSeconds(30));
            return true;
        }

        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsFalse(PathProbe.Exists($@"{share}\bin\foundry.exe", _ => true, Hangs, limit));
            Assert.IsTrue(timer.Elapsed >= limit * 0.9, $"took {timer.Elapsed}");

            timer.Restart();
            Assert.IsFalse(PathProbe.Exists($@"{share.ToUpperInvariant()}\other\ollama.exe", _ => true, Hangs, limit));
            Assert.IsTrue(timer.Elapsed < limit, $"waited {timer.Elapsed} on a share that didn't answer before");
            Assert.AreEqual(1, probes);
        }
        finally
        {
            never.Set();
        }
    }

    [TestMethod]
    public void AShareThatAnswersIsCheckedAsBefore()
    {
        string share = $@"\\devdrive-test-{Guid.NewGuid():N}\tools";
        TimeSpan generous = TimeSpan.FromSeconds(10);
        TimeSpan limit = TimeSpan.FromMilliseconds(200);
        Assert.IsTrue(PathProbe.Exists($@"{share}\bin\foundry.exe", p => p.EndsWith("foundry.exe", StringComparison.Ordinal), _ => true, generous));
        Assert.IsFalse(PathProbe.Exists($@"{share}\bin\ollama.exe", p => p.EndsWith("foundry.exe", StringComparison.Ordinal), _ => true, generous));

        using var never = new ManualResetEventSlim();
        try
        {
            string slow = $@"\\devdrive-test-{Guid.NewGuid():N}\tools";
            Assert.IsFalse(PathProbe.Exists($@"{slow}\bin\foundry.exe", _ => never.Wait(TimeSpan.FromSeconds(30)), _ => true, limit));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsFalse(PathProbe.Exists($@"{slow}\bin\ollama.exe", _ => true, _ => true, generous));
            Assert.IsTrue(timer.Elapsed < limit, $"waited {timer.Elapsed} after a file check there ran out");
        }
        finally
        {
            never.Set();
        }
    }

    [TestMethod]
    [DataRow(@"\\server\share\bin\x.exe", @"\\server\share")]
    [DataRow(@"\\?\UNC\server\share\bin", @"\\server\share")]
    [DataRow(@"\\server", @"\\server")]
    [DataRow(@"Z:\tools\bin", @"Z:\")]
    public void APathIsOnItsShare(string path, string share)
    {
        Assert.AreEqual(share, PathProbe.ShareOf(path));
    }
}
