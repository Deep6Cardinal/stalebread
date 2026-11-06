using StaleBread.Core.Notifications;
using StaleBread.Core.Model;
namespace StaleBread.App.Services;
public sealed class NotificationService(Func<DesktopState> desktop, Func<Settings> settings,
    Action<NotificationDecision, bool> show, AudioService audio)
{
    public Task DeliverAsync(NotificationDecision decision, CancellationToken ct)
    {
        var d = desktop(); var s = settings();
        if (ct.IsCancellationRequested || !decision.ShowPopup || !s.PopupEnabled || d.Locked || d.Disconnected || d.Notifications != NotificationState.Accepts)
            return Task.CompletedTask;
        show(decision, Animate(true, false, false, d, s));
        return decision.PlaySound && !decision.ShowSilentDigest && s.SoundEnabled ? audio.PlayPopAsync(s.Volume, ct) : Task.CompletedTask;
    }
    public static bool Animate(bool visible, bool paused, bool economy, DesktopState desktop, Settings settings)
        => visible && !paused && !economy && !desktop.Locked && !desktop.Disconnected && !desktop.ReducedMotion && !settings.ReducedMotion;
}
public sealed class PopupLifetime(TimeProvider time)
{
    private long until = time.GetTimestamp() + 10 * time.TimestampFrequency;
    public bool Expired(bool hovered) { if (hovered) until = time.GetTimestamp() + 10 * time.TimestampFrequency; return time.GetTimestamp() >= until; }
}
