using System.Security.Cryptography;
using System.Text;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Tests;

/// <summary>
/// Hand-written in-memory fakes for the write-side seams used by the app's reversible mutation engines.
/// Every engine test runs exclusively against these fakes (or, for the few integration tests, a
/// throwaway temp directory) so the suite NEVER touches a real cache, environment variable, or disk.
/// </summary>
internal sealed class InMemoryFileSystem : IFileSystem
{
    // Keyed by normalized full path (backslashes, no trailing slash), case-insensitive like Windows.
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, <see cref="CopyFile"/> throws if the destination path contains this substring.</summary>
    public string? FailCopyToContaining { get; set; }

    /// <summary>When set, <see cref="CopyFile"/> writes different bytes than the source if the destination contains this substring (to exercise the hash-verification failure path).</summary>
    public string? CorruptCopyToContaining { get; set; }

    /// <summary>Number of successful <see cref="CopyFile"/> calls (for asserting rollback deleted what it copied).</summary>
    public int CopyCount { get; private set; }

    // ---- Test setup helpers --------------------------------------------------------------------

    public InMemoryFileSystem AddDirectory(string path)
    {
        RegisterDirectory(Normalize(path));
        return this;
    }

    public InMemoryFileSystem AddFile(string path, string contents) => AddFile(path, Encoding.UTF8.GetBytes(contents));

    public InMemoryFileSystem AddFile(string path, byte[] contents)
    {
        string key = Normalize(path);
        _files[key] = contents;
        RegisterParents(key);
        return this;
    }

    public int FileCount => _files.Count;

    public IReadOnlyCollection<string> AllFiles => _files.Keys.ToList();

    public IReadOnlyCollection<string> AllDirectories => _dirs.ToList();

    // ---- IFileSystem ---------------------------------------------------------------------------

    public bool DirectoryExists(string path) => _dirs.Contains(Normalize(path));

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public void CreateDirectory(string path) => RegisterDirectory(Normalize(path));

    public void DeleteDirectory(string path, bool recursive)
    {
        string key = Normalize(path);
        if (!_dirs.Contains(key))
        {
            return; // No-op when absent, per contract.
        }

        string prefix = key + "\\";
        List<string> childFiles = _files.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> childDirs = _dirs.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!recursive && (childFiles.Count > 0 || childDirs.Count > 0))
        {
            throw new IOException($"Directory is not empty: '{path}'.");
        }

        foreach (string f in childFiles)
        {
            _files.Remove(f);
        }

        foreach (string d in childDirs)
        {
            _dirs.Remove(d);
        }

        _dirs.Remove(key);
    }

    public void DeleteFile(string path) => _files.Remove(Normalize(path));

    public IReadOnlyList<string> EnumerateFiles(string directory)
    {
        string prefix = Normalize(directory) + "\\";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase) // deterministic for progress assertions
            .ToList();
    }

    public long GetFileLength(string path)
    {
        string key = Normalize(path);
        return _files.TryGetValue(key, out byte[]? bytes)
            ? bytes.Length
            : throw new FileNotFoundException("File not found.", path);
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        string src = Normalize(sourcePath);
        string dst = Normalize(destinationPath);

        if (FailCopyToContaining is not null && dst.Contains(FailCopyToContaining, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Injected copy failure for '{destinationPath}'.");
        }

        if (!_files.TryGetValue(src, out byte[]? bytes))
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        if (!overwrite && _files.ContainsKey(dst))
        {
            throw new IOException($"Destination already exists: '{destinationPath}'.");
        }

        byte[] toWrite = bytes;
        if (CorruptCopyToContaining is not null && dst.Contains(CorruptCopyToContaining, StringComparison.OrdinalIgnoreCase))
        {
            toWrite = bytes.Concat(new byte[] { 0xFF }).ToArray(); // differs from source -> hash mismatch
        }

        _files[dst] = toWrite;
        RegisterParents(dst);
        CopyCount++;
    }

    public string ReadAllText(string path)
    {
        string key = Normalize(path);
        return _files.TryGetValue(key, out byte[]? bytes)
            ? Encoding.UTF8.GetString(bytes)
            : throw new FileNotFoundException("File not found.", path);
    }

    public void WriteAllText(string path, string contents)
    {
        string key = Normalize(path);
        _files[key] = Encoding.UTF8.GetBytes(contents ?? string.Empty);
        RegisterParents(key);
    }

    public string ComputeSha256(string path)
    {
        string key = Normalize(path);
        if (!_files.TryGetValue(key, out byte[]? bytes))
        {
            throw new FileNotFoundException("File not found.", path);
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // ---- internals -----------------------------------------------------------------------------

    private void RegisterDirectory(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        _dirs.Add(normalized);
        RegisterParents(normalized);
    }

    private void RegisterParents(string normalizedPath)
    {
        string? parent = GetParent(normalizedPath);
        while (!string.IsNullOrEmpty(parent))
        {
            if (!_dirs.Add(parent))
            {
                break; // already registered (and so are its ancestors)
            }

            parent = GetParent(parent);
        }
    }

    private static string? GetParent(string normalizedPath)
    {
        int idx = normalizedPath.LastIndexOf('\\');
        if (idx <= 0)
        {
            return null;
        }

        string parent = normalizedPath[..idx];
        // Keep a drive root like "C:" out of the directory set as a navigable parent.
        return parent.EndsWith(':') ? null : parent;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string p = path.Replace('/', '\\').TrimEnd('\\');
        return p;
    }
}

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> for tests. Unlike <see cref="System.Progress{T}"/> — which
/// marshals callbacks to a captured <see cref="SynchronizationContext"/> / the thread pool, so they
/// can arrive late and out of order — this records each report inline on the caller's thread, in
/// order, making progress assertions deterministic.
/// </summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = new();
    private readonly object _gate = new();

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_gate)
            {
                return _reports.ToList();
            }
        }
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _reports.Add(value);
        }
    }
}

/// <summary>In-memory <see cref="IEnvironmentWriter"/>; records sets and removals for assertions.</summary>
internal sealed class FakeEnvironmentWriter : IEnvironmentWriter
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Name, string? Value)> SetCalls { get; } = new();
    public List<string> Removals { get; } = new();

    public FakeEnvironmentWriter Seed(string name, string value)
    {
        _vars[name] = value;
        return this;
    }

    public string? GetUserVariable(string name) => _vars.TryGetValue(name, out string? value) ? value : null;

    public void SetUserVariable(string name, string? value)
    {
        SetCalls.Add((name, value));
        if (string.IsNullOrEmpty(value))
        {
            _vars.Remove(name);
            Removals.Add(name);
        }
        else
        {
            _vars[name] = value;
        }
    }
}

/// <summary>In-memory <see cref="IPathProbe"/>: returns a configured path for known executables.</summary>
internal sealed class FakePathProbe : IPathProbe
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public FakePathProbe Add(string executableName, string path)
    {
        _paths[executableName] = path;
        return this;
    }

    public string? Resolve(string executableName) =>
        _paths.TryGetValue(executableName, out string? path) ? path : null;
}

/// <summary>
/// In-memory <see cref="IProcessRunner"/>: maps an executable name to a canned <see cref="ProcessRunResult"/>.
/// Unknown executables return a negative exit code (i.e. "not launched"), matching the real runner.
/// No real process is ever launched.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessRunResult> _results = new(StringComparer.OrdinalIgnoreCase);

    public FakeProcessRunner Set(string fileName, string stdout, string stderr = "", int exitCode = 0, bool timedOut = false)
    {
        _results[fileName] = new ProcessRunResult(exitCode, stdout, stderr) { TimedOut = timedOut };
        return this;
    }

    public ProcessRunResult Run(string fileName, string arguments) =>
        _results.TryGetValue(fileName, out ProcessRunResult? result)
            ? result
            : new ProcessRunResult(-1, string.Empty, string.Empty);
}

/// <summary>
/// In-memory <see cref="IWorkloadBenchmark"/> for the orchestrator tests. Records the lifecycle calls
/// and returns canned per-drive times (or skips by throwing <see cref="WorkloadUnavailableException"/>)
/// so the suite NEVER launches a real git/npm/dotnet/cargo process.
/// </summary>
internal sealed class FakeWorkloadBenchmark : IWorkloadBenchmark
{
    private readonly string _systemRoot;
    private readonly double _systemSeconds;
    private readonly double _devSeconds;
    private readonly string? _prepareSkip;
    private readonly string? _measureSkip;
    private readonly Exception? _prepareThrows;

    public FakeWorkloadBenchmark(
        string name,
        string requiredTool,
        string systemRoot = "C:\\",
        double systemSeconds = 2d,
        double devSeconds = 1d,
        string detail = "fixture",
        string? prepareSkip = null,
        string? measureSkip = null,
        Exception? prepareThrows = null)
    {
        Name = name;
        RequiredTool = requiredTool;
        Detail = detail;
        _systemRoot = systemRoot;
        _systemSeconds = systemSeconds;
        _devSeconds = devSeconds;
        _prepareSkip = prepareSkip;
        _measureSkip = measureSkip;
        _prepareThrows = prepareThrows;
    }

    public string Name { get; }

    public string Detail { get; }

    public string RequiredTool { get; }

    public WorkloadProfile Profile { get; set; } = WorkloadProfile.Quick;

    /// <summary>The <see cref="Profile"/> value observed when <see cref="Prepare"/> was called (null if never prepared).</summary>
    public WorkloadProfile? ProfileAtPrepare { get; private set; }

    public int PrepareCount { get; private set; }

    public int CleanupCount { get; private set; }

    public List<string> MeasuredRoots { get; } = new();

    public void Prepare(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        PrepareCount++;
        ProfileAtPrepare = Profile;
        if (_prepareThrows is not null)
        {
            throw _prepareThrows;
        }

        if (_prepareSkip is not null)
        {
            throw new WorkloadUnavailableException(_prepareSkip);
        }
    }

    public double MeasureOnce(string driveRoot, CancellationToken cancellationToken)
    {
        MeasuredRoots.Add(driveRoot);
        if (_measureSkip is not null)
        {
            throw new WorkloadUnavailableException(_measureSkip);
        }

        return string.Equals(driveRoot, _systemRoot, StringComparison.OrdinalIgnoreCase) ? _systemSeconds : _devSeconds;
    }

    public void Cleanup() => CleanupCount++;
}
