using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>Work that keeps CPUs busy runs below the window's thread (<see cref="BackgroundWork"/>).</summary>
[TestClass]
public sealed class BackgroundWorkTests
{
    [TestMethod]
    public void AThreadStartsBelowNormal()
    {
        ThreadPriority seen = ThreadPriority.Highest;
        Thread thread = BackgroundWork.Thread(() => seen = Thread.CurrentThread.Priority, "test");
        Assert.IsTrue(thread.IsBackground);
        Assert.AreEqual("test", thread.Name);
        thread.Start();
        thread.Join();

        Assert.AreEqual(ThreadPriority.BelowNormal, seen);
    }

    [TestMethod]
    public void EveryLoopRunsItsWorkBelowNormalAndGivesTheCallerItsPriorityBack()
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        Action<ConcurrentBag<ThreadPriority>>[] loops =
        [
            seen => BackgroundWork.For(0, 64, options, _ => seen.Add(Thread.CurrentThread.Priority)),
            seen => BackgroundWork.ForEach(Enumerable.Range(0, 64), options, _ => seen.Add(Thread.CurrentThread.Priority)),
            seen => BackgroundWork.For(
                0,
                64,
                () => 0,
                (_, _, local) =>
                {
                    seen.Add(Thread.CurrentThread.Priority);
                    return local;
                },
                _ => seen.Add(Thread.CurrentThread.Priority)),
        ];

        foreach (Action<ConcurrentBag<ThreadPriority>> loop in loops)
        {
            var seen = new ConcurrentBag<ThreadPriority>();
            ThreadPriority after = OnNormalThread(() => loop(seen));

            Assert.IsGreaterThanOrEqualTo(64, seen.Count);
            Assert.IsTrue(seen.All(p => p == ThreadPriority.BelowNormal));
            Assert.AreEqual(ThreadPriority.Normal, after);
        }
    }

    [TestMethod]
    public void ALoopThatThrowsStillGivesTheCallerItsPriorityBack()
    {
        ThreadPriority after = OnNormalThread(() => Assert.ThrowsExactly<AggregateException>(() =>
            BackgroundWork.For(0, 64, new ParallelOptions(), i =>
            {
                if (i % 8 == 3)
                {
                    throw new InvalidOperationException();
                }
            })));

        Assert.AreEqual(ThreadPriority.Normal, after);
    }

    [TestMethod]
    public async Task RunDoesItsWorkBelowNormal()
    {
        Assert.AreEqual(ThreadPriority.BelowNormal, await BackgroundWork.Run(() => Thread.CurrentThread.Priority));
    }

    [TestMethod]
    public void LoweringNeverRaisesAThread()
    {
        ThreadPriority inside = ThreadPriority.Normal;
        var thread = new Thread(() =>
        {
            using (BackgroundWork.Lower())
            {
                inside = Thread.CurrentThread.Priority;
            }
        })
        { Priority = ThreadPriority.Lowest };
        thread.Start();
        thread.Join();

        Assert.AreEqual(ThreadPriority.Lowest, inside);
    }

    /// <summary>
    /// A thread, a parallel loop or a long-running task anywhere but <see cref="BackgroundWork"/> would start at Normal, and so
    /// would a Reclaim provider's or a storage source's <c>Task.Run</c>: they walk and hash.
    /// </summary>
    [TestMethod]
    public void NoWorkerStartsAtNormal()
    {
        string root = RepoRoot();
        var waits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("DevDriveCore", "Platform", "PathProbe.cs")] = "checks a share within a bound: it waits, and must answer in time",
        };
        var anywhere = new Regex(@"new\s+Thread\s*\(|\bParallel\.(For|ForEach|Invoke)\b|\.AsParallel\s*\(|TaskCreationOptions\.LongRunning");
        var run = new Regex(@"\bTask\.Run\b");
        string[] scanning = [Path.Combine("DevDriveReclaim", "Providers"), "DevDriveStorage"];

        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || p.EndsWith("Tests", StringComparison.Ordinal) || p is "obj" or "bin")
                || relative.Equals(Path.Combine("DevDriveCore", "Platform", "BackgroundWork.cs"), StringComparison.OrdinalIgnoreCase)
                || waits.ContainsKey(relative))
            {
                continue;
            }

            bool scans = scanning.Any(s => relative.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (anywhere.IsMatch(code) || (scans && run.IsMatch(code)))
                {
                    found.Add($"{relative}:{i + 1}: {code}");
                }
            }
        }

        Assert.IsEmpty(found, "Start it through BackgroundWork (Thread, For, ForEach or Run):\n" + string.Join("\n", found));
    }

    private static ThreadPriority OnNormalThread(Action work)
    {
        ThreadPriority after = ThreadPriority.Highest;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
                after = Thread.CurrentThread.Priority;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        { Priority = ThreadPriority.Normal };
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new AssertFailedException("The loop failed.", failure);
        }

        return after;
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DevDriveCore", "DevDriveCore.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new AssertInconclusiveException("The tests don't run inside a devdrive clone.");
    }
}
