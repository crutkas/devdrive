using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class PackageCacheServiceTests
{
    private static FakeEnvironmentProvider StandardEnvironment() => new(new Dictionary<string, string>
    {
        ["UserProfile"] = @"C:\Users\test",
        ["AppData"] = @"C:\Users\test\AppData\Roaming",
        ["LocalAppData"] = @"C:\Users\test\AppData\Local",
    });

    private static PackageCacheInfo Npm(IReadOnlyList<PackageCacheInfo> caches) =>
        caches.Single(c => c.Name == "npm");

    [TestMethod]
    public void GetPackageCaches_ReturnsEveryCatalogueEntry()
    {
        var service = new PackageCacheService(StandardEnvironment(), new FakeFileSystemProbe());

        IReadOnlyList<PackageCacheInfo> caches = service.GetPackageCaches('G');

        Assert.HasCount(PackageCacheCatalog.Default.Count, caches);
        Assert.IsTrue(caches.Any(c => c.Name == "npm"));
        Assert.IsTrue(caches.Any(c => c.Name == "NuGet global packages"));
    }

    [TestMethod]
    public void GetPackageCaches_UnsetVariable_ResolvesDefaultTemplateOnSystemDrive()
    {
        var service = new PackageCacheService(StandardEnvironment(), new FakeFileSystemProbe());

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.AreEqual(@"%LocalAppData%\npm-cache", npm.PathTemplate);
        Assert.AreEqual(@"C:\Users\test\AppData\Local\npm-cache", npm.ResolvedPath);
        Assert.IsFalse(npm.EnvironmentVariableSet);
        Assert.IsNull(npm.EnvironmentValue);
        Assert.AreEqual('C', npm.DriveLetter);
        Assert.IsFalse(npm.OnDevDrive);
    }

    [TestMethod]
    public void GetPackageCaches_DetectsExistingDirectory()
    {
        var fs = new FakeFileSystemProbe(new[] { @"C:\Users\test\AppData\Local\npm-cache" });
        var service = new PackageCacheService(StandardEnvironment(), fs);

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.IsTrue(npm.Detected);
    }

    [TestMethod]
    public void GetPackageCaches_MissingDirectory_NotDetected()
    {
        var service = new PackageCacheService(StandardEnvironment(), new FakeFileSystemProbe());

        Assert.IsFalse(Npm(service.GetPackageCaches('G')).Detected);
    }

    [TestMethod]
    public void GetPackageCaches_EnvironmentOverride_RedirectsToDevDrive()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", @"G:\packages\npm");
        var fs = new FakeFileSystemProbe(new[] { @"G:\packages\npm" });
        var service = new PackageCacheService(env, fs);

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.IsTrue(npm.EnvironmentVariableSet);
        Assert.AreEqual(@"G:\packages\npm", npm.EnvironmentValue);
        Assert.AreEqual(@"G:\packages\npm", npm.ResolvedPath);
        Assert.AreEqual('G', npm.DriveLetter);
        Assert.IsTrue(npm.OnDevDrive);
        Assert.IsTrue(npm.Detected);
    }

    [TestMethod]
    public void GetPackageCaches_WithoutDevDrive_StillDetectsCacheWithoutDevClassification()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", @"G:\packages\npm");
        var fs = new FakeFileSystemProbe(new[] { @"G:\packages\npm" });
        var service = new PackageCacheService(env, fs);

        PackageCacheInfo npm = Npm(service.GetPackageCaches(null));

        Assert.IsTrue(npm.Detected);
        Assert.AreEqual('G', npm.DriveLetter);
        Assert.IsFalse(npm.OnDevDrive);
    }

    [TestMethod]
    public void GetPackageCaches_EnvironmentOverrideWithVariables_IsExpanded()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", @"%UserProfile%\custom-npm");
        var service = new PackageCacheService(env, new FakeFileSystemProbe());

        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        Assert.AreEqual(@"C:\Users\test\custom-npm", npm.ResolvedPath);
    }

    [TestMethod]
    public void GetPackageCaches_DevDriveLetterIsCaseInsensitive()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("npm_config_cache", @"G:\packages\npm");
        var service = new PackageCacheService(env, new FakeFileSystemProbe());

        // Lower-case 'g' must still classify the G: path as on the Dev Drive.
        Assert.IsTrue(Npm(service.GetPackageCaches('g')).OnDevDrive);
    }

    [TestMethod]
    public void Catalogue_HasSingleSharedNuGetPackagesRow()
    {
        var service = new PackageCacheService(StandardEnvironment(), new FakeFileSystemProbe());

        IReadOnlyList<PackageCacheInfo> caches = service.GetPackageCaches('G');

        // The NuGet global-packages folder (NUGET_PACKAGES) is shared by NuGet/dotnet/MSBuild/Visual
        // Studio; it must appear exactly once — no separate "Visual Studio package cache" duplicate.
        int nugetPackagesRows = caches.Count(c =>
            string.Equals(c.EnvironmentVariable, "NUGET_PACKAGES", StringComparison.Ordinal));
        Assert.AreEqual(1, nugetPackagesRows);
        Assert.IsFalse(caches.Any(c => c.Name == "Visual Studio package cache"));
    }

    [TestMethod]
    public async Task CalculateSizeAsync_NotDetected_ReturnsZeroWithoutProbing()
    {
        var service = new PackageCacheService(StandardEnvironment(), new FakeFileSystemProbe());
        PackageCacheInfo npm = Npm(service.GetPackageCaches('G')); // not detected

        ulong size = await service.CalculateSizeAsync(npm, TimeSpan.FromSeconds(1));

        Assert.AreEqual(0UL, size);
    }

    [TestMethod]
    public async Task CalculateSizeAsync_Detected_ReturnsProbeSize()
    {
        const string path = @"C:\Users\test\AppData\Local\npm-cache";
        var fs = new FakeFileSystemProbe(
            new[] { path },
            new Dictionary<string, ulong> { [path] = 5UL * 1024 * 1024 });
        var service = new PackageCacheService(StandardEnvironment(), fs);
        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        ulong size = await service.CalculateSizeAsync(npm, TimeSpan.FromSeconds(1));

        Assert.AreEqual(5UL * 1024 * 1024, size);
    }

    [TestMethod]
    public void BuildMovePlan_TargetsDevDriveAndDescribesEnvVar()
    {
        FakeEnvironmentProvider env = StandardEnvironment();
        var fs = new FakeFileSystemProbe(new[] { @"C:\Users\test\AppData\Local\npm-cache" });
        var service = new PackageCacheService(env, fs);
        PackageCacheInfo npm = Npm(service.GetPackageCaches('G'));

        PackageCacheMovePlan plan = service.BuildMovePlan(npm, 'G');

        Assert.AreEqual("npm", plan.ToolName);
        Assert.AreEqual(@"C:\Users\test\AppData\Local\npm-cache", plan.SourcePath);
        Assert.AreEqual(@"G:\packages\npm", plan.TargetPath);
        Assert.AreEqual("npm_config_cache", plan.EnvironmentVariable);
    }

    [TestMethod]
    public void BuildMovePlan_MultiWordToolName_ProducesCleanFolderToken()
    {
        FakeEnvironmentProvider env = StandardEnvironment().Set("GOMODCACHE", @"C:\Users\test\go\pkg\mod");
        var fs = new FakeFileSystemProbe(new[] { @"C:\Users\test\go\pkg\mod" });
        var service = new PackageCacheService(env, fs);
        PackageCacheInfo go = service.GetPackageCaches('G').Single(c => c.Name == "Go modules");

        PackageCacheMovePlan plan = service.BuildMovePlan(go, 'G');

        Assert.AreEqual(@"G:\packages\go-modules", plan.TargetPath);
    }
}
