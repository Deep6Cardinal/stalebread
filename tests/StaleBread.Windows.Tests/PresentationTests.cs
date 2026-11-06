using StaleBread.App.Services;
using StaleBread.Core.Notifications;
using StaleBread.Core.Model;
using StaleBread.Windows.Audio;
namespace StaleBread.Windows.Tests;
public class PresentationTests
{
    private sealed class NeverAudio : IWaveOutput { public Task PlayAsync(PcmWave wave, CancellationToken ct) => throw new Exception("must not play"); }
    [Theory] [InlineData(true, false, NotificationState.Accepts)] [InlineData(false, true, NotificationState.Accepts)]
    [InlineData(false, false, NotificationState.Unknown)] [InlineData(false, false, NotificationState.Suppressed)]
    public async Task DeliveryRechecksSuppressionBeforeCreatingPopup(bool locked, bool disconnected, NotificationState status)
    {
        using var audio = new AudioService(new NeverAudio(), () => throw new Exception("must not load"));
        int popups = 0; var service = new NotificationService(() => new(locked, disconnected, status), () => new(), (_, _) => popups++, audio);
        await service.DeliverAsync(new([], true, true, true, false, []), default);
        Assert.Equal(0, popups); Assert.Null(audio.Error);
    }
    [Fact] public async Task SilentDigestShowsStaticEquivalentAndDoesNotPlayAudio()
    {
        using var audio = new AudioService(new NeverAudio(), () => throw new Exception("must not load"));
        bool? animate = null; var service = new NotificationService(() => new(), () => new() { ReducedMotion = true }, (_, a) => animate = a, audio);
        await service.DeliverAsync(new([], true, false, true, true, []), default);
        Assert.False(animate); Assert.Null(audio.Error);
    }
    [Fact] public void HiddenPausedEconomyLockedAndReducedMotionStopAnimation()
    {
        Assert.True(NotificationService.Animate(true, false, false, new(), new()));
        Assert.False(NotificationService.Animate(false, false, false, new(), new()));
        Assert.False(NotificationService.Animate(true, true, false, new(), new()));
        Assert.False(NotificationService.Animate(true, false, true, new(), new()));
        Assert.False(NotificationService.Animate(true, false, false, new(Locked: true), new()));
        Assert.False(NotificationService.Animate(true, false, false, new(ReducedMotion: true), new()));
    }
    private sealed class Clock : TimeProvider
    { public int Seconds; public override long TimestampFrequency => 1; public override long GetTimestamp() => Seconds; }
    [Fact] public void HoverExtendsTenSecondLifetime()
    {
        var c = new Clock(); var life = new PopupLifetime(c);
        c.Seconds = 9; Assert.False(life.Expired(false));
        c.Seconds = 10; Assert.False(life.Expired(true));
        c.Seconds = 19; Assert.False(life.Expired(false));
        c.Seconds = 20; Assert.True(life.Expired(false));
    }
}
