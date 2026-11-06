namespace DevDriveReclaim.Providers.Models;

/// <summary>The model managers the AI models category reads, by the id a <see cref="ReclaimOwner"/> carries.</summary>
public static class LocalModelTools
{
    /// <summary>Ollama: manifests name models, content-addressed blobs hold them, and models share blobs.</summary>
    public const string Ollama = "ollama";

    /// <summary>The Hugging Face hub cache (and llama.cpp's, which uses the same layout): a folder per repo.</summary>
    public const string HuggingFace = "huggingface";

    /// <summary>LM Studio: a folder per model under its models folder.</summary>
    public const string LmStudio = "lmstudio";

    /// <summary>Foundry Local: a folder per model in its cache, removed with <c>foundry cache remove</c>.</summary>
    public const string FoundryLocal = "foundry";

    /// <summary>Standalone model files in a folder an app downloads them to (GPT4All, a setup script's folder).</summary>
    public const string ModelFiles = "model-files";

    /// <summary>A manager's name as people know it: "Ollama", "Hugging Face", "LM Studio", "Foundry Local".</summary>
    public static string TitleOf(string tool) => tool switch
    {
        Ollama => "Ollama",
        HuggingFace => "Hugging Face",
        LmStudio => "LM Studio",
        FoundryLocal => "Foundry Local",
        _ => tool,
    };
}

/// <summary>A folder of standalone model files (<c>*.gguf</c>) that belongs to one app.</summary>
/// <param name="Title">The app, e.g. "GPT4All".</param>
/// <param name="Path">The folder.</param>
/// <param name="RecoveryHint">How to get a removed file back, e.g. "Download it again in GPT4All."</param>
public sealed record ModelFolder(string Title, string Path, string RecoveryHint);

/// <summary>A Hugging Face-layout cache and who fills it.</summary>
/// <param name="Title">"Hugging Face", or "llama.cpp" for its own cache (<c>LLAMA_CACHE</c>).</param>
/// <param name="Path">The hub folder, the one that holds <c>models--org--name</c> folders.</param>
public sealed record HubCache(string Title, string Path);

/// <summary>Where each model manager keeps its models on this PC, resolved the way the manager itself resolves it.</summary>
/// <param name="OllamaModels"><c>OLLAMA_MODELS</c>, else <c>%USERPROFILE%\.ollama\models</c>.</param>
/// <param name="OllamaHost">Where Ollama's server listens: <c>OLLAMA_HOST</c>, else <c>http://127.0.0.1:11434</c>.</param>
/// <param name="HubCaches">The Hugging Face hub cache, and llama.cpp's when <c>LLAMA_CACHE</c> moves it elsewhere.</param>
/// <param name="LmStudioModels">LM Studio's models folder.</param>
/// <param name="FoundryCache">Foundry Local's model cache, or null to ask <c>foundry cache location</c> at scan time.</param>
/// <param name="ModelFolders">Folders of standalone model files.</param>
public sealed record LocalModelLocations(
    string OllamaModels,
    Uri OllamaHost,
    IReadOnlyList<HubCache> HubCaches,
    string LmStudioModels,
    string? FoundryCache,
    IReadOnlyList<ModelFolder> ModelFolders)
{
    /// <summary>
    /// This PC's locations. Each environment variable is read from this process, then the user's and the machine's
    /// settings, so one set after this app started still counts.
    /// </summary>
    /// <param name="extraFolders">More folders of standalone model files, such as a setup script's.</param>
    public static LocalModelLocations FromEnvironment(IEnumerable<ModelFolder>? extraFolders = null)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string ollama = Variable("OLLAMA_MODELS") ?? Path.Combine(profile, ".ollama", "models");

        // huggingface_hub's constants.py: HF_HUB_CACHE, then HUGGINGFACE_HUB_CACHE, then HF_HOME\hub, then
        // XDG_CACHE_HOME\huggingface\hub, then ~\.cache\huggingface\hub.
        string hfHome = Variable("HF_HOME")
            ?? (Variable("XDG_CACHE_HOME") is { } xdg ? Path.Combine(xdg, "huggingface") : Path.Combine(profile, ".cache", "huggingface"));
        string hub = Variable("HF_HUB_CACHE") ?? Variable("HUGGINGFACE_HUB_CACHE") ?? Path.Combine(hfHome, "hub");
        List<HubCache> hubs = [new(LocalModelTools.TitleOf(LocalModelTools.HuggingFace), hub)];

        // llama.cpp's -hf downloads use the same layout, in LLAMA_CACHE when it's set and in the hub cache otherwise.
        if (Variable("LLAMA_CACHE") is { } llama && !SameFolder(llama, hub))
        {
            hubs.Add(new HubCache("llama.cpp", llama));
        }

        List<ModelFolder> folders =
        [
            new("GPT4All", GptForAllFolder(localAppData), "Download it again in GPT4All."),
            new("GPT4All", Path.Combine(profile, ".cache", "gpt4all"), "Download it again with GPT4All."),
        ];
        if (extraFolders is not null)
        {
            folders.AddRange(extraFolders);
        }

        return new LocalModelLocations(
            ollama,
            OllamaApi.HostFrom(Variable("OLLAMA_HOST")),
            hubs,
            LmStudioModelsFolder(profile),
            null,
            [.. folders.DistinctBy(f => Path.TrimEndingDirectorySeparator(f.Path), StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Foundry Local's default cache, <c>%USERPROFILE%\.foundry\cache\models</c>.</summary>
    public static string DefaultFoundryCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "cache", "models");

    /// <summary>
    /// LM Studio's home (lmstudio-js findLMStudioHome): the folder <c>~\.lmstudio-home-pointer</c> names, else
    /// <c>~\.cache\lm-studio</c> when it exists, else <c>~\.lmstudio</c>. Models are in its <c>models</c> folder, unless its
    /// settings move them (<c>downloadsFolder</c>).
    /// </summary>
    internal static string LmStudioModelsFolder(string profile)
    {
        string home = Path.Combine(profile, ".lmstudio");
        try
        {
            string pointer = Path.Combine(profile, ".lmstudio-home-pointer");
            string legacy = Path.Combine(profile, ".cache", "lm-studio");
            if (File.Exists(pointer) && File.ReadAllText(pointer).Trim() is { Length: > 0 } pointed)
            {
                home = pointed;
            }
            else if (Directory.Exists(legacy))
            {
                home = legacy;
            }

            string settings = Path.Combine(home, "settings.json");
            if (File.Exists(settings))
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
                if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("downloadsFolder", out var folder) &&
                    folder.ValueKind == System.Text.Json.JsonValueKind.String &&
                    folder.GetString() is { Length: > 0 } moved)
                {
                    return moved;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The default stands; a folder that can't be read is reported when the scan gets to it.
        }

        return Path.Combine(home, "models");
    }

    /// <summary>GPT4All's download folder: its settings' <c>modelPath</c>, else <c>%LOCALAPPDATA%\nomic.ai\GPT4All</c>.</summary>
    private static string GptForAllFolder(string localAppData)
    {
        string folder = Path.Combine(localAppData, "nomic.ai", "GPT4All");
        try
        {
            string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nomic.ai", "GPT4All.ini");
            if (File.Exists(ini))
            {
                foreach (string line in File.ReadLines(ini))
                {
                    if (line.StartsWith("modelPath=", StringComparison.OrdinalIgnoreCase) && line[10..].Trim() is { Length: > 0 } path)
                    {
                        return path.Replace('/', '\\');
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The default stands.
        }

        return folder;
    }

    /// <summary>An environment variable from this process, the user's settings or the machine's, expanded; null when unset.</summary>
    internal static string? Variable(string name)
    {
        foreach (EnvironmentVariableTarget target in (EnvironmentVariableTarget[])[EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine])
        {
            string? value = Environment.GetEnvironmentVariable(name, target)?.Trim().Trim('"').Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return Environment.ExpandEnvironmentVariables(value);
            }
        }

        return null;
    }

    internal static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Full(a)), Path.TrimEndingDirectorySeparator(Full(b)), StringComparison.OrdinalIgnoreCase);

    internal static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
