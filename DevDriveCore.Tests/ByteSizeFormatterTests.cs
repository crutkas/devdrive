using DevDriveCore;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class ByteSizeFormatterTests
{
    [TestMethod]
    [DataRow(0UL, "0 B")]
    [DataRow(1023UL, "1023 B")]
    [DataRow(1024UL, "1 KB")]
    [DataRow(1048576UL, "1 MB")]            // 1024^2
    [DataRow(471855104UL, "450 MB")]        // the Recovery partition on this machine
    [DataRow(1073741824UL, "1.0 GB")]       // 1024^3 -> GB uses one decimal
    [DataRow(570387447808UL, "531.2 GB")]   // G: free space on this machine
    [DataRow(1048508891136UL, "976.5 GB")]  // G: total size on this machine
    [DataRow(1149853741056UL, "1.0 TB")]    // C: total size on this machine
    [DataRow(2199023255552UL, "2.0 TB")]    // 2 * 1024^4
    public void Format_ProducesExpectedString(ulong bytes, string expected)
    {
        Assert.AreEqual(expected, ByteSizeFormatter.Format(bytes));
    }
}
