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
    public void ResolveSkipsANetworkFolderAndFindsTheToolAfterIt()
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
}
