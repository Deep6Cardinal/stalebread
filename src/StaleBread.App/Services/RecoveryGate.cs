namespace StaleBread.App.Services;
public sealed class RecoveryGate(TimeProvider time)
{
    private long? until;
    public bool Recovering => until.HasValue && time.GetTimestamp() < until.Value;
    public void Reset() => until = time.GetTimestamp() + 60 * time.TimestampFrequency;
    public void Observe(bool healthy) { if (!healthy) Reset(); }
}
