using StaleBread.Core.Graph;
using StaleBread.Core.Model;

namespace StaleBread.Core.Cleanup;

[Flags] public enum ProcessAccess { Query = 1, Terminate = 2, Synchronize = 4 }
public interface IProcessHandleFactory
{
    IValidatedProcessHandle Open(ProcessIdentity identity, ProcessAccess access);
}
public interface IValidatedProcessHandle : IDisposable
{
    ProcessIdentity Identity { get; }
    string? OwnerSid { get; }
    int? SessionId { get; }
    bool IsAlive { get; }
    void Terminate();
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
}
public interface IFreshSnapshotSource { Task<GraphSnapshot> GetAsync(CancellationToken ct); }
