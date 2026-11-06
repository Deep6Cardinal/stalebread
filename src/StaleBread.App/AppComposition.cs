using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using StaleBread.Core.Cleanup;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Notifications;
using StaleBread.Core.Storage;
using StaleBread.Core.Trends;
using StaleBread.App.Services;
using StaleBread.Windows.Collection;
using StaleBread.Windows.Desktop;
using StaleBread.Windows.Native;
using StaleBread.Windows.Storage;
namespace StaleBread.App;

public sealed class AppComposition : IAsyncDisposable
{
    private readonly LocalDataPaths paths = new();
    private readonly Dictionary<string, FindingState> recorded = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly NotificationMemoryWriter notificationWriter;
    private SettingsController settingsController;
    public Settings Settings => settingsController.Current;
    public SettingsStore SettingsStore { get; }
    public HistoryStore History { get; }
    public MonitorService Monitor { get; }
    public CleanupCoordinator Cleanup { get; }
    public CleanupPolicy Policy { get; }
    public NotificationPolicy Notifications { get; } = new();
    public DesktopStateProvider Desktop { get; }
    public StartupRegistration Startup { get; }
    public DesktopState DesktopState { get; private set; } = new(Notifications: NotificationState.Unknown);
    public int SessionId { get; }
    public string StatusMessage { get; private set; } = "";
    public event Action<NotificationDecision>? NotificationRequested;
    public event Action? PreviewRequested;
    private long quietUntil;
    public AppComposition(string sid)
    {
        settingsController = new(new(), PersistSettingsAsync);
        using var self = Process.GetCurrentProcess(); SessionId = self.SessionId;
        var identity = new ProcessIdentity(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc());
        var selfIds = new HashSet<ProcessIdentity> { identity };
        SettingsStore = new(paths.Settings); History = new(paths.History, TimeProvider.System);
        notificationWriter = new(() => Notifications.Export(), (memory, ct) =>
            AtomicFile.WriteAsync(paths.Notifications, JsonSerializer.Serialize(memory), ct));
        Policy = new(sid, SessionId);
        var classifier = new AssociationClassifier(new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), sid, SessionId, selfIds);
        Monitor = new(new WindowsProcessCollector(TimeProvider.System, SessionId), TimeProvider.System, classifier,
            new CountScope(SessionId, "1", selfIds), Policy, () => Settings);
        Cleanup = new(new ProcessHandleFactory(), Monitor, Policy, () => Settings, TimeProvider.System,
            new CleanupAuditWriter(History, TimeProvider.System));
        Desktop = new(SessionId); Startup = new(Environment.ProcessPath!);
    }
    public async Task StartAsync()
    {
        var loaded = await SettingsStore.LoadAsync(lifetime.Token); settingsController = new(loaded.Settings, PersistSettingsAsync);
        StatusMessage = loaded.Error ?? "";
        if (Startup.IsStale) StatusMessage = "Start-at-sign-in points to a previous location. Review Settings.";
        try
        {
            if (File.Exists(paths.Notifications) && new FileInfo(paths.Notifications).Length <= 1024 * 1024 &&
                JsonSerializer.Deserialize<NotificationMemory>(await File.ReadAllTextAsync(paths.Notifications, lifetime.Token)) is { } memory)
                Notifications.Restore(memory, TimeProvider.System);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { StatusMessage = "Notification history unavailable; no process authorization was restored."; }
        await Monitor.StartAsync(lifetime.Token);
    }
    public void Tick()
    {
        DesktopState = Desktop.Read();
        if (TimeProvider.System.GetTimestamp() < quietUntil)
            DesktopState = DesktopState with { Notifications = NotificationState.Suppressed };
        var state = Monitor.State;
        if (!state.Paused && state.Coverage != Coverage.Degraded)
        {
            long revision = Notifications.Revision;
            var decision = Notifications.Evaluate(state.Findings, new(TimeProvider.System, TimeZoneInfo.Local, DesktopState, Settings, state.Graph, state.Cadence));
            if (decision.ShowPopup) NotificationRequested?.Invoke(decision);
            if (Notifications.Revision != revision) _ = SaveNotificationsAsync();
        }
        foreach (var finding in state.Findings)
            if (!recorded.TryGetValue(finding.EpisodeKey, out var status) || status != finding.State)
            {
                recorded[finding.EpisodeKey] = finding.State;
                _ = RecordAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Finding, finding.Kind.ToString(), finding));
            }
        if (recorded.Count > 2000) recorded.Clear();
    }
    public Task<StorageResult> SaveSettingsAsync(Settings value, Settings? expected = null) => settingsController.UpdateAsync(_ => value, expected);
    private async Task<StorageResult> PersistSettingsAsync(Settings previous, Settings value)
    {
        var result = await SettingsStore.SaveAsync(value, lifetime.Token);
        if (!result.Success) { StatusMessage = result.Error ?? "Settings not saved"; return result; }
        if (value.StartAtSignIn != previous.StartAtSignIn || value.StartAtSignIn && Startup.IsStale)
        {
            var startup = Startup.SetEnabled(value.StartAtSignIn);
            if (!startup.Success)
            {
                var rollback = await SettingsStore.SaveAsync(previous, lifetime.Token);
                StatusMessage = startup.Error + (rollback.Success ? "; settings rolled back." : "; settings rollback failed.");
                return new(false, StatusMessage);
            }
        }
        StatusMessage = "Settings saved";
        return result;
    }
    public async Task KeepAppAsync(string path)
    {
        var result = await settingsController.UpdateAsync(s => s with { ExcludedPaths = s.ExcludedPaths.Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
        if (!result.Success) throw new IOException(result.Error);
    }
    public async Task IgnoreProcessAsync(ProcessIdentity id)
    {
        var current = Monitor.State.Graph;
        var result = await settingsController.UpdateAsync(s => s with { IgnoredProcesses = s.IgnoredProcesses
            .Where(i => current?.Current.ContainsKey(i) == true).Append(id).Distinct().ToArray() });
        if (!result.Success) throw new IOException(result.Error);
    }
    public async Task RecordSymptomAsync()
    { await RecordAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Symptom, "Typing feels slow")); StatusMessage = "Symptom timestamp recorded — no keystrokes or chat content collected."; }
    private async Task RecordAsync(HistoryEntry entry)
    {
        var result = await History.AppendAsync(entry, lifetime.Token);
        if (!result.Success) StatusMessage = result.Error ?? "History not saved";
    }
    public void Snooze(IEnumerable<string> keys) { foreach (var key in keys) Notifications.Snooze(key, TimeProvider.System); _ = SaveNotificationsAsync(); }
    public void Dismiss(IEnumerable<string> keys) { foreach (var key in keys) Notifications.Dismiss(key); _ = SaveNotificationsAsync(); }
    public void QuietHour() { quietUntil = TimeProvider.System.GetTimestamp() + 3600 * TimeProvider.System.TimestampFrequency; StatusMessage = "Quiet for one hour"; }
    public void Preview() => PreviewRequested?.Invoke();
    private async Task SaveNotificationsAsync()
    {
        try { await notificationWriter.SaveAsync(lifetime.Token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        { StatusMessage = "Notification preferences could not be saved"; }
    }
    public void ObserveAfterCleanup(CleanupOutcome result) => _ = new PostCleanupObservations(Monitor, TimeProvider.System)
        .RunAsync(result, value => { _ = RecordAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Action,
            $"Post-action observation at {value.RequestedSeconds}s: {value.Explanation}")); }, lifetime.Token);
    public async Task<SanitizedExport> ExportAsync() => DiagnosticExporter.Export(new(Settings, await History.ReadAsync(lifetime.Token),
        Monitor.State.Coverage, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName));
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { await Monitor.StopAsync(); } catch (TimeoutException) { }
        try { await History.DisposeAsync(); } catch (TimeoutException) { }
        lifetime.Dispose();
    }
}
