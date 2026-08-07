namespace DevDriveReclaim.Tests;

using DevDriveReclaim.Providers;

/// <summary>
/// Builds real, disposable directory trees under the temp folder. Nothing here touches the
/// developer's own data: every path lives below a unique temp root and is deleted on dispose.
/// </summary>
internal sealed class ReclaimFixture : IDisposable
{
    public ReclaimFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "devdrive-reclaim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(params string[] segments)
    {
        string path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a file of exactly <paramref name="bytes"/> apparent length.</summary>
    public string File(string relativePath, long bytes)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(bytes);
        return path;
    }

    /// <summary>Creates a file with specific content, so duplicate detection has something to hash.</summary>
    public string FileWithContent(string relativePath, byte[] content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Marks a folder as a git repository (a real <c>.git</c> directory).</summary>
    public string Repo(string name)
    {
        string path = Dir(name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    /// <summary>Marks a folder as a git worktree (a <c>.git</c> <em>file</em> pointing elsewhere).</summary>
    public string Worktree(string name, string gitDir)
    {
        string path = Dir(name);
        System.IO.File.WriteAllText(Path.Combine(path, ".git"), $"gitdir: {gitDir}");
        return path;
    }

    /// <summary>
    /// Creates a directory junction, the reparse point a developer machine actually has. Junctions
    /// need no elevation and no Developer Mode, unlike symbolic links, which is why the tests that
    /// need a reparse point use one.
    /// </summary>
    public string Junction(string name, string target)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{path}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        process.WaitForExit(10_000);

        return path;
    }

    public void Age(string path, TimeSpan age)
    {
        var when = DateTime.UtcNow - age;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetLastWriteTimeUtc(file, when);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked handle on a temp tree must never fail a test run.
        }
    }
}

/// <summary>A scripted inspector, so risk grading is testable without building real git history.</summary>
internal sealed class StubWorktreeInspector(WorktreeState state) : IWorktreeInspector
{
    public WorktreeState Inspect(string worktreePath, CancellationToken cancellationToken) => state;
}
