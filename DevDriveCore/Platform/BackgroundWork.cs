namespace DevDriveCore.Platform;

/// <summary>
/// Runs work that keeps CPUs busy off the window's thread (scans, walks, hashing, detectors) below that thread's priority,
/// so the window gets a CPU first: one Normal worker per CPU made it wait its turn. Waits that must answer within a bound
/// (<see cref="PathProbe"/>) and benchmarks stay at Normal.
/// </summary>
public static class BackgroundWork
{
    public const ThreadPriority Priority = ThreadPriority.BelowNormal;

    /// <summary>A background thread that starts below normal; the caller starts it.</summary>
    public static Thread Thread(ThreadStart start, string name, int maxStackSize = 0) =>
        new(start, maxStackSize) { IsBackground = true, Name = name, Priority = Priority };

    /// <summary>Lowers the calling thread until disposed, then gives it back the priority it had.</summary>
    public static Lowered Lower() => new(System.Threading.Thread.CurrentThread);

    public static Task Run(Action work, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                using Lowered _ = Lower();
                work();
            },
            cancellationToken);

    public static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                using Lowered _ = Lower();
                return work();
            },
            cancellationToken);

    /// <summary><see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> with every worker lowered, the caller too.</summary>
    public static ParallelLoopResult For(int fromInclusive, int toExclusive, ParallelOptions options, Action<int> body) =>
        Parallel.For(
            fromInclusive,
            toExclusive,
            options,
            Lower,
            (i, _, lowered) =>
            {
                body(i);
                return lowered;
            },
            static lowered => lowered.Dispose());

    /// <summary>
    /// <see cref="Parallel.For{TLocal}(int, int, Func{TLocal}, Func{int, ParallelLoopState, TLocal, TLocal}, Action{TLocal})"/>
    /// with every worker lowered, the caller too.
    /// </summary>
    public static ParallelLoopResult For<TLocal>(
        int fromInclusive,
        int toExclusive,
        Func<TLocal> localInit,
        Func<int, ParallelLoopState, TLocal, TLocal> body,
        Action<TLocal> localFinally) =>
        Parallel.For(
            fromInclusive,
            toExclusive,
            () =>
            {
                Lowered lowered = Lower();
                try
                {
                    return (Lowered: lowered, Local: localInit());
                }
                catch
                {
                    lowered.Dispose();
                    throw;
                }
            },
            (i, state, local) => (local.Lowered, body(i, state, local.Local)),
            local =>
            {
                try
                {
                    localFinally(local.Local);
                }
                finally
                {
                    local.Lowered.Dispose();
                }
            });

    /// <summary><see cref="Parallel.ForEach{TSource}(IEnumerable{TSource}, ParallelOptions, Action{TSource})"/> with every worker lowered, the caller too.</summary>
    public static ParallelLoopResult ForEach<T>(IEnumerable<T> source, ParallelOptions options, Action<T> body) =>
        Parallel.ForEach(
            source,
            options,
            Lower,
            (item, _, lowered) =>
            {
                body(item);
                return lowered;
            },
            static lowered => lowered.Dispose());

    /// <summary>A thread lowered by <see cref="Lower"/>; disposing it on that thread restores its priority.</summary>
    public readonly struct Lowered : IDisposable
    {
        private readonly Thread? _thread;
        private readonly ThreadPriority _previous;

        internal Lowered(Thread thread)
        {
            _thread = thread;
            _previous = thread.Priority;
            if (_previous > Priority)
            {
                thread.Priority = Priority;
            }
        }

        public void Dispose()
        {
            if (_thread is not null && _thread.Priority != _previous)
            {
                _thread.Priority = _previous;
            }
        }
    }
}
