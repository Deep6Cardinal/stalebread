using System.Windows;
using StaleBread.App.ViewModels;
namespace StaleBread.App.Views;

public partial class CompanionWindow : Window
{
    private readonly AppComposition model;
    private readonly WindowLifetime lifetime = new();
    public Action? OpenFindings, OpenSettings, ExplainClose;
    public CompanionWindow(AppComposition model)
    {
        InitializeComponent(); this.model = model;
        Closing += (_, e) => { var decision = lifetime.Close(); if (decision.HideToTray) { e.Cancel = true; Hide(); if (decision.Explain) ExplainClose?.Invoke(); } };
    }
    public void Exit() { lifetime.ExitRequested = true; Close(); }
    public void Refresh()
    {
        var state = model.Monitor.State;
        Mascot.SetWatching(Services.NotificationService.Animate(IsVisible, state.Paused, state.EconomyMode, model.DesktopState, model.Settings));
        int findings = state.Findings.Count(f => f.State == Core.Model.FindingState.Active);
        Status.Text = state.Paused ? "Watching paused" : state.Coverage == Core.Model.Coverage.Degraded ? "Limited visibility" :
            findings > 0 ? "Breadcrumbs found" : "Watching for breadcrumbs…";
        Counts.Text = CountText.Compact(state.Counts);
        Detail.Text = state.EconomyMode ? "Economy mode · 15-second samples" :
            state.Counts.Partial ? "Limited native count coverage" :
            state.LastSuccessfulUtc is { } last ? $"Sampled {Math.Max(0, (DateTimeOffset.UtcNow - last).TotalSeconds):F0}s ago · appeared / disappeared" : "Starting collector…";
        PauseButton.Content = state.Paused ? "_Resume" : "_Pause";
    }
    private void FindingsClick(object sender, RoutedEventArgs e) => OpenFindings?.Invoke();
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();
    private void PauseClick(object sender, RoutedEventArgs e) => model.Monitor.SetPaused(!model.Monitor.State.Paused);
}
