using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IReversibilityStore"/> that persists entries to a JSON file (an array of
/// <see cref="ReversibilityEntry"/>). This is app-local bookkeeping — writing the file records what a
/// mutation engine changed so it can be undone later; it is not itself a machine mutation.
/// </summary>
/// <remarks>
/// <b>WIRED:</b> the app composes this via <see cref="DevDriveCore.Services.PackageCacheMoveCoordinator"/>
/// (<c>CreateDefault</c>, pointed at <see cref="DefaultPath"/>), so a move recorded in one launch is
/// visible to "Move back" in the next. Construction is side-effect-free — the file is read lazily and
/// only written on <see cref="Save"/>/<see cref="Remove"/>. Unit tests point it at a throwaway temp file.
/// </remarks>
public sealed class JsonFileReversibilityStore : IReversibilityStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <summary>Creates a store backed by the JSON file at <paramref name="filePath"/>.</summary>
    public JsonFileReversibilityStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// The default per-user persistence path: <c>%LocalAppData%\DevDriveManager\reversibility.json</c>.
    /// Pure string computation — no directory is created.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevDriveManager",
        "reversibility.json");

    /// <inheritdoc />
    public void Save(ReversibilityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(entry.Id);

        Dictionary<string, ReversibilityEntry> entries = Load();
        entries[entry.Id] = entry;
        Persist(entries);
    }

    /// <inheritdoc />
    public ReversibilityEntry? TryGet(string id) =>
        id is not null && Load().TryGetValue(id, out ReversibilityEntry? entry) ? entry : null;

    /// <inheritdoc />
    public IReadOnlyList<ReversibilityEntry> GetAll() => Load().Values.ToList();

    /// <inheritdoc />
    public bool Remove(string id)
    {
        if (id is null)
        {
            return false;
        }

        Dictionary<string, ReversibilityEntry> entries = Load();
        if (!entries.Remove(id))
        {
            return false;
        }

        Persist(entries);
        return true;
    }

    private Dictionary<string, ReversibilityEntry> Load()
    {
        var result = new Dictionary<string, ReversibilityEntry>(StringComparer.Ordinal);
        if (!File.Exists(_filePath))
        {
            return result;
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't read the store right now (locked/permissions). Degrade to "no entries" WITHOUT
            // touching the file, so a transient access error never destroys a good store.
            return result;
        }

        try
        {
            ReversibilityEntry[]? entries = JsonSerializer.Deserialize<ReversibilityEntry[]>(json);
            if (entries is not null)
            {
                foreach (ReversibilityEntry entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.Id))
                    {
                        result[entry.Id] = entry;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // F13: the file exists but its contents are corrupt. Rather than silently degrading to an
            // empty store (which the next Save would then overwrite, destroying any salvageable bytes
            // forever), QUARANTINE the bad file by renaming it aside so it can be inspected/recovered,
            // and start this session from a clean store.
            TryQuarantineCorruptFile();
            result.Clear();
        }

        return result;
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            string quarantinePath = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(_filePath, quarantinePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort quarantine; if we can't move it aside the worst case is we re-read it next time.
        }
    }

    private void Persist(Dictionary<string, ReversibilityEntry> entries)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(entries.Values.ToArray(), SerializerOptions);

        // F13 (atomic write): write to a sibling temp file then atomically replace the real file. A crash
        // or a full disk mid-write then leaves the previous good store intact instead of a truncated,
        // unparseable file.
        string tempPath = $"{_filePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup of the temp file; never mask the original write outcome.
                }
            }
        }
    }
}
