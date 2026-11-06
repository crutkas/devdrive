using System.Diagnostics;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The real git, for tests that need git's own answer about a real repository in a <see cref="ReclaimFixture"/>. Tests that
/// use it are Inconclusive rather than failed when git isn't on PATH, as <see cref="GitWorktreeInspectorTests"/> are.
/// </summary>
internal static class TestGit
{
    private static bool? _available;

    public static bool IsAvailable => _available ??= Probe();

    /// <summary>Runs git in <paramref name="workingDirectory"/> and returns what it printed; fails the test if git fails.</summary>
    public static string Run(string workingDirectory, string arguments)
    {
        using Process? process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Assert.IsNotNull(process, $"could not start: git {arguments}");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(30_000), $"git {arguments} did not finish");
        Assert.AreEqual(0, process.ExitCode, $"git {arguments}: {error.Result}");
        return output.Result;
    }

    /// <summary>A repository with one commit on <c>main</c>, its identity set locally so the developer's isn't used or changed.</summary>
    public static string Repo(ReclaimFixture fixture, string name)
    {
        string root = fixture.Dir(name);
        Run(root, "init --initial-branch=main");
        Identity(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "committed\n");
        Run(root, "add .");
        Run(root, "commit -m initial");
        return root;
    }

    /// <summary>Sets a throwaway identity in <paramref name="repository"/>'s own config, never the global one.</summary>
    public static void Identity(string repository)
    {
        Run(repository, "config user.email tests@example.invalid");
        Run(repository, "config user.name Tests");
        Run(repository, "config commit.gpgsign false");
    }

    private static bool Probe()
    {
        try
        {
            using Process? probe = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null)
            {
                return false;
            }

            probe.BeginOutputReadLine();
            probe.BeginErrorReadLine();
            return probe.WaitForExit(10_000) && probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
