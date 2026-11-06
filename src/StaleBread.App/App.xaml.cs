using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StaleBread.App.Services;
using StaleBread.App.Views;
using StaleBread.Windows.Desktop;
namespace StaleBread.App;

public partial class App : Application
{
    private SingleInstance? instance;
    private AppComposition? composition;
    private TrayService? tray;
    private CompanionWindow? companion;
    private FindingsWindow? findings;
    private SettingsWindow? settings;
    private DispatcherTimer? timer;
    private bool exiting;
    private AudioService? audio;
    private NotificationService? delivery;
    private ToastPopup? popup;
    private string? trayState;
    private readonly Dictionary<string, System.Drawing.Icon> icons = [];
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new InvalidOperationException("Current user identity unavailable");
            instance = new(sid);
            if (!instance.IsFirst) { MessageBox.Show("StaleBread is already watching. Open it from the system tray.", "StaleBread"); Shutdown(); return; }
            composition = new(sid); await composition.StartAsync();
            audio = new(new WaveOutOutput(new WinMmDevice()), () => typeof(App).Assembly.GetManifestResourceStream("StaleBread.Assets.toaster-pop.wav")
                ?? throw new IOException("Bundled toaster recording unavailable"));
            delivery = new(() => composition.DesktopState, () => composition.Settings, ShowPopup, audio);
            composition.NotificationRequested += decision => _ = delivery.DeliverAsync(decision, default);
            composition.PreviewRequested += () => _ = delivery.DeliverAsync(new([], true, true, false, false, ["ExplicitPreview"]), default);
            companion = new(composition) { OpenFindings = ShowFindings, OpenSettings = ShowSettings };
            tray = new(ShowFindings, ShowCompanion, ShowSettings, () => composition.Monitor.SetPaused(!composition.Monitor.State.Paused),
                composition.QuietHour, () => _ = ExitAsync());
            companion.ExplainClose = tray.ExplainClose;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                composition.Tick(); companion.Refresh(); if (findings?.IsVisible == true) findings.Refresh();
                var state = composition.Monitor.State;
                if (composition.DesktopState.Locked || composition.DesktopState.Disconnected ||
                    composition.DesktopState.Notifications != Core.Notifications.NotificationState.Accepts || state.Paused)
                { popup?.Close(); audio.Stop(); }
                string iconState = state.Paused ? "paused" : state.Coverage == Core.Model.Coverage.Degraded ? "limited" :
                    state.Findings.Any(f => f.State == Core.Model.FindingState.Active) ? "found" : "watching";
                if (trayState != iconState)
                {
                    if (!icons.TryGetValue(iconState, out var icon)) icons[iconState] = icon = ToastIcons.Create(iconState);
                    tray.SetIcon(icon); trayState = iconState;
                }
                tray.Update("StaleBread — " + (state.Paused ? "paused" : state.Coverage == Core.Model.Coverage.Degraded ? "limited visibility" :
                    $"{state.Findings.Count(f => f.State == Core.Model.FindingState.Active)} findings"), state.Paused);
            };
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionEnding += OnSystemSessionEnding;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            timer.Start();
            if (!e.Args.Contains("--minimized") && composition.Settings.ShowCompanion) companion.Show();
        }
        catch (Exception ex) { MessageBox.Show("StaleBread could not start: " + ex.Message, "StaleBread"); await ExitAsync(); }
    }
    private void ShowCompanion() { companion?.Show(); companion?.Activate(); }
    private void ShowPopup(Core.Notifications.NotificationDecision decision, bool animate)
    {
        if (composition is null) return;
        popup?.Close();
        bool preview = decision.ReasonCodes.Contains("ExplicitPreview");
        string text = preview ? "Test notification — no process action" : decision.ShowSilentDigest ?
            $"While you were away: {decision.EpisodeIds.Count} findings to review" : $"Breadcrumbs found — {decision.EpisodeIds.Count} findings to review";
        popup = new(text, animate && !composition.Monitor.State.EconomyMode, ShowFindings,
            () => composition.Snooze(decision.EpisodeIds), () => composition.Dismiss(decision.EpisodeIds));
        popup.Show();
    }
    private void ShowFindings()
    {
        if (composition is null) return;
        if (findings is null) { findings = new(composition) { OpenSettings = ShowSettings }; findings.Closed += (_, _) => findings = null; }
        findings.Refresh(); findings.Show(); findings.Activate();
    }
    private void ShowSettings()
    {
        if (composition is null) return;
        if (settings is null) { settings = new(composition); settings.Closed += (_, _) => settings = null; }
        settings.Show(); settings.Activate();
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) composition?.Monitor.SetSystemSuspended(true);
        else if (e.Mode == PowerModes.Resume) composition?.Monitor.SetSystemSuspended(false);
    });
    private void OnSystemSessionEnding(object sender, SessionEndingEventArgs e) => Dispatcher.BeginInvoke(() => _ = ExitAsync());
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        foreach (Window window in Windows)
            if (window.Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth ||
                window.Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight ||
                window.Left + window.Width < SystemParameters.VirtualScreenLeft || window.Top + window.Height < SystemParameters.VirtualScreenTop)
            { window.Left = SystemParameters.WorkArea.Left + 20; window.Top = SystemParameters.WorkArea.Top + 20; }
    });
    public async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        timer?.Stop(); tray?.Dispose();
        popup?.Close(); audio?.Dispose(); foreach (var icon in icons.Values) icon.Dispose(); icons.Clear();
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionEnding -= OnSystemSessionEnding; SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        if (composition is not null) await composition.DisposeAsync();
        companion?.Exit(); instance?.Dispose(); instance = null;
        Shutdown();
    }
}
