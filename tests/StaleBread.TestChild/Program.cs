// Disposable, bounded test workload; never distributed with the consumer application.
using System.Diagnostics;

if (args.Length < 3 || !Guid.TryParse(args[0], out _) || !int.TryParse(args[2], out int lifetime) ||
    lifetime < 100 || lifetime > 10000) return 2;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
string mode = args[1];
var elapsed = Stopwatch.StartNew();
switch (mode)
{
    case "idle": await Task.Delay(lifetime, deadline.Token); break;
    case "cpu":
        while (elapsed.ElapsedMilliseconds < lifetime) Thread.SpinWait(1000);
        break;
    case "allocate":
        var bytes = new byte[32 * 1024 * 1024];
        for (int i = 0; i < bytes.Length; i += 4096) bytes[i] = 1;
        await Task.Delay(lifetime, deadline.Token);
        GC.KeepAlive(bytes);
        break;
    case "spawn":
    case "exit-parent":
        for (int i = 0; i < (mode == "spawn" ? 3 : 1); i++)
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { args[0], "idle", lifetime.ToString() }
            });
            if (mode == "spawn" && child is not null) await child.WaitForExitAsync(deadline.Token);
        }
        break;
    default: return 2;
}
return 0;
