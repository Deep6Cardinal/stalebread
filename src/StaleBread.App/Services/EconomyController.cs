namespace StaleBread.App.Services;
public sealed class EconomyController(TimeProvider time)
{
    private long? previous, highSince, lowSince, entered;
    private TimeSpan lastCpu;
    public bool Economy { get; private set; }
    public bool Observe(TimeSpan cpu, int processors)
    {
        long now = time.GetTimestamp();
        if (!previous.HasValue) { previous = now; lastCpu = cpu; return false; }
        double seconds = time.GetElapsedTime(previous.Value, now).TotalSeconds;
        double percent = seconds > 0 && cpu >= lastCpu ? (cpu - lastCpu).TotalSeconds / seconds / Math.Max(1, processors) * 100 : double.NaN;
        previous = now; lastCpu = cpu;
        if (percent > 2) { highSince ??= now; lowSince = null; }
        else if (percent < 1) { lowSince ??= now; highSince = null; }
        else { highSince = lowSince = null; }
        if (!Economy && highSince.HasValue && time.GetElapsedTime(highSince.Value, now).TotalSeconds >= 60)
        { Economy = true; entered = now; return true; }
        if (Economy && entered.HasValue && lowSince.HasValue &&
            time.GetElapsedTime(entered.Value, now).TotalSeconds >= 300 && time.GetElapsedTime(lowSince.Value, now).TotalSeconds >= 60)
        { Economy = false; highSince = lowSince = null; return true; }
        return false;
    }
}
