using StaleBread.App.Services;
namespace StaleBread.Windows.Tests;
public class RecoveryTests
{
    private sealed class Clock : TimeProvider
    { public int Seconds; public override long TimestampFrequency => 1; public override long GetTimestamp() => Seconds; }
    [Fact] public void UnhealthyObservationRestartsFullSixtySecondRecovery()
    {
        var clock = new Clock(); var gate = new RecoveryGate(clock);
        Assert.False(gate.Recovering); gate.Reset();
        clock.Seconds = 59; gate.Observe(false);
        clock.Seconds = 60; gate.Observe(true); Assert.True(gate.Recovering);
        clock.Seconds = 118; Assert.True(gate.Recovering);
        clock.Seconds = 119; gate.Observe(true); Assert.False(gate.Recovering);
    }
}
