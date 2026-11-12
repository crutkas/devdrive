using DevDriveCore;
using DevDriveCore.Platform;

namespace DevDriveReclaim.Providers.Models;

/// <summary>
/// AI models the model managers on this PC downloaded: Ollama, the Hugging Face cache (and llama.cpp's), LM Studio, Foundry
/// Local, and folders of standalone model files (GPT4All's, and any the host adds). A row is a model as its manager names it,
/// sized by what removing it gives back, and removed through its manager (<see cref="LocalModelRemoval"/>).
/// </summary>
/// <remarks>
/// Each manager's folder is found the way the manager finds it (<see cref="LocalModelLocations"/>), and a folder that exists but
/// can't be read is reported as a gap, never as no models. Sizes count a file once: a blob or stored file two models use is
/// neither's, a hard-linked file is shared, and a symbolic link costs nothing.
/// <para>
/// Every row is <see cref="ReclaimRisk.Check"/>. A model is usually a download, but it can be big and slow to fetch again, and
/// one someone made or imported may not exist anywhere else. Its age or size says nothing about whether it's still wanted, so
/// nothing is called idle and nothing is preselected. Not read yet: AI Toolkit for VS Code (deleting its files would leave its
/// <c>my-models.yml</c> stale). Never read: Windows' own AI models, which Windows manages.
/// </para>
/// </remarks>
public sealed class LocalModelReclaimProvider : IReclaimProvider
{
    public static readonly ReclaimCategory ReclaimCategory = new(
        "ai-models",
        "AI models",
        "Models your AI tools downloaded. Removing one means downloading it again before you use it next.",
        "\uE99A",
        order: 3);

    private const string Reason =
        "Using it again means downloading it again, which takes a while for a big model. A model you made or imported " +
        "yourself may not be anywhere else.";

    private readonly Func<LocalModelLocations> _locations;
    private readonly ILocalModelRuntime _runtime;

    public LocalModelReclaimProvider()
        : this(() => LocalModelLocations.FromEnvironment(), LocalModelRuntime.Instance)
    {
    }

    /// <param name="locations">Where the managers keep their models, read for each scan.</param>
    /// <param name="runtime">The managers' running state and command lines.</param>
    public LocalModelReclaimProvider(Func<LocalModelLocations> locations, ILocalModelRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(runtime);
        _locations = locations;
        _runtime = runtime;
    }

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return BackgroundWork.Run<IReadOnlyList<ReclaimCandidate>>(() =>
        {
            LocalModelLocations where = _locations();
            List<FoundModel> found = [];

            void Gap(string location, string reason) => context.ReportGap(new ReclaimGap(ReclaimCategory.Id, location, reason));

            void Read(string title, string folder, Func<List<FoundModel>> read)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReclaimScanProgress(ReclaimCategory.Id, $"Reading {title}", found.Count));
                try
                {
                    if (Directory.Exists(folder))
                    {
                        found.AddRange(read());
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Gap(folder, "access denied");
                }
                catch (IOException exception)
                {
                    Gap(folder, exception.Message);
                }
            }

            Read("Ollama", where.OllamaModels, () =>
            {
                int unreadable = 0;
                List<FoundModel> models = OllamaStore.Read(where.OllamaModels, _ => unreadable++, cancellationToken);
                if (unreadable > 0)
                {
                    Gap(where.OllamaModels, unreadable == 1 ? "a manifest couldn't be read" : $"{unreadable} manifests couldn't be read");
                }

                return models;
            });

            // A running Ollama that lists models this folder doesn't have keeps them somewhere else (its own OLLAMA_MODELS).
            if (_runtime.OllamaListed(where.OllamaHost) is { Running: true, Digests.Count: > 0 } served)
            {
                var here = new HashSet<string>(found.Where(m => m.Tool == LocalModelTools.Ollama).SelectMany(m => m.Names), StringComparer.OrdinalIgnoreCase);
                int elsewhere = served.Digests.Keys.Count(n => !here.Contains(n));
                if (elsewhere > 0)
                {
                    Gap(where.OllamaModels, elsewhere == 1 ? "Ollama lists a model that isn't in this folder" : $"Ollama lists {elsewhere} models that aren't in this folder");
                }
            }

            foreach (HubCache hub in where.HubCaches)
            {
                Read(hub.Title, hub.Path, () => HubCacheReader.Read(hub, Gap, cancellationToken));
            }

            Read("LM Studio", where.LmStudioModels, () => LmStudioReader.Read(where.LmStudioModels, Gap, cancellationToken));

            string foundry = where.FoundryCache ?? _runtime.FoundryCacheLocation() ?? LocalModelLocations.DefaultFoundryCache;
            Read("Foundry Local", foundry, () => FoundryReader.Read(foundry, Gap, cancellationToken));

            foreach (ModelFolder folder in where.ModelFolders)
            {
                Read(folder.Title, folder.Path, () => ModelFolderReader.Read(folder, cancellationToken));
            }

            return [.. found.Select(ToCandidate)];
        }, cancellationToken);
    }

    private static ReclaimCandidate ToCandidate(FoundModel model) => new(
        ReclaimCategory.Id,
        model.Path,
        model.Names[0],
        model.Bytes,
        ReclaimRisk.Check,
        Reason,
        model.RecoveryHint,
        detail: Detail(model),
        supportsRecycleBin: false,
        owner: new ReclaimOwner(model.Tool, model.Title, model.Store, model.Names));

    /// <summary>
    /// A model candidate made the way a scan makes one, for a host's sample data: the same risk, reason, recovery, detail and
    /// owner a real model of that manager gets.
    /// </summary>
    public static ReclaimCandidate CandidateFor(
        string tool, string title, string store, string path, IReadOnlyList<string> names, long bytes,
        long sharedBytes = 0, string? extra = null, string? recoveryHint = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(names.Count);
        return ToCandidate(new FoundModel(tool, title, store, path, names, bytes, sharedBytes, extra, recoveryHint ?? Recovery(tool, title, names[0])));
    }

    /// <summary>How a model of <paramref name="tool"/> comes back after it's removed.</summary>
    internal static string Recovery(string tool, string title, string name) => tool switch
    {
        LocalModelTools.Ollama => $"Run ollama pull {name} to download it again. A model you made with ollama create comes back only from its Modelfile.",
        LocalModelTools.HuggingFace when title == "llama.cpp" => $"llama.cpp downloads it again the next time you run it with -hf {name}.",
        LocalModelTools.HuggingFace => $"It downloads again the next time something loads {name} from Hugging Face.",
        LocalModelTools.LmStudio => $"Download {name} again in LM Studio.",
        LocalModelTools.FoundryLocal => $"Run foundry model download {name} to download it again.",
        _ => "Download it again from where you got it.",
    };

    /// <summary>"Ollama · also qwen3:latest · 1.2 GB shared with another model": who owns it, then what the row's name doesn't say.</summary>
    private static string Detail(FoundModel model)
    {
        List<string> parts = [model.Title];
        if (model.Names.Count > 1)
        {
            parts.Add("also " + string.Join(", ", model.Names.Skip(1)));
        }

        if (model.Extra is { } extra)
        {
            parts.Add(extra);
        }

        if (model.SharedBytes >= ModelFiles.LargeFileBytes)
        {
            parts.Add($"{ByteSizeFormatter.Format((ulong)model.SharedBytes)} shared with another model");
        }

        return string.Join(" · ", parts);
    }
}
