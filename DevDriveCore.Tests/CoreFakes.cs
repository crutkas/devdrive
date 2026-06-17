using DevDriveCore.Abstractions;

namespace DevDriveCore.Tests;

/// <summary>
/// Lightweight in-memory fakes for the new platform seams. Real (dictionary/HashSet) fakes are
/// clearer than mocks for the environment-variable expansion and directory-exists behaviour the
/// package-cache and source-location services depend on.
/// </summary>
internal sealed class FakeEnvironmentProvider : IEnvironmentProvider
{
    private readonly Dictionary<string, string> _vars;

    public FakeEnvironmentProvider(IDictionary<string, string>? vars = null) =>
        _vars = vars is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);

    public FakeEnvironmentProvider Set(string name, string? value)
    {
        if (value is null)
        {
            _vars.Remove(name);
        }
        else
        {
            _vars[name] = value;
        }

        return this;
    }

    public string? GetEnvironmentVariable(string name) =>
        _vars.TryGetValue(name, out string? value) ? value : null;

    public string ExpandEnvironmentVariables(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        string result = template;
        foreach (KeyValuePair<string, string> kv in _vars)
        {
            result = result.Replace($"%{kv.Key}%", kv.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}

internal sealed class FakeFileSystemProbe : IFileSystemProbe
{
    private readonly HashSet<string> _dirs;
    private readonly Dictionary<string, ulong> _sizes;

    public FakeFileSystemProbe(IEnumerable<string>? directories = null, IDictionary<string, ulong>? sizes = null)
    {
        _dirs = new HashSet<string>(directories ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _sizes = sizes is null
            ? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ulong>(sizes, StringComparer.OrdinalIgnoreCase);
    }

    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && _dirs.Contains(path);

    public Task<ulong> GetDirectorySizeAsync(string path, TimeSpan timeBudget, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sizes.TryGetValue(path, out ulong size) ? size : 0UL);
}
