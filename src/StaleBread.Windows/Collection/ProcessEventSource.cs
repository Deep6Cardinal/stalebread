using System.Management;
using System.Security.Principal;
using StaleBread.Core.Model;

namespace StaleBread.Windows.Collection;

public sealed record EventBatch(IReadOnlyList<ProcessEvent> Events, bool Loss, IReadOnlyList<string> Gaps);
public sealed class ProcessEventSource(TimeProvider time) : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<ProcessEvent> queue = new();
    private ManagementEventWatcher? starts, stops;
    private bool loss, attempted, disposed;
    private volatile bool available;
    public bool Available => available;
    public string? Failure { get; private set; }
    public void Start()
    {
        lock (gate) { if (attempted || disposed) return; attempted = true; }
        new Thread(() =>
        {
            ManagementEventWatcher? a = null, b = null;
            try
            {
                a = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                b = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                a.Options.Timeout = b.Options.Timeout = TimeSpan.FromSeconds(2);
                a.EventArrived += (_, e) => Capture(e.NewEvent, true);
                b.EventArrived += (_, e) => Capture(e.NewEvent, false);
                a.Stopped += (_, _) => { available = false; Failure = "Process trace stopped"; };
                b.Stopped += (_, _) => { available = false; Failure = "Process trace stopped"; };
                a.Start(); b.Start();
                lock (gate)
                {
                    if (!disposed) { starts = a; stops = b; available = true; a = b = null; }
                }
            }
            catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { Failure = "Process trace unavailable: " + (e is ManagementException m ? m.ErrorCode.ToString() : e.GetType().Name); }
            finally { a?.Dispose(); b?.Dispose(); }
        }) { IsBackground = true, Name = "StaleBread trace startup" }.Start();
    }
    private void Capture(ManagementBaseObject entry, bool start)
    {
        try
        {
            string? sid = entry["Sid"] is byte[] bytes ? new SecurityIdentifier(bytes, 0).Value : null;
            Enqueue(new(Convert.ToInt32(entry["ProcessID"]), Convert.ToInt32(entry["ParentProcessID"]),
                Convert.ToString(entry["ProcessName"]) ?? "", start, time.GetTimestamp(),
                Convert.ToInt64(entry["TIME_CREATED"]), sid, Convert.ToInt32(entry["SessionID"]), null, null));
        }
        catch (Exception e) when (e is ManagementException or ArgumentException or OverflowException)
        { lock (gate) loss = true; }
        finally { entry.Dispose(); }
    }
    public void Enqueue(ProcessEvent entry)
    {
        lock (gate)
        {
            if (disposed) return;
            if (queue.Count == 4096) { loss = true; return; }
            queue.Enqueue(entry);
        }
    }
    public EventBatch Drain()
    {
        lock (gate)
        {
            var result = new EventBatch(queue.ToArray(), loss, loss ? ["Process event queue overflow"] : []);
            queue.Clear(); loss = false; return result;
        }
    }
    public void Dispose()
    {
        ManagementEventWatcher? a, b;
        lock (gate) { if (disposed) return; disposed = true; available = false; a = starts; b = stops; starts = stops = null; queue.Clear(); }
        // COM watcher shutdown is isolated so a hung provider cannot freeze the UI or exit.
        new Thread(() => { try { a?.Dispose(); b?.Dispose(); } catch (Exception) { /* shutdown only */ } })
            { IsBackground = true, Name = "StaleBread trace shutdown" }.Start();
    }
}
