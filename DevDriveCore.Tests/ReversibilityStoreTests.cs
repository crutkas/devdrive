using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the two <see cref="DevDriveCore.Abstractions.IReversibilityStore"/> implementations:
/// the in-memory default and the JSON-file persistence. The JSON tests use a throwaway temp file
/// (unique per test, deleted in <c>finally</c>) — they never write to the real default path.
/// </summary>
[TestClass]
public sealed class ReversibilityStoreTests
{
    private static ReversibilityEntry SampleEntry(string id = "package-cache:npm_config_cache") => new()
    {
        Id = id,
        Kind = ReversibilityKinds.PackageCacheMove,
        TimestampUtc = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
        ToolName = "npm",
        EnvironmentVariable = "npm_config_cache",
        EnvironmentValueWasSet = true,
        PriorEnvironmentValue = @"D:\old",
        SourcePath = @"C:\old",
        TargetPath = @"G:\packages\npm",
        SourceDeleted = true,
    };

    // ---- InMemoryReversibilityStore ------------------------------------------------------------

    [TestMethod]
    public void InMemory_SaveAndTryGet_RoundTrips()
    {
        var store = new InMemoryReversibilityStore();
        ReversibilityEntry entry = SampleEntry();

        store.Save(entry);

        Assert.AreEqual(entry, store.TryGet(entry.Id));
    }

    [TestMethod]
    public void InMemory_TryGet_Missing_ReturnsNull() =>
        Assert.IsNull(new InMemoryReversibilityStore().TryGet("nope"));

    [TestMethod]
    public void InMemory_Save_UpsertsById()
    {
        var store = new InMemoryReversibilityStore();
        store.Save(SampleEntry() with { TargetPath = @"G:\one" });
        store.Save(SampleEntry() with { TargetPath = @"G:\two" });

        Assert.HasCount(1, store.GetAll());
        Assert.AreEqual(@"G:\two", store.TryGet("package-cache:npm_config_cache")!.TargetPath);
    }

    [TestMethod]
    public void InMemory_Remove_ReturnsTrueThenFalse()
    {
        var store = new InMemoryReversibilityStore();
        store.Save(SampleEntry());

        Assert.IsTrue(store.Remove("package-cache:npm_config_cache"));
        Assert.IsFalse(store.Remove("package-cache:npm_config_cache"));
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    public void InMemory_GetAll_ReturnsEverything()
    {
        var store = new InMemoryReversibilityStore();
        store.Save(SampleEntry("a"));
        store.Save(SampleEntry("b"));

        Assert.HasCount(2, store.GetAll());
    }

    // ---- JsonFileReversibilityStore ------------------------------------------------------------

    [TestMethod]
    public void Json_RoundTripsAllFieldsAcrossInstances()
    {
        RunWithTempFile(path =>
        {
            ReversibilityEntry entry = SampleEntry();
            new JsonFileReversibilityStore(path).Save(entry);

            // A fresh instance must read the persisted entry back, field-for-field.
            ReversibilityEntry? loaded = new JsonFileReversibilityStore(path).TryGet(entry.Id);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(entry, loaded, "Record equality proves every field round-tripped through JSON.");
        });
    }

    [TestMethod]
    public void Json_MissingFile_GetAllEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), "ddm-rev-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.IsEmpty(new JsonFileReversibilityStore(path).GetAll());
        Assert.IsNull(new JsonFileReversibilityStore(path).TryGet("anything"));
    }

    [TestMethod]
    public void Json_CorruptFile_DegradesToEmpty()
    {
        RunWithTempFile(path =>
        {
            File.WriteAllText(path, "this is not json");
            Assert.IsEmpty(new JsonFileReversibilityStore(path).GetAll());
            CleanupSiblings(path);
        });
    }

    [TestMethod]
    public void Json_CorruptFile_IsQuarantined_NotSilentlyDropped()
    {
        // F13: a corrupt store must be moved aside (so it can be inspected/recovered) rather than
        // silently treated as empty and then overwritten by the next Save.
        RunWithTempFile(path =>
        {
            File.WriteAllText(path, "{ not valid json");

            // Reading triggers the quarantine and degrades to an empty store.
            Assert.IsEmpty(new JsonFileReversibilityStore(path).GetAll());

            string[] quarantined = SiblingsMatching(path, ".corrupt-*");
            Assert.IsNotEmpty(quarantined, "The corrupt store must be quarantined to a *.corrupt-* file.");

            CleanupSiblings(path);
        });
    }

    [TestMethod]
    public void Json_AtomicReplace_LeavesValidFile_NoTempLeftover()
    {
        // F13: Persist writes to a sibling temp file then atomically replaces the target, so a valid
        // file is always left behind and no *.tmp-* scratch file lingers.
        RunWithTempFile(path =>
        {
            var store = new JsonFileReversibilityStore(path);
            store.Save(SampleEntry() with { TargetPath = @"G:\one" });
            store.Save(SampleEntry() with { TargetPath = @"G:\two" });

            Assert.IsEmpty(SiblingsMatching(path, ".tmp-*"), "Atomic replace must not leave a temp file behind.");

            // The persisted file is valid JSON a fresh instance can fully read back.
            var reread = new JsonFileReversibilityStore(path);
            Assert.HasCount(1, reread.GetAll());
            Assert.AreEqual(@"G:\two", reread.TryGet("package-cache:npm_config_cache")!.TargetPath);

            CleanupSiblings(path);
        });
    }

    [TestMethod]
    public void Json_RemovePersistsAcrossInstances()
    {
        RunWithTempFile(path =>
        {
            new JsonFileReversibilityStore(path).Save(SampleEntry());
            Assert.IsTrue(new JsonFileReversibilityStore(path).Remove("package-cache:npm_config_cache"));

            Assert.IsNull(new JsonFileReversibilityStore(path).TryGet("package-cache:npm_config_cache"));
        });
    }

    [TestMethod]
    public void Json_UpsertReplacesAcrossInstances()
    {
        RunWithTempFile(path =>
        {
            new JsonFileReversibilityStore(path).Save(SampleEntry() with { TargetPath = @"G:\one" });
            new JsonFileReversibilityStore(path).Save(SampleEntry() with { TargetPath = @"G:\two" });

            var reread = new JsonFileReversibilityStore(path);
            Assert.HasCount(1, reread.GetAll());
            Assert.AreEqual(@"G:\two", reread.TryGet("package-cache:npm_config_cache")!.TargetPath);
        });
    }

    [TestMethod]
    public void Json_DefaultPath_IsUnderLocalAppData()
    {
        // Pure string computation — must not perform any I/O or create directories.
        string path = JsonFileReversibilityStore.DefaultPath;
        StringAssert.Contains(path, "DevDriveManager");
        StringAssert.EndsWith(path, "reversibility.json");
    }

    private static string[] SiblingsMatching(string path, string suffixPattern)
    {
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(dir, Path.GetFileName(path) + suffixPattern);
    }

    private static void CleanupSiblings(string path)
    {
        foreach (string sibling in SiblingsMatching(path, ".corrupt-*").Concat(SiblingsMatching(path, ".tmp-*")))
        {
            try
            {
                File.Delete(sibling);
            }
            catch
            {
                // Best-effort cleanup of throwaway quarantine/temp siblings.
            }
        }
    }

    private static void RunWithTempFile(Action<string> body)
    {
        string path = Path.Combine(Path.GetTempPath(), "ddm-rev-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            body(path);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup of the throwaway file.
            }
        }
    }
}
