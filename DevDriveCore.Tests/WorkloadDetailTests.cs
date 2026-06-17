using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Verifies each workload's honest <see cref="IWorkloadBenchmark.Detail"/> string reflects the active
/// <see cref="WorkloadProfile"/>. These are pure property checks — the process runner is a stub and no
/// fixture is ever generated, so they never touch the disk or launch a tool.
/// </summary>
[TestClass]
public sealed class WorkloadDetailTests
{
    private static IWorkloadProcessRunner Runner() => Substitute.For<IWorkloadProcessRunner>();

    [TestMethod]
    public void Git_Detail_ReflectsProfile_AndFileCount()
    {
        var git = new GitCloneWorkload(Runner());

        git.Profile = WorkloadProfile.Quick;
        string quick = git.Detail;
        StringAssert.Contains(quick, "(Quick)");
        StringAssert.Contains(quick, 1200.ToString("N0"));    // 60 dirs x 20 files

        git.Profile = WorkloadProfile.Thorough;
        string thorough = git.Detail;
        StringAssert.Contains(thorough, "(Thorough)");
        StringAssert.Contains(thorough, 15000.ToString("N0")); // 300 dirs x 50 files
    }

    [TestMethod]
    public void Npm_Detail_ReflectsProfile()
    {
        var npm = new NpmCiWorkload(Runner());

        npm.Profile = WorkloadProfile.Quick;
        string quick = npm.Detail;
        StringAssert.Contains(quick, "(Quick)");
        StringAssert.Contains(quick, "vscode-eslint"); // the real Microsoft project
        StringAssert.Contains(quick, "npm ci");

        npm.Profile = WorkloadProfile.Thorough;
        string thorough = npm.Detail;
        StringAssert.Contains(thorough, "(Thorough)");
        StringAssert.Contains(thorough, "vscode-eslint");
    }

    [TestMethod]
    public void Dotnet_Detail_ReflectsProfile()
    {
        var dotnet = new DotnetBuildWorkload(Runner());

        dotnet.Profile = WorkloadProfile.Quick;
        string quick = dotnet.Detail;
        StringAssert.Contains(quick, "(Quick)");
        StringAssert.Contains(quick, "System.Reactive"); // the real Microsoft project
        StringAssert.Contains(quick, "net6.0");

        dotnet.Profile = WorkloadProfile.Thorough;
        string thorough = dotnet.Detail;
        StringAssert.Contains(thorough, "(Thorough)");
        StringAssert.Contains(thorough, "System.Reactive");
    }

    [TestMethod]
    public void Cargo_Detail_ReflectsProfile()
    {
        var cargo = new CargoBuildWorkload(Runner());

        cargo.Profile = WorkloadProfile.Quick;
        string quick = cargo.Detail;
        StringAssert.Contains(quick, "(Quick)");
        StringAssert.Contains(quick, "edit"); // microsoft/edit, the real Rust project

        cargo.Profile = WorkloadProfile.Thorough;
        string thorough = cargo.Detail;
        StringAssert.Contains(thorough, "(Thorough)");
        StringAssert.Contains(thorough, "edit");
    }

    [TestMethod]
    public void DefaultProfile_IsQuick()
    {
        // The base default is Quick so an un-wired benchmark is the responsive one.
        Assert.AreEqual(WorkloadProfile.Quick, new GitCloneWorkload(Runner()).Profile);
    }

    [TestMethod]
    public void BuildWorkloads_Detail_StateColdCacheOnTheTestDrive()
    {
        // The honest wording must reflect the fair, symmetric protocol: each build's package cache is a
        // cold copy on the SAME drive under test (not a shared cache on C:), and the timed run is offline.
        foreach (IWorkloadBenchmark workload in new IWorkloadBenchmark[]
                 {
                     new NpmCiWorkload(Runner()),
                     new DotnetBuildWorkload(Runner()),
                     new CargoBuildWorkload(Runner()),
                 })
        {
            StringAssert.Contains(workload.Detail, "cold cache on the test drive", $"{workload.Name} detail");
            StringAssert.Contains(workload.Detail, "offline", $"{workload.Name} detail");
        }
    }
}
