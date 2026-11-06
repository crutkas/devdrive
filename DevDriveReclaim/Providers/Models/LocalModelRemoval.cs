namespace DevDriveReclaim.Providers.Models;

/// <summary>
/// Removes AI models through their managers: <c>ollama rm</c> for Ollama, <c>foundry cache remove</c> for Foundry Local, and
/// for the managers whose folders are the whole record (the Hugging Face cache, LM Studio, a folder of model files) the same
/// files their own delete removes. Nothing is removed while it's in use or after it changed since the scan.
/// </summary>
/// <remarks>
/// In use means: Ollama has it loaded (<c>/api/ps</c>; Ollama's runner doesn't hold the files, so they can't tell), Foundry Local
/// has it loaded, or another program has one of its large files open or mapped. A folder is renamed before it's deleted:
/// Windows refuses that while a program has a file in it open, so a model in use is never half-deleted. Removal is permanent;
/// the managers have no undo.
/// </remarks>
public sealed class LocalModelRemoval : IReclaimOwnerRemoval
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly ILocalModelRuntime _runtime;
    private readonly Func<LocalModelLocations> _locations;

    /// <summary>The real one: this PC's managers.</summary>
    public static LocalModelRemoval Default { get; } = new(LocalModelRuntime.Instance, () => LocalModelLocations.FromEnvironment());

    /// <param name="runtime">The managers' running state and command lines.</param>
    /// <param name="locations">Where they keep models (Ollama's address comes from here).</param>
    public LocalModelRemoval(ILocalModelRuntime runtime, Func<LocalModelLocations> locations)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(locations);
        _runtime = runtime;
        _locations = locations;
    }

    public string? WhyKept(ReclaimCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Owner is not { } owner)
        {
            return null;
        }

        return owner.Tool switch
        {
            LocalModelTools.Ollama => OllamaWhyKept(owner),
            LocalModelTools.FoundryLocal => FoundryWhyKept(candidate, owner),
            LocalModelTools.HuggingFace => Held(HubFiles(owner.Store, candidate.Path)),
            LocalModelTools.LmStudio or LocalModelTools.ModelFiles => Held(FilesOf(candidate.Path)),
            _ => $"{owner.Title} isn't a model manager this can remove from",
        };
    }

    public string? Describe(ReclaimCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Owner switch
        {
            { Tool: LocalModelTools.Ollama } owner => "ollama rm " + string.Join(" ", owner.Names),
            { Tool: LocalModelTools.FoundryLocal } owner => $"foundry cache remove {owner.Names[0]} --force",
            _ => null,
        };
    }

    public ReclaimItemOutcome Remove(ReclaimCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (WhyKept(candidate) is { } reason)
        {
            return Kept(candidate, reason);
        }

        try
        {
            return candidate.Owner!.Tool switch
            {
                LocalModelTools.Ollama => RemoveFromOllama(candidate, cancellationToken),
                LocalModelTools.FoundryLocal => RemoveFromFoundry(candidate, cancellationToken),
                LocalModelTools.HuggingFace => RemoveFromHub(candidate),
                LocalModelTools.LmStudio => RemoveFolder(candidate, removeEmptyParent: true),
                _ => RemoveFile(candidate),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Failed, 0, FileSystemRemovalError.Explain(exception));
        }
    }

    // ---- Ollama

    private string? OllamaWhyKept(ReclaimOwner owner)
    {
        if (_runtime.FindTool(LocalModelTools.Ollama) is null)
        {
            return "Ollama isn't installed, so it can't remove it";
        }

        IReadOnlyList<string>? now = OllamaStore.NamesWith(owner.Store, owner.Names[0]);
        if (now is null)
        {
            return "Ollama doesn't have it any more";
        }

        if (!now.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(owner.Names))
        {
            return "it changed since the scan; scan again";
        }

        IReadOnlyList<string>? loaded = _runtime.OllamaLoaded(_locations().OllamaHost);
        if (loaded is null)
        {
            return "Ollama didn't say whether it's in use";
        }

        return loaded.FirstOrDefault(n => owner.Names.Contains(n, StringComparer.OrdinalIgnoreCase)) is { } running
            ? $"in use: Ollama has {running} loaded"
            : null;
    }

    private ReclaimItemOutcome RemoveFromOllama(ReclaimCandidate candidate, CancellationToken cancellationToken)
    {
        ReclaimOwner owner = candidate.Owner!;
        (int exit, string output) = _runtime.Run(_runtime.FindTool(LocalModelTools.Ollama)!, ["rm", .. owner.Names], CommandTimeout, cancellationToken);

        // What's left decides it, not the exit code alone: ollama rm stops at the first name it can't remove.
        List<string> left = [.. OllamaStore.ReadManifests(owner.Store, _ => { }, CancellationToken.None)
            .Select(e => e.Name)
            .Where(n => owner.Names.Contains(n, StringComparer.OrdinalIgnoreCase))];
        if (left.Count == 0)
        {
            return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Deleted, candidate.SizeBytes, "removed with ollama rm");
        }

        string why = LastLine(output) ?? $"ollama rm exited with {exit}";
        return left.Count == owner.Names.Count
            ? new ReclaimItemOutcome(candidate, ReclaimItemStatus.Failed, 0, why)
            : new ReclaimItemOutcome(candidate, ReclaimItemStatus.Failed, 0, $"{string.Join(", ", left)} still there: {why}");
    }

    // ---- Foundry Local

    private string? FoundryWhyKept(ReclaimCandidate candidate, ReclaimOwner owner)
    {
        if (_runtime.FindTool(LocalModelTools.FoundryLocal) is null)
        {
            return "Foundry Local isn't installed, so it can't remove it";
        }

        IReadOnlyList<string>? loaded = _runtime.FoundryLoaded();
        if (loaded is null)
        {
            return "Foundry Local didn't say whether it's in use";
        }

        return loaded.Contains(owner.Names[0], StringComparer.OrdinalIgnoreCase)
            ? $"in use: Foundry Local has {owner.Names[0]} loaded"
            : Held(FilesOf(candidate.Path));
    }

    private ReclaimItemOutcome RemoveFromFoundry(ReclaimCandidate candidate, CancellationToken cancellationToken)
    {
        string id = candidate.Owner!.Names[0];
        (int exit, string output) = _runtime.Run(_runtime.FindTool(LocalModelTools.FoundryLocal)!, ["cache", "remove", id, "--force"], CommandTimeout, cancellationToken);
        return !Directory.Exists(candidate.Path)
            ? new ReclaimItemOutcome(candidate, ReclaimItemStatus.Deleted, candidate.SizeBytes, "removed with foundry cache remove")
            : new ReclaimItemOutcome(candidate, ReclaimItemStatus.Failed, 0, LastLine(output) ?? $"foundry cache remove exited with {exit}");
    }

    // ---- Folders and files

    /// <summary>
    /// A Hugging Face repo, as <c>hf cache rm</c> removes one whose every revision goes: its folder, then each stored file only it
    /// used (<see cref="SharedBlobStore.Sweep"/>).
    /// </summary>
    private static ReclaimItemOutcome RemoveFromHub(ReclaimCandidate candidate)
    {
        ReclaimOwner owner = candidate.Owner!;
        ModelFootprint own = ModelFiles.Measure(candidate.Path, CancellationToken.None);
        IReadOnlyList<string> stored = SharedBlobStore.IsMarked(owner.Store) ? HubCacheReader.StoredTargets(owner.Store, candidate.Path, own.Links) : [];

        if (Stage(candidate) is not { } staged)
        {
            return Kept(candidate, InUseFolder);
        }

        ModelFiles.DeleteTree(staged);
        long freed = own.Bytes + stored.Sum(file => SharedBlobStore.Sweep(owner.Store, file));
        return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Deleted, freed, $"deleted from the {owner.Title} cache");
    }

    private static ReclaimItemOutcome RemoveFolder(ReclaimCandidate candidate, bool removeEmptyParent)
    {
        if (Stage(candidate) is not { } staged)
        {
            return Kept(candidate, InUseFolder);
        }

        ModelFiles.DeleteTree(staged);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate.Path));
        if (removeEmptyParent && parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }

        return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Deleted, candidate.SizeBytes, "deleted permanently");
    }

    private static ReclaimItemOutcome RemoveFile(ReclaimCandidate candidate)
    {
        var file = new FileInfo(candidate.Path);
        if ((file.Attributes & FileAttributes.ReadOnly) != 0)
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }

        file.Delete();
        return new ReclaimItemOutcome(candidate, ReclaimItemStatus.Deleted, candidate.SizeBytes, "deleted permanently");
    }

    private const string InUseFolder = "in use: a program has one of its files open";

    /// <summary>The folder renamed aside for deletion, or null when Windows refused because something in it is open.</summary>
    private static string? Stage(ReclaimCandidate candidate)
    {
        try
        {
            return ModelFiles.StageForDeletion(candidate.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- In use

    private static IEnumerable<string> FilesOf(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A repo's large files and the stored files it uses, which a program loading the model opens.</summary>
    private static IEnumerable<string> HubFiles(string hub, string repo)
    {
        try
        {
            ModelFootprint footprint = ModelFiles.Measure(repo, CancellationToken.None);
            return SharedBlobStore.IsMarked(hub) ? [.. footprint.Files, .. HubCacheReader.StoredTargets(hub, repo, footprint.Links)] : footprint.Files;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Held(IEnumerable<string> files) =>
        ModelFiles.FirstHeld(files) is { } held ? $"in use: a program has {Path.GetFileName(held)} open" : null;

    private static ReclaimItemOutcome Kept(ReclaimCandidate candidate, string reason) =>
        new(candidate, ReclaimItemStatus.Failed, 0, $"kept — {reason}");

    private static string? LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
