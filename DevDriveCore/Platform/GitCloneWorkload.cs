using System.Globalization;
using System.Text;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;

namespace DevDriveCore.Platform;

/// <summary>
/// Times <c>git clone</c> of a locally-seeded bare repository onto each drive — the first, most
/// important benchmark because it is fully offline (no network) and universally available wherever
/// git is installed.
/// </summary>
/// <remarks>
/// <para>A working tree of many tiny files is generated and committed once under the seed folder, then
/// converted to a bare repo. Each measured iteration runs <c>git clone --no-hardlinks</c> from that
/// bare repo into a fresh working directory on the target drive. <c>--no-hardlinks</c> forces real
/// file copies even for the same-filesystem (C:→C:) case so the comparison is fair.</para>
/// <para>The tree size follows the <see cref="WorkloadProfile"/>: <b>Quick</b> ≈ 1,200 files
/// (60 dirs × 20), <b>Thorough</b> ≈ 15,000 files (300 dirs × 50) to expose the many-small-file churn
/// where the Dev Drive's async-scanning advantage shows. Both stay a few MB and are cleaned up.</para>
/// </remarks>
public sealed class GitCloneWorkload : WorkloadBenchmarkBase
{
    private const int QuickDirectories = 60;
    private const int QuickFilesPerDirectory = 20;       // 1,200 files
    private const int ThoroughDirectories = 300;
    private const int ThoroughFilesPerDirectory = 50;    // 15,000 files
    private const int TimeoutMs = 120_000;

    // A many-small-file checkout can transiently fail under synchronous AV on the system drive; retry a
    // bounded number of times (a real `git clone` user would just re-run) before skipping.
    private const int MaxCloneAttempts = 3;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(300);

    private string _barePath = string.Empty;

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    public GitCloneWorkload(IWorkloadProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    public override string Name => "git clone";

    /// <inheritdoc />
    public override string Detail =>
        $"local bare repo · {FileCount:N0} files · no-hardlinks ({Profile})";

    /// <inheritdoc />
    public override string RequiredTool => "git";

    /// <inheritdoc />
    protected override string Slug => "git-clone";

    private int Directories => Profile == WorkloadProfile.Thorough ? ThoroughDirectories : QuickDirectories;

    private int FilesPerDirectory => Profile == WorkloadProfile.Thorough ? ThoroughFilesPerDirectory : QuickFilesPerDirectory;

    private int FileCount => Directories * FilesPerDirectory;

    /// <inheritdoc />
    protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        string sourceTree = Path.Combine(SeedDirectory, "src");
        GenerateWorkingTree(sourceTree, Directories, FilesPerDirectory, cancellationToken);

        // Initialise, stage, and commit the generated tree (identity supplied inline so no global config is touched).
        RunChecked(Git("-c init.defaultBranch=main init -q .", sourceTree), cancellationToken, "git init");
        RunChecked(Git("add -A", sourceTree), cancellationToken, "git add");
        RunChecked(
            Git("-c user.email=bench@devdrive.local -c user.name=DevDriveBench -c commit.gpgsign=false commit -q -m seed", sourceTree),
            cancellationToken,
            "git commit");

        // Convert to a bare repo that the measured clones read from (warm in RAM for both drives).
        _barePath = Path.Combine(SeedDirectory, "bench.git");
        RunChecked(Git($"clone --bare -q \"{sourceTree}\" \"{_barePath}\"", SeedDirectory), cancellationToken, "git clone --bare");
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <c>git clone</c> that checks out thousands of tiny files can transiently fail (a non-zero exit
    /// with no diagnostic) when synchronous Defender scanning — i.e. performance mode <b>off</b> — briefly
    /// locks a freshly written file on the system drive. That is exactly the churn this benchmark exists
    /// to surface, and a real developer simply re-runs, so we retry a bounded number of times (fresh
    /// target each attempt, cancellation-aware backoff) before honestly skipping. Only the successful
    /// attempt is timed; the retries keep one spurious hiccup from discarding the whole git result.
    /// </remarks>
    protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxCloneAttempts; attempt++)
        {
            // Fresh target per attempt so a partial, AV-locked checkout can never block the retry.
            string target = Path.Combine(workDir, "clone" + attempt.ToString(CultureInfo.InvariantCulture));
            try
            {
                return TimeProcess(
                    Git($"clone --no-hardlinks -q \"{_barePath}\" \"{target}\"", workDir),
                    cancellationToken,
                    "git clone");
            }
            catch (WorkloadUnavailableException) when (attempt < MaxCloneAttempts)
            {
                // Discard the partial checkout and pause briefly (or bail out promptly if cancelled).
                DeleteTree(target);
                if (cancellationToken.WaitHandle.WaitOne(RetryBackoff))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        // Unreachable: the final attempt either returns or lets its failure propagate (the `when` is false).
        throw new WorkloadUnavailableException("git clone failed");
    }

    private static WorkloadProcessRequest Git(string arguments, string workingDirectory) =>
        new("git", arguments, workingDirectory, TimeoutMs: TimeoutMs);

    /// <summary>Generates a deterministic tree of many small text files (bounded to a few MB total).</summary>
    private static void GenerateWorkingTree(string root, int directories, int filesPerDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);

        // A README plus a .gitignore so the tree resembles a real small project.
        File.WriteAllText(Path.Combine(root, "README.md"), "# DevDriveManager benchmark fixture\n\nGenerated, disposable.\n");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\nobj/\n");

        for (int d = 0; d < directories; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = Path.Combine(root, "module" + d.ToString("D3", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);

            for (int f = 0; f < filesPerDirectory; f++)
            {
                string file = Path.Combine(dir, $"source{f:D2}.txt");
                File.WriteAllText(file, BuildFileContent(d, f), Encoding.UTF8);
            }
        }
    }

    private static string BuildFileContent(int dir, int file)
    {
        var builder = new StringBuilder(256);
        builder.Append("// module ").Append(dir).Append(" file ").Append(file).Append('\n');
        for (int line = 0; line < 8; line++)
        {
            builder.Append("const value_").Append(dir).Append('_').Append(file).Append('_').Append(line)
                   .Append(" = ").Append((dir * 131 + file * 17 + line) % 1000).Append(";\n");
        }

        return builder.ToString();
    }
}
