using System.Text.Json;
using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>Where free-space readings are kept between launches.</summary>
public interface IFreeSpaceHistoryStore
{
    /// <summary>Every reading on record, oldest first. Never throws — a missing or corrupt store reads as empty.</summary>
    IReadOnlyList<FreeSpaceSample> Load();

    /// <summary>
    /// Records a reading, subject to <see cref="FreeSpaceHistory.MinimumInterval"/>.
    /// </summary>
    /// <returns>The history after the write.</returns>
    IReadOnlyList<FreeSpaceSample> Append(FreeSpaceSample sample);
}

/// <summary>
/// A JSON file of free-space readings under the user's local app data.
/// </summary>
/// <remarks>
/// <para>
/// Failure is always silent here, and that is deliberate. This history is a nicety — the room it
/// feeds degrades to "not enough history yet", which is a state it has to render correctly anyway on
/// first run. Nothing about that is worth an error dialog, let alone taking down a launch because a
/// roaming profile had the file open.
/// </para>
/// <para>
/// Writes go to a temporary file and then replace, so a crash mid-write costs the newest reading
/// rather than every reading.
/// </para>
/// </remarks>
public sealed class JsonFreeSpaceHistoryStore : IFreeSpaceHistoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>Creates a store at an explicit path. Tests use this; the app uses the default.</summary>
    public JsonFreeSpaceHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Creates a store at the default per-user location.</summary>
    public JsonFreeSpaceHistoryStore()
        : this(DefaultPath())
    {
    }

    /// <summary><c>%LOCALAPPDATA%\DevDriveManager\free-space-history.json</c>.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevDriveManager",
        "free-space-history.json");

    /// <inheritdoc />
    public IReadOnlyList<FreeSpaceSample> Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FreeSpaceSample> Append(FreeSpaceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_gate)
        {
            IReadOnlyList<FreeSpaceSample> existing = LoadCore();
            IReadOnlyList<FreeSpaceSample> updated = FreeSpaceHistory.Append(existing, sample);

            if (!ReferenceEquals(existing, updated) && updated.Count != existing.Count)
            {
                Save(updated);
            }

            return updated;
        }
    }

    private IReadOnlyList<FreeSpaceSample> LoadCore()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            FreeSpaceSample[]? samples = JsonSerializer.Deserialize<FreeSpaceSample[]>(
                File.ReadAllText(_path), Options);

            return samples is null
                ? []
                : [.. samples.Where(s => !string.IsNullOrWhiteSpace(s.VolumeId)).OrderBy(s => s.TakenAtUtc)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(IReadOnlyList<FreeSpaceSample> samples)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(samples, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See the class remarks: a history we could not write is a chart that says "not yet".
        }
    }
}
