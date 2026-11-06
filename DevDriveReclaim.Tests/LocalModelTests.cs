using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DevDriveReclaim.Providers.Models;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The AI models category on fixture stores laid out as each manager lays them out (checked against Ollama 0.40, huggingface_hub
/// 2.4, LM Studio and Foundry Local 0.13 on Windows): what's found, what it's sized at, what's left out, and what removal does and
/// refuses. Nothing here reaches a real model manager: the runtime is a fake.
/// </summary>
[TestClass]
public sealed class LocalModelTests
{
    private const long MiB = 1L << 20;

    // ---- Ollama

    [TestMethod]
    public async Task OllamaNamesThatShareWeightsAreOneRowSizedByWhatOnlyTheyUse()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(4 * MiB);
        string license = ollama.Blob(1024);
        string config = ollama.Blob(400);
        string other = ollama.Blob(3 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3", "0.6b", config, [weights, license]);
        ollama.Manifest("manifests", "registry.ollama.ai", "library", "qwen3", "0.6b", config, [weights, license]);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3-alias", "latest", ollama.Blob(300), [weights, license], later: 1);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "smollm2", "135m", ollama.Blob(500), [other, license]);

        IReadOnlyList<ReclaimCandidate> found = await Scan(ollama.Locations(fixture));

        Assert.HasCount(2, found);
        ReclaimCandidate qwen = found.Single(c => c.DisplayName == "qwen3:0.6b");
        CollectionAssert.AreEqual(new[] { "qwen3:0.6b", "qwen3-alias:latest" }, qwen.Owner!.Names.ToArray());
        Assert.AreEqual(4 * MiB + 400 + 300, qwen.SizeBytes, "the weights once, plus the blobs only these names use; the shared license isn't theirs");
        Assert.AreEqual(ReclaimRisk.Check, qwen.Risk);
        Assert.IsFalse(qwen.SupportsRecycleBin);
        StringAssert.Contains(qwen.Detail, "also qwen3-alias:latest");
        StringAssert.Contains(qwen.Path, "manifests-v2");
        Assert.AreEqual(3 * MiB + 500, found.Single(c => c.DisplayName == "smollm2:135m").SizeBytes);
    }

    [TestMethod]
    public void OllamaNamesAreTheOnesOllamaShows()
    {
        Assert.AreEqual("qwen3:0.6b", OllamaStore.NameOf("ollama.com", "library", "qwen3", "0.6b"));
        Assert.AreEqual("qwen3:0.6b", OllamaStore.NameOf("registry.ollama.ai", "library", "qwen3", "0.6b"));
        Assert.AreEqual("someone/model:latest", OllamaStore.NameOf("ollama.com", "someone", "model", "latest"));
        Assert.AreEqual("hf.co/bartowski/Llama-3.2-1B-Instruct-GGUF:Q4_K_M", OllamaStore.NameOf("hf.co", "bartowski", "Llama-3.2-1B-Instruct-GGUF", "Q4_K_M"));
        Assert.AreEqual("localhost:5000/team/model:v1", OllamaStore.NameOf("localhost%3A5000", "team", "model", "v1"));
        Assert.AreEqual(@"C:\m\blobs\sha256-ab", OllamaStore.BlobPath(@"C:\m", "sha256:ab"));
    }

    [TestMethod]
    public async Task AMissingBlobFreesNothingAndAnUnreadableManifestIsAGap()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(2 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "broken", "latest", weights, [weights, "sha256:" + new string('e', 64)]);
        fixture.File(@"ollama\manifests-v2\ollama.com\library\garbled\latest", 10);

        var context = new ReclaimScanContext([], []);
        IReadOnlyList<ReclaimCandidate> found = await new LocalModelReclaimProvider(() => ollama.Locations(fixture), new FakeRuntime())
            .ScanAsync(context, null, CancellationToken.None);

        Assert.AreEqual(2 * MiB, found.Single().SizeBytes);
        ReclaimGap gap = context.Gaps.Single();
        Assert.AreEqual(LocalModelReclaimProvider.ReclaimCategory.Id, gap.CategoryId);
        StringAssert.Contains(gap.Reason, "manifest");
    }

    [TestMethod]
    public async Task ARunningOllamaThatListsModelsElsewhereIsAGapNotNothing()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "here", "latest", ollama.Blob(400), [ollama.Blob(2 * MiB)]);

        var context = new ReclaimScanContext([], []);
        await new LocalModelReclaimProvider(() => ollama.Locations(fixture), new FakeRuntime { Listed = ["here:latest", "elsewhere:latest"] })
            .ScanAsync(context, null, CancellationToken.None);

        StringAssert.Contains(context.Gaps.Single().Reason, "isn't in this folder");
    }

    [TestMethod]
    public void OllamaRemovesEveryNameWithOllamaRm()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(2 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3", "0.6b", ollama.Blob(400), [weights]);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3-alias", "latest", ollama.Blob(400), [weights], later: 1);
        ReclaimCandidate candidate = Candidate(LocalModelTools.Ollama, ollama.Store, ollama.ManifestPath("qwen3", "0.6b"), "qwen3:0.6b", "qwen3-alias:latest");
        var runtime = new FakeRuntime { Serving = ollama.Store, OnRun = (_, args) => ollama.Remove(args.Skip(1)) };

        ReclaimItemOutcome outcome = new LocalModelRemoval(runtime, () => ollama.Locations(fixture)).Remove(candidate, CancellationToken.None);

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Status, outcome.Message);
        CollectionAssert.AreEqual(new[] { "rm", "qwen3:0.6b", "qwen3-alias:latest" }, runtime.Commands.Single().ToArray());
        Assert.AreEqual("http://127.0.0.1:11434", runtime.Environments.Single()!["OLLAMA_HOST"], "the Ollama that was asked is the one that removes");
    }

    [TestMethod]
    public void OllamaKeepsALoadedModelAndOneThatChangedOrCantBeAsked()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(2 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3", "0.6b", weights, [weights]);
        ReclaimCandidate candidate = Candidate(LocalModelTools.Ollama, ollama.Store, ollama.ManifestPath("qwen3", "0.6b"), "qwen3:0.6b");

        string? Why(FakeRuntime runtime) => new LocalModelRemoval(runtime, () => ollama.Locations(fixture)).WhyKept(candidate);

        StringAssert.Contains(Why(new FakeRuntime { Serving = ollama.Store, Loaded = ["qwen3:0.6b"] }), "in use");
        StringAssert.Contains(Why(new FakeRuntime { Serving = ollama.Store, Loaded = null }), "didn't say");
        StringAssert.Contains(Why(new FakeRuntime { Tools = [] }), "isn't installed");
        StringAssert.Contains(Why(new FakeRuntime { OllamaRunning = false }), "isn't running");
        StringAssert.Contains(Why(new FakeRuntime { OllamaAnswers = false }), "didn't answer");
        StringAssert.Contains(Why(new FakeRuntime { Listed = ["qwen3:0.6b"] }), "another models folder");
        StringAssert.Contains(Why(new FakeRuntime()), "another models folder");
        Assert.IsNull(Why(new FakeRuntime { Serving = ollama.Store }));

        ollama.Manifest("manifests-v2", "ollama.com", "library", "copied", "latest", weights, [weights], later: 2);
        StringAssert.Contains(Why(new FakeRuntime { Serving = ollama.Store }), "changed since the scan");

        var runtime = new FakeRuntime { Serving = ollama.Store, Loaded = ["copied:latest"] };
        ReclaimItemOutcome kept = new LocalModelRemoval(runtime, () => ollama.Locations(fixture)).Remove(candidate, CancellationToken.None);
        Assert.AreEqual(ReclaimItemStatus.Failed, kept.Status);
        Assert.IsEmpty(runtime.Commands, "nothing runs when it's kept");
    }

    [TestMethod]
    public void AnOllamaServingTheSameModelFromAnotherFolderIsntAskedToRemoveIt()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(2 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3", "0.6b", weights, [weights]);
        ReclaimCandidate candidate = Candidate(LocalModelTools.Ollama, ollama.Store, ollama.ManifestPath("qwen3", "0.6b"), "qwen3:0.6b");

        // A pulled manifest is the same bytes in every folder that pulled it, so the old folder's digests match.
        string old = fixture.Dir("old-ollama");
        foreach (string file in Directory.EnumerateFiles(ollama.Store, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(old, Path.GetRelativePath(ollama.Store, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        var runtime = new FakeRuntime { Serving = old };
        StringAssert.Contains(new LocalModelRemoval(runtime, () => ollama.Locations(fixture)).WhyKept(candidate), "another models folder");
        Assert.AreEqual(ReclaimItemStatus.Failed, new LocalModelRemoval(runtime, () => ollama.Locations(fixture)).Remove(candidate, CancellationToken.None).Status);
        Assert.IsEmpty(runtime.Commands);

        // Another PC's Ollama names its own files, which can have the same path as this one's.
        LocalModelLocations remote = ollama.Locations(fixture) with { OllamaHost = new Uri("http://192.168.1.20:11434/") };
        StringAssert.Contains(new LocalModelRemoval(new FakeRuntime { Serving = ollama.Store }, () => remote).WhyKept(candidate), "another PC");
        Assert.IsNull(new LocalModelRemoval(new FakeRuntime { Serving = ollama.Store }, () => ollama.Locations(fixture)).WhyKept(candidate));
    }

    [TestMethod]
    public async Task AnOllamaFolderThatCantBeReadKeepsTheModelWithoutStoppingTheRun()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(2 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "qwen3", "0.6b", weights, [weights]);
        ReclaimCandidate candidate = Candidate(LocalModelTools.Ollama, ollama.Store, ollama.ManifestPath("qwen3", "0.6b"), "qwen3:0.6b");
        var removal = new LocalModelRemoval(new FakeRuntime { Serving = ollama.Store }, () => ollama.Locations(fixture));
        string library = Path.Combine(ollama.Store, "manifests-v2", "ollama.com", "library");

        using (Deny(library))
        {
            StringAssert.Contains(removal.WhyKept(candidate), "access was denied");
            ReclaimOutcome outcome = await new ReclaimExecutor(_ => null, removal).ExecuteAsync([candidate], null, CancellationToken.None);
            Assert.AreEqual(ReclaimItemStatus.Failed, outcome.Items.Single().Status);
        }
    }

    [TestMethod]
    public async Task AnOllamaBlobWithAnotherHardLinkIsSharedNotFreed()
    {
        using var fixture = new ReclaimFixture();
        var ollama = new OllamaFixture(fixture);
        string weights = ollama.Blob(3 * MiB);
        ollama.Manifest("manifests-v2", "ollama.com", "library", "linked", "latest", ollama.Blob(400), [weights]);
        Assert.IsTrue(CreateHardLink(Path.Combine(fixture.Dir("lms"), "linked.gguf"), OllamaStore.BlobPath(ollama.Store, weights), IntPtr.Zero));

        Assert.AreEqual(400, (await Scan(ollama.Locations(fixture))).Single().SizeBytes, "ollama rm doesn't free a blob LM Studio's folder still has");
    }

    [TestMethod]
    public void OllamaHostIsReadTheWayOllamaReadsIt()
    {
        Assert.AreEqual("http://127.0.0.1:11434/", OllamaApi.HostFrom(null).ToString());
        Assert.AreEqual("http://127.0.0.1:11434/", OllamaApi.HostFrom("0.0.0.0").ToString());
        Assert.AreEqual("http://127.0.0.1:8080/", OllamaApi.HostFrom("0.0.0.0:8080").ToString());
        Assert.AreEqual("http://box:11434/", OllamaApi.HostFrom("box").ToString());
        Uri https = OllamaApi.HostFrom("https://ollama.example");
        Assert.AreEqual(("https", "ollama.example", 443), (https.Scheme, https.Host, https.Port));
        Assert.AreEqual("http://[::1]:11434/", OllamaApi.HostFrom("[::1]:11434").ToString());
    }

    // ---- Hugging Face

    [TestMethod]
    public async Task HubReposAreModelsAndOnlyModels()
    {
        using var fixture = new ReclaimFixture();
        fixture.File(@"hub\models--org--small\blobs\aaa", 3 * MiB);
        fixture.File(@"hub\models--org--small\snapshots\rev1\model.safetensors", 2 * MiB);
        fixture.Dir(@"hub\models--org--small\snapshots\rev2");
        fixture.File(@"hub\models--org--small\refs\main", 40);
        fixture.File(@"hub\models--gpt2\blobs\bbb", 2 * MiB);
        fixture.File(@"hub\datasets--org--data\blobs\ccc", 5 * MiB);
        fixture.Dir(@"hub\.locks\models--org--small");

        IReadOnlyList<ReclaimCandidate> found = await Scan(Locations(fixture, hub: Path.Combine(fixture.Root, "hub")));

        Assert.HasCount(2, found);
        ReclaimCandidate small = found.Single(c => c.DisplayName == "org/small");
        Assert.AreEqual(5 * MiB + 40, small.SizeBytes);
        StringAssert.Contains(small.Detail, "2 revisions");
        Assert.AreEqual("gpt2", found.Single(c => c.SizeBytes == 2 * MiB).DisplayName);
    }

    [TestMethod]
    public async Task AStoredFileCountsOnlyForTheRepoThatAloneUsesItAndGoesWithIt()
    {
        using var fixture = new ReclaimFixture();
        string hub = fixture.Dir("hub");
        // Python writes the marker in text mode, so on Windows it's "1\r\n" (huggingface_hub 2.4 on Windows 11, observed).
        fixture.FileWithContent(@"hub\blobs\.huggingface-shared-blobs", "1\r\n"u8.ToArray());
        string own = Stored(fixture, 'a', 4 * MiB);
        string shared = Stored(fixture, 'b', 2 * MiB);
        if (!Link(fixture, "models--org--one", "e1", own) || !Link(fixture, "models--org--one", "e2", shared) || !Link(fixture, "models--org--two", "e3", shared))
        {
            Assert.Inconclusive("Symbolic links need Developer Mode or administrator rights here.");
        }

        Refs(own, "models--org--one/blobs/e1");
        Refs(shared, "models--org--one/blobs/e2", "models--org--two/blobs/e3");

        IReadOnlyList<ReclaimCandidate> found = await Scan(Locations(fixture, hub: hub));
        ReclaimCandidate one = found.Single(c => c.DisplayName == "org/one");
        Assert.AreEqual(4 * MiB + 40, one.SizeBytes, "its own stored file and refs; the one org/two uses too isn't freed by removing org/one");
        StringAssert.Contains(one.Detail, "shared with another model");

        ReclaimItemOutcome outcome = new LocalModelRemoval(new FakeRuntime(), () => Locations(fixture, hub: hub)).Remove(one, CancellationToken.None);

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Status, outcome.Message);
        Assert.AreEqual(4 * MiB + 40, outcome.Bytes);
        Assert.IsFalse(Directory.Exists(one.Path));
        Assert.IsFalse(File.Exists(own), "the file only it used goes");
        Assert.IsFalse(File.Exists(own + ".refs"));
        Assert.IsTrue(File.Exists(shared), "the file org/two still uses stays");
        Assert.AreEqual("models--org--two/blobs/e3\n", File.ReadAllText(shared + ".refs"), "and its list names only org/two now");
        Assert.IsFalse(File.Exists(own + ".lock") || File.Exists(shared + ".lock"), "its locks are let go of and deleted, as filelock does");
    }

    [TestMethod]
    public void AHubRepoWithAFileOpenIsKeptWhole()
    {
        using var fixture = new ReclaimFixture();
        string weights = fixture.File(@"hub\models--org--busy\blobs\aaa", 2 * MiB);
        fixture.File(@"hub\models--org--busy\refs\main", 40);
        string repo = Path.Combine(fixture.Root, "hub", "models--org--busy");
        ReclaimCandidate candidate = Candidate(LocalModelTools.HuggingFace, Path.Combine(fixture.Root, "hub"), repo, "org/busy");
        var removal = new LocalModelRemoval(new FakeRuntime(), () => Locations(fixture));

        using (new FileStream(weights, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            StringAssert.Contains(removal.WhyKept(candidate), "in use");
            Assert.AreEqual(ReclaimItemStatus.Failed, removal.Remove(candidate, CancellationToken.None).Status);
        }

        Assert.IsTrue(File.Exists(weights) && File.Exists(Path.Combine(repo, "refs", "main")), "nothing in it was touched");
        Assert.AreEqual(ReclaimItemStatus.Deleted, removal.Remove(candidate, CancellationToken.None).Status);
        Assert.IsFalse(Directory.Exists(repo));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "hub")), "no staged leftovers");
    }

    // ---- LM Studio, Foundry Local, model files

    [TestMethod]
    public async Task LmStudioModelsAreFoldersWithWeightsAndGoWithTheirEmptyPublisher()
    {
        using var fixture = new ReclaimFixture();
        fixture.File(@"lms\lmstudio-community\Qwen3-0.6B-GGUF\Qwen3-0.6B-Q8_0.gguf", 3 * MiB);
        fixture.File(@"lms\lmstudio-community\notes\readme.md", 2 * MiB);
        string models = Path.Combine(fixture.Root, "lms");

        IReadOnlyList<ReclaimCandidate> found = await Scan(Locations(fixture, lmStudio: models));
        ReclaimCandidate model = found.Single();
        Assert.AreEqual("lmstudio-community/Qwen3-0.6B-GGUF", model.DisplayName);
        StringAssert.Contains(model.Detail, "Qwen3-0.6B-Q8_0.gguf");

        Directory.Delete(Path.Combine(models, "lmstudio-community", "notes"), recursive: true);
        Assert.AreEqual(ReclaimItemStatus.Deleted, new LocalModelRemoval(new FakeRuntime(), () => Locations(fixture)).Remove(model, CancellationToken.None).Status);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(models));
    }

    [TestMethod]
    public async Task FoundryModelsAreNamedByTheirIdAndRemovedByFoundry()
    {
        using var fixture = new ReclaimFixture();
        string cache = fixture.Dir("foundry");
        fixture.File(@"foundry\Microsoft\qwen3-0.6b-generic-cpu-4\v4\model.onnx", 3 * MiB);
        fixture.FileWithContent(@"foundry\Microsoft\qwen3-0.6b-generic-cpu-4\v4\inference_model.json", """{ "Name": "qwen3-0.6b-generic-cpu:4", "PromptTemplate": null }"""u8.ToArray());
        fixture.File(@"foundry\Microsoft\downloading\v1\model.onnx", 3 * MiB);
        fixture.File(@"foundry\Microsoft\downloading\v1\inference_model.json", 10);
        fixture.File(@"foundry\Microsoft\downloading\download.tmp", 1);
        fixture.File(@"foundry\foundry.modelinfo.json", 100);

        IReadOnlyList<ReclaimCandidate> found = await Scan(Locations(fixture, foundry: cache));
        ReclaimCandidate model = found.Single();
        Assert.AreEqual("qwen3-0.6b-generic-cpu:4", model.DisplayName);

        var removal = new LocalModelRemoval(new FakeRuntime { FoundryLoadedIds = ["qwen3-0.6b-generic-cpu:4"] }, () => Locations(fixture));
        StringAssert.Contains(removal.WhyKept(model), "in use");

        var runtime = new FakeRuntime { OnRun = (_, _) => Directory.Delete(Path.Combine(model.Path, "v4"), recursive: true) };
        ReclaimItemOutcome removed = new LocalModelRemoval(runtime, () => Locations(fixture)).Remove(model, CancellationToken.None);
        Assert.AreEqual(ReclaimItemStatus.Deleted, removed.Status, "Foundry Local 0.13 empties the model's folder and leaves it");
        CollectionAssert.AreEqual(new[] { "cache", "remove", "qwen3-0.6b-generic-cpu:4", "--force" }, runtime.Commands.Single().ToArray());

        fixture.File(@"foundry\Microsoft\qwen3-0.6b-generic-cpu-4\v4\model.onnx", 3 * MiB);
        var refusing = new FakeRuntime();
        Assert.AreEqual(ReclaimItemStatus.Failed, new LocalModelRemoval(refusing, () => Locations(fixture)).Remove(model, CancellationToken.None).Status,
            "files still there means it wasn't removed, whatever it printed");
    }

    [TestMethod]
    public void AModelALoaderHasMappedIsKeptWhole()
    {
        using var fixture = new ReclaimFixture();
        string weights = fixture.File(@"lms\pub\Model-GGUF\model.gguf", 3 * MiB);
        string projector = fixture.File(@"lms\pub\Model-GGUF\mmproj.gguf", 2 * MiB);
        string models = Path.Combine(fixture.Root, "lms");
        string folder = Path.GetDirectoryName(weights)!;
        ReclaimCandidate candidate = Candidate(LocalModelTools.LmStudio, models, folder, "pub/Model-GGUF");
        var removal = new LocalModelRemoval(new FakeRuntime(), () => Locations(fixture, lmStudio: models));

        // llama.cpp maps the weights and closes its handles, so only the view is left.
        using (Mapped(weights))
        {
            ReclaimItemOutcome kept = removal.Remove(candidate, CancellationToken.None);
            Assert.AreEqual(ReclaimItemStatus.Failed, kept.Status);
            StringAssert.Contains(kept.Message, "model.gguf");
            Assert.IsTrue(File.Exists(weights) && File.Exists(projector), "none of its files was deleted");
        }

        Assert.AreEqual(ReclaimItemStatus.Deleted, removal.Remove(candidate, CancellationToken.None).Status);
        Assert.IsFalse(Directory.Exists(folder));
    }

    [TestMethod]
    public void ACommandWhoseChildKeepsItsOutputOpenStillReturns()
    {
        // ollama rm starts Ollama's app when the server is down, and the app inherits the command's output.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        (int exit, string output) = LocalModelRuntime.Instance.Run(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/c", "echo started & start /b ping -n 20 127.0.0.1"], TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(10), $"returned after {timer.Elapsed}");
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "started");
    }

    [TestMethod]
    public async Task AHardLinkedModelFileIsSharedNotFreed()
    {
        using var fixture = new ReclaimFixture();
        string folder = fixture.Dir("gguf");
        string model = fixture.File(@"gguf\tiny.gguf", 3 * MiB);
        string linked = fixture.File(@"gguf\linked.gguf", 2 * MiB);
        Assert.IsTrue(CreateHardLink(Path.Combine(fixture.Dir("elsewhere"), "copy.gguf"), linked, IntPtr.Zero));
        fixture.File(@"gguf\localdocs_v2.db", 4 * MiB);

        IReadOnlyList<ReclaimCandidate> found = await Scan(Locations(fixture, folders: [new ModelFolder("GPT4All", folder, "Download it again in GPT4All.")]));

        Assert.HasCount(2, found, "only the model files, not the app's other files");
        Assert.AreEqual(3 * MiB, found.Single(c => c.Path == model).SizeBytes);
        Assert.AreEqual(0, found.Single(c => c.Path == linked).SizeBytes);
    }

    [TestMethod]
    public async Task AFolderThatCantBeReadIsAGap()
    {
        using var fixture = new ReclaimFixture();
        string models = fixture.Dir("lms");
        fixture.File(@"lms\pub\model\m.gguf", 2 * MiB);
        using (Deny(models))
        {
            var context = new ReclaimScanContext([], []);
            IReadOnlyList<ReclaimCandidate> found = await new LocalModelReclaimProvider(() => Locations(fixture, lmStudio: models), new FakeRuntime())
                .ScanAsync(context, null, CancellationToken.None);

            Assert.IsEmpty(found);
            ReclaimGap gap = context.Gaps.Single();
            Assert.AreEqual(models, gap.Location);
            Assert.AreEqual("access denied", gap.Reason);
        }
    }

    // ---- The engine and the executor

    [TestMethod]
    public async Task GapsReachTheirCategorysResult()
    {
        using var fixture = new ReclaimFixture();
        var engine = new ReclaimEngine([new GapProvider(fixture.Root)]);
        ReclaimResult result = await engine.ScanAsync(new ReclaimScanContext([], []));

        Assert.AreEqual(fixture.Root, result.Categories.Single().Unread.Single().Location);
        Assert.HasCount(1, result.PartlyReadCategories);
    }

    [TestMethod]
    public async Task AnOwnedCandidateGoesToItsOwnerNeverToADelete()
    {
        using var fixture = new ReclaimFixture();
        string manifest = fixture.File(@"m\manifests-v2\ollama.com\library\x\latest", 10);
        ReclaimCandidate candidate = Candidate(LocalModelTools.Ollama, Path.Combine(fixture.Root, "m"), manifest, "x:latest");
        var owners = new RecordingOwners();

        ReclaimOutcome outcome = await new ReclaimExecutor(_ => null, owners).ExecuteAsync([candidate], null, CancellationToken.None);

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status);
        Assert.AreSame(candidate, owners.Removed.Single());
        Assert.IsTrue(File.Exists(manifest), "the executor didn't delete the path itself");
    }

    // ---- Helpers

    private static async Task<IReadOnlyList<ReclaimCandidate>> Scan(LocalModelLocations locations) =>
        await new LocalModelReclaimProvider(() => locations, new FakeRuntime()).ScanAsync(new ReclaimScanContext([], []), null, CancellationToken.None);

    internal static LocalModelLocations Locations(
        ReclaimFixture fixture, string? ollama = null, string? hub = null, string? lmStudio = null, string? foundry = null, IReadOnlyList<ModelFolder>? folders = null) =>
        new(ollama ?? Path.Combine(fixture.Root, "no-ollama"), new Uri("http://127.0.0.1:11434/"),
            [new HubCache("Hugging Face", hub ?? Path.Combine(fixture.Root, "no-hub"))],
            lmStudio ?? Path.Combine(fixture.Root, "no-lms"), foundry ?? Path.Combine(fixture.Root, "no-foundry"), folders ?? []);

    private static ReclaimCandidate Candidate(string tool, string store, string path, params string[] names) =>
        new(LocalModelReclaimProvider.ReclaimCategory.Id, path, names[0], 2 * MiB, ReclaimRisk.Check, "why", "how",
            supportsRecycleBin: false, owner: new ReclaimOwner(tool, tool, store, names));

    private static string Stored(ReclaimFixture fixture, char hash, long bytes) =>
        fixture.File($@"hub\blobs\{hash}{hash}\{new string(hash, 64)}", bytes);

    private static void Refs(string stored, params string[] users) =>
        File.WriteAllText(stored + ".refs", string.Concat(users.Select(u => u + "\n")));

    private static bool Link(ReclaimFixture fixture, string repo, string etag, string stored)
    {
        string blobs = fixture.Dir("hub", repo, "blobs");
        try
        {
            File.CreateSymbolicLink(Path.Combine(blobs, etag), Path.GetRelativePath(blobs, stored));
            fixture.File($@"hub\{repo}\refs\main", 40);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>Denies listing <paramref name="folder"/> to this user until disposed.</summary>
    private static IDisposable Deny(string folder)
    {
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(folder);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        return new Undo(() =>
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        });
    }

    /// <summary>A read-only view of <paramref name="path"/> with its file and mapping handles closed, as llama.cpp leaves a loaded model.</summary>
    private static IDisposable Mapped(string path)
    {
        using var file = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        return file.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>An Ollama models folder laid out as Ollama 0.40 lays it out.</summary>
    private sealed class OllamaFixture(ReclaimFixture fixture)
    {
        private int _next;

        public string Store { get; } = fixture.Dir("ollama");

        public LocalModelLocations Locations(ReclaimFixture f) => LocalModelTests.Locations(f, ollama: Store);

        public string Blob(long bytes)
        {
            string digest = "sha256:" + (++_next).ToString("x64");
            fixture.File(Path.GetRelativePath(fixture.Root, OllamaStore.BlobPath(Store, digest)), bytes);
            return digest;
        }

        public string ManifestPath(string model, string tag) => Path.Combine(Store, "manifests-v2", "ollama.com", "library", model, tag);

        public void Manifest(string tree, string host, string space, string model, string tag, string config, string[] layers, int later = 0)
        {
            string items = string.Join(",", layers.Select(d => $$"""{"mediaType":"application/vnd.ollama.image.model","digest":"{{d}}","size":1}"""));
            string path = Path.Combine(Store, tree, host, space, model, tag);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $$"""{"schemaVersion":2,"config":{"digest":"{{config}}","size":1},"layers":[{{items}}]}""");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(later));
        }

        /// <summary>What ollama rm does: the names' manifests, then every blob no manifest references.</summary>
        public void Remove(IEnumerable<string> names)
        {
            foreach (OllamaStore.Entry entry in OllamaStore.ReadManifests(Store, _ => { }, CancellationToken.None).Where(e => names.Contains(e.Name)))
            {
                File.Delete(entry.Manifest);
            }

            HashSet<string> used = [.. OllamaStore.ReadManifests(Store, _ => { }, CancellationToken.None).SelectMany(e => e.Digests).Select(d => d.Replace(':', '-'))];
            foreach (string blob in Directory.EnumerateFiles(Path.Combine(Store, "blobs")).Where(b => !used.Contains(Path.GetFileName(b))))
            {
                File.Delete(blob);
            }
        }
    }

    private sealed class FakeRuntime : ILocalModelRuntime
    {
        public IReadOnlyList<string>? Loaded { get; init; } = [];

        /// <summary>The models folder the running Ollama serves: its names with their manifests' digests.</summary>
        public string? Serving { get; init; }

        /// <summary>More names the running Ollama lists, from a folder that isn't scanned.</summary>
        public IReadOnlyList<string>? Listed { get; init; }

        public bool OllamaRunning { get; init; } = true;

        public bool OllamaAnswers { get; init; } = true;

        public IReadOnlyList<string>? FoundryLoadedIds { get; init; } = [];

        public IReadOnlyList<string> Tools { get; init; } = [LocalModelTools.Ollama, LocalModelTools.FoundryLocal];

        public Action<string, IReadOnlyList<string>>? OnRun { get; init; }

        public List<IReadOnlyList<string>> Commands { get; } = [];

        public List<IReadOnlyDictionary<string, string>?> Environments { get; } = [];

        public IReadOnlyList<string>? OllamaLoaded(Uri host) => Loaded;

        public OllamaServed? OllamaListed(Uri host)
        {
            if (!OllamaAnswers)
            {
                return null;
            }

            if (!OllamaRunning)
            {
                return OllamaServed.NotRunning;
            }

            var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (OllamaStore.Entry entry in Serving is null ? [] : OllamaStore.ReadManifests(Serving, _ => { }, CancellationToken.None))
            {
                digests[entry.Name] = OllamaStore.ManifestDigest(entry.Manifest);
            }

            foreach (string name in Listed ?? [])
            {
                digests.TryAdd(name, new string('0', 64));
            }

            return new OllamaServed(true, digests);
        }

        public string? OllamaWeightsPath(Uri host, string name)
        {
            if (Serving is null)
            {
                return null;
            }

            OllamaStore.Entry? entry = OllamaStore.ReadManifests(Serving, _ => { }, CancellationToken.None)
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return entry?.Digests.Select(d => OllamaStore.BlobPath(Serving, d)).FirstOrDefault(File.Exists);
        }

        public IReadOnlyList<string>? FoundryLoaded() => FoundryLoadedIds;

        public string? FoundryCacheLocation() => null;

        public string? FindTool(string tool) => Tools.Contains(tool) ? $@"C:\fake\{tool}.exe" : null;

        public (int ExitCode, string Output) Run(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(arguments);
            Environments.Add(environment);
            OnRun?.Invoke(executable, arguments);
            return (0, "done");
        }
    }

    private sealed class GapProvider(string where) : IReclaimProvider
    {
        public ReclaimCategory Category { get; } = new("gappy", "Gappy", "A category that couldn't read one place.", "\uE8B7", 0);

        public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(ReclaimScanContext context, IProgress<ReclaimScanProgress>? progress, CancellationToken cancellationToken)
        {
            context.ReportGap(new ReclaimGap(Category.Id, where, "access denied"));
            context.ReportGap(new ReclaimGap("someone-else", where, "access denied"));
            return Task.FromResult<IReadOnlyList<ReclaimCandidate>>([]);
        }
    }

    private sealed class RecordingOwners : IReclaimOwnerRemoval
    {
        public List<ReclaimCandidate> Removed { get; } = [];

        public string? WhyKept(ReclaimCandidate candidate) => null;

        public string? Describe(ReclaimCandidate candidate) => "test";

        public ReclaimItemOutcome Remove(ReclaimCandidate candidate, CancellationToken cancellationToken)
        {
            Removed.Add(candidate);
            return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Deleted, candidate.SizeBytes, "removed by its owner");
        }
    }
}
