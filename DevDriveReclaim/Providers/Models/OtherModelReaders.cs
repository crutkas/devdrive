using System.Text.Json;

namespace DevDriveReclaim.Providers.Models;

/// <summary>
/// LM Studio's models folder: <c>&lt;publisher&gt;\&lt;model&gt;\*.gguf</c>, a folder per model with nothing shared between them and
/// no index (LM Studio lists the folder). LM Studio has no command that removes a model; its My Models › Delete removes the
/// model's folder, and so does Reclaim.
/// </summary>
internal static class LmStudioReader
{
    public static List<FoundModel> Read(string models, Action<string, string> unreadable, CancellationToken cancellationToken)
    {
        List<FoundModel> found = [];
        foreach (string publisher in Directory.EnumerateDirectories(models))
        {
            if (IsLinkOrHidden(publisher))
            {
                continue;
            }

            foreach (string model in Directory.EnumerateDirectories(publisher))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLinkOrHidden(model))
                {
                    continue;
                }

                try
                {
                    ModelFootprint footprint = ModelFiles.Measure(model, cancellationToken);
                    List<string> weights = [.. footprint.Files.Where(f => f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))];
                    if (weights.Count == 0)
                    {
                        continue;
                    }

                    string name = $"{Path.GetFileName(publisher)}/{Path.GetFileName(model)}";
                    found.Add(new FoundModel(
                        LocalModelTools.LmStudio,
                        LocalModelTools.TitleOf(LocalModelTools.LmStudio),
                        models,
                        model,
                        [name],
                        footprint.Bytes,
                        footprint.SharedBytes,
                        weights.Count == 1 ? Path.GetFileName(weights[0]) : $"{weights.Count} files",
                        LocalModelReclaimProvider.Recovery(LocalModelTools.LmStudio, string.Empty, name)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    unreadable(model, exception is UnauthorizedAccessException ? "access denied" : exception.Message);
                }
            }
        }

        return found;
    }

    internal static bool IsLinkOrHidden(string folder) =>
        Path.GetFileName(folder).StartsWith('.') || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;
}

/// <summary>
/// Foundry Local's model cache (<c>foundry cache location</c>, by default <c>%USERPROFILE%\.foundry\cache\models</c>):
/// <c>&lt;publisher&gt;\&lt;model&gt;\v&lt;n&gt;\…</c>, each model whole on its own. A model's id is the <c>Name</c> in its
/// <c>inference_model.json</c>; one still downloading has a <c>download.tmp</c> and isn't a model yet. <c>foundry cache
/// remove</c> removes the model's whole folder, every revision (sdk_v2 README).
/// </summary>
internal static class FoundryReader
{
    public static List<FoundModel> Read(string cache, Action<string, string> unreadable, CancellationToken cancellationToken)
    {
        List<FoundModel> found = [];
        foreach (string publisher in Directory.EnumerateDirectories(cache))
        {
            if (LmStudioReader.IsLinkOrHidden(publisher))
            {
                continue;
            }

            foreach (string model in Directory.EnumerateDirectories(publisher))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LmStudioReader.IsLinkOrHidden(model))
                {
                    continue;
                }

                try
                {
                    ModelFootprint footprint = ModelFiles.Measure(model, cancellationToken);
                    if (footprint.Files.Any(f => string.Equals(Path.GetFileName(f), "download.tmp", StringComparison.OrdinalIgnoreCase)) ||
                        Id(footprint.Files) is not { } id)
                    {
                        continue;
                    }

                    found.Add(new FoundModel(
                        LocalModelTools.FoundryLocal,
                        LocalModelTools.TitleOf(LocalModelTools.FoundryLocal),
                        cache,
                        model,
                        [id],
                        footprint.Bytes,
                        footprint.SharedBytes,
                        null,
                        LocalModelReclaimProvider.Recovery(LocalModelTools.FoundryLocal, string.Empty, id)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    unreadable(model, exception is UnauthorizedAccessException ? "access denied" : exception.Message);
                }
            }
        }

        return found;
    }

    /// <summary>The model id the newest revision's <c>inference_model.json</c> gives, or null.</summary>
    internal static string? Id(IEnumerable<string> files)
    {
        foreach (string file in files
            .Where(f => string.Equals(Path.GetFileName(f), "inference_model.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("Name", out JsonElement name) &&
                    name.ValueKind == JsonValueKind.String &&
                    name.GetString() is { Length: > 0 } id)
                {
                    return id;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // The next one, if any.
            }
        }

        return null;
    }
}

/// <summary>
/// A folder an app downloads standalone model files to (GPT4All, a setup script's): each <c>*.gguf</c> directly in it is a
/// model, and removing one is deleting that file, which is what the app's own remove does.
/// </summary>
internal static class ModelFolderReader
{
    public static List<FoundModel> Read(ModelFolder folder, CancellationToken cancellationToken)
    {
        List<FoundModel> found = [];
        foreach (string file in Directory.EnumerateFiles(folder.Path, "*.gguf"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            long size = ModelFiles.Allocated(file, info.Length);
            bool shared = ModelFiles.LinkCount(file) > 1;
            found.Add(new FoundModel(
                LocalModelTools.ModelFiles,
                folder.Title,
                folder.Path,
                file,
                [Path.GetFileNameWithoutExtension(file)],
                shared ? 0 : size,
                shared ? size : 0,
                null,
                folder.RecoveryHint));
        }

        return found;
    }
}
