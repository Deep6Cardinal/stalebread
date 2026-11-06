namespace StaleBread.Windows.Collection;

/// <summary>One background thread, one outstanding native call; no replacement after logical timeout.</summary>
public sealed class BoundedWorker<T> : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent signal = new(false);
    private Func<T>? operation;
    private TaskCompletionSource<T>? completion;
    private bool disposed;
    public BoundedWorker()
    {
        new Thread(Run) { IsBackground = true, Name = "StaleBread bounded metadata" }.Start();
    }
    public Task<T>? TryStart(Func<T> work)
    {
        lock (gate)
        {
            if (disposed || completion is not null) return null;
            operation = work;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = completion.Task;
            signal.Set();
            return result;
        }
    }
    private void Run()
    {
        try
        {
            while (true)
            {
                signal.WaitOne();
                Func<T>? work;
                TaskCompletionSource<T>? done;
                lock (gate) { if (disposed) return; work = operation; done = completion; }
                if (work is null || done is null) continue;
                T? result = default; Exception? failure = null;
                try { result = work(); } catch (Exception e) { failure = e; }
                // Release the single slot BEFORE publishing completion, so a sequential batch
                // can schedule its next request without racing this worker's bookkeeping.
                lock (gate) { operation = null; completion = null; }
                if (failure is null) done.TrySetResult(result!); else done.TrySetException(failure);
                lock (gate) { if (disposed) return; }
            }
        }
        finally { signal.Dispose(); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; signal.Set();
        }
        // Never join a native WMI call. IsBackground prevents a stuck call holding app exit.
    }
}
