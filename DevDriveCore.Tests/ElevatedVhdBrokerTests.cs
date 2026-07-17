using System.Text.Json;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class ElevatedVhdBrokerTests
{
    [TestMethod]
    public void BuildArguments_UsesNarrowVhdVerbAndQuotedOutput()
    {
        string arguments = ElevatedVhdBroker.BuildArguments(
            VhdBrokerMode.Create,
            "YWJj",
            @"C:\Temp Folder\ddm-vhd-result.json",
            @"C:\Temp Folder\");

        Assert.AreEqual(
            "vhd --create --plan YWJj --out \"C:\\Temp Folder\\ddm-vhd-result.json\" --allowed-root \"C:\\Temp Folder\"",
            arguments);
    }

    [TestMethod]
    public void BuildArguments_RevertUsesExplicitMode()
    {
        string arguments = ElevatedVhdBroker.BuildArguments(
            VhdBrokerMode.Revert,
            "YWJj",
            @"C:\Temp\ddm-vhd-result.json",
            @"C:\Temp");

        StringAssert.StartsWith(arguments, "vhd --revert ");
    }

    [TestMethod]
    public void ClassifyCompletedInvocation_PassesThroughTrustworthyOutput()
    {
        var request = new VhdBrokerRequest { Mode = VhdBrokerMode.Create, PlanJson = "{}" };

        string json = ElevatedVhdBroker.ClassifyCompletedInvocation(request, 0, """{"Success":true}""");

        Assert.AreEqual("""{"Success":true}""", json);
    }

    [TestMethod]
    public void ClassifyCompletedInvocation_MissingOutputBecomesStateUnknown()
    {
        string planJson = JsonSerializer.Serialize(new VhdProvisionPlan
        {
            FilePath = @"C:\DevDrives\Dev.vhdx",
            DriveLetter = 'V',
        });
        var request = new VhdBrokerRequest { Mode = VhdBrokerMode.Create, PlanJson = planJson };

        string json = ElevatedVhdBroker.ClassifyCompletedInvocation(request, 5, null);
        VhdProvisionResult result = JsonSerializer.Deserialize<VhdProvisionResult>(json)!;

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Executed);
        Assert.IsTrue(result.StateUnknown);
        Assert.AreEqual(@"C:\DevDrives\Dev.vhdx", result.FilePath);
        Assert.AreEqual('V', result.DriveLetter);
        StringAssert.Contains(result.Message, "code 5");
    }
}
