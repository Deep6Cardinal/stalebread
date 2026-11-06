namespace StaleBread.Windows.Desktop;
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    public bool IsFirst { get; }
    public SingleInstance(string scope)
    {
        mutex = new Mutex(true, @"Local\StaleBread-" + scope, out bool isNew);
        IsFirst = isNew;
    }
    public void Dispose() { if (IsFirst) mutex.ReleaseMutex(); mutex.Dispose(); }
}
