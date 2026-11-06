using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Detection;
using StaleBread.App.ViewModels;
namespace StaleBread.App.Views;

public sealed record FindingRow(Finding Finding)
{
    public string Title => DiagnosticText.Title(Finding);
    public string Summary => DiagnosticText.MeasurementSummary(Finding);
    public string Evidence => string.Join(" · ", Finding.ReasonCodes) + (Finding.IsLowerBound ? " · observed lower bound" : "") +
        "\n" + DiagnosticText.Cleanup(Finding);
}
public sealed record ProcessRow(ProcessIdentity Identity, string Name, int Pid, string Cpu, string Memory, string Role);
public partial class FindingsWindow : Window
{
    private readonly AppComposition model;
    private readonly Dictionary<TimeSpan, Button> trendButtons = [];
    private GraphSnapshot? renderedGraph;
    public Action? OpenSettings;
    public FindingsWindow(AppComposition model) { InitializeComponent(); this.model = model; }
    public void Refresh()
    {
        var state = model.Monitor.State;
        Status.Text = DiagnosticText.MonitorStatus(state.Coverage, state.Paused);
        if (model.DesktopState.Notifications == Core.Notifications.NotificationState.Unknown) Status.Text += " · notification state unknown; intrusive alerts suppressed";
        CountHeading.Text = $"{state.Counts.CurrentCount?.ToString() ?? "—"} monitored processes" + (state.Counts.Stale ? " · stale" : "") + (state.Counts.Partial ? " · limited coverage" : "");
        foreach (var delta in state.Counts.Deltas)
        {
            if (!trendButtons.TryGetValue(delta.Horizon, out var tile))
            {
                tile = new Button { MinWidth = 145 };
                tile.Click += (sender, _) => MessageBox.Show((string)((Button)sender).ToolTip, "Observed process activity");
                trendButtons.Add(delta.Horizon, tile); Trends.Children.Add(tile);
            }
            tile.Content = CountText.Horizon(delta.Horizon) + "   " + delta.Pair; tile.ToolTip = CountText.Details(delta);
            AutomationProperties.SetName(tile, delta.Spoken); AutomationProperties.SetHelpText(tile, CountText.Details(delta));
        }
        Footer.Text = string.Join(" · ", state.Errors.Take(3).Select(DiagnosticText.CollectorMessage)) + "\n" + model.StatusMessage;
        PauseButton.Content = state.Paused ? "_Resume watching" : "_Pause watching";
        if (ReferenceEquals(renderedGraph, state.Graph)) return;
        renderedGraph = state.Graph;
        string? selected = (Findings.SelectedItem as FindingRow)?.Finding.EpisodeKey;
        Findings.ItemsSource = state.Findings.Where(f => f.State == FindingState.Active).Select(f => new FindingRow(f)).ToArray();
        if (selected is not null) Findings.SelectedItem = Findings.Items.Cast<FindingRow>().FirstOrDefault(r => r.Finding.EpisodeKey == selected);
        var selectedProcess = (Processes.SelectedItem as ProcessRow)?.Identity;
        if (state.Graph is { } graph)
        {
            Processes.ItemsSource = graph.Current.Where(kv => ClassifierCatalog.WatchNames.Contains(kv.Value.Sample.Name) && kv.Value.Sample.SessionId == model.SessionId)
                .OrderByDescending(kv => kv.Value.Sample.PrivateBytes).Select(kv => {
                    var p = kv.Value.Sample; var interval = WindowTracker.Intervals(kv.Value, graph.TimestampFrequency).LastOrDefault();
                    return new ProcessRow(kv.Key, p.Name, p.Pid, interval?.CoreEquivalents is { } cpu ? $"{cpu / graph.System.LogicalProcessorCount * 100:F2}%" : "Unavailable",
                        p.PrivateBytes.HasValue ? $"{p.PrivateBytes / 1048576d:F1} MiB" : "Unavailable", graph.Associations[kv.Key].Role.ToString());
                }).ToArray();
            if (selectedProcess.HasValue) Processes.SelectedItem = Processes.Items.Cast<ProcessRow>().FirstOrDefault(r => r.Identity == selectedProcess);
        }
    }
    private async void SymptomClick(object sender, RoutedEventArgs e) { await model.RecordSymptomAsync(); Refresh(); }
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();
    private void PauseClick(object sender, RoutedEventArgs e) => model.Monitor.SetPaused(!model.Monitor.State.Paused);
    private void SnoozeClick(object sender, RoutedEventArgs e) { if (Findings.SelectedItem is FindingRow row) model.Snooze([row.Finding.EpisodeKey]); }
    private void DismissClick(object sender, RoutedEventArgs e) { if (Findings.SelectedItem is FindingRow row) model.Dismiss([row.Finding.EpisodeKey]); }
    private void DetailsClick(object sender, RoutedEventArgs e)
    {
        if (Findings.SelectedItem is FindingRow row) Ui.TextWindow("StaleBread — evidence", row.Summary + "\n" + row.Evidence + "\n\n" +
            DiagnosticText.MeasurementDetails(row.Finding) + "\n\nCurrent process details (may differ from the trigger observation):\n\n" +
            string.Join("\n\n", row.Finding.Members.Select(Describe)), this).Show();
    }
    private void ProcessDetailsClick(object sender, RoutedEventArgs e) { if (Processes.SelectedItem is ProcessRow row) Ui.TextWindow("Process evidence", Describe(row.Identity), this).Show(); }
    private string Describe(ProcessIdentity id)
    {
        var graph = model.Monitor.State.Graph;
        if (graph?.Find(id) is not { } n) return "Process is no longer observed.";
        var p = n.Sample; var a = graph.Associations[id]; var eligibility = model.Policy.Evaluate(id, graph, model.Settings);
        var lineage = new List<string>(); var cursor = id; var seen = new HashSet<ProcessIdentity>();
        while (seen.Add(cursor) && graph.Find(cursor) is { } node && lineage.Count < 64)
        { lineage.Add($"{node.Sample.Name} [{cursor.Pid}] — {(node.ExitedAt.HasValue ? "exit observed" : "not confirmed exited")}"); if (node.Parent is not { } parent) break; cursor = parent; }
        return $"{p.Name} · PID {id.Pid}\nCreated UTC: {DateTime.FromFileTimeUtc(id.CreationFileTimeUtc):O}\nPath: {p.CanonicalImagePath ?? "Unavailable"}\n" +
            $"Owner SID: {p.OwnerSid ?? "Unavailable"} · session: {p.SessionId?.ToString() ?? "Unavailable"}\nRole: {a.Role}; owner kind: {a.OwnerKind}; protected: {a.IsProtected}\n" +
            $"Window visible: {p.HasVisibleWindow?.ToString() ?? "Unavailable"}; launch mode: {p.LaunchMode}\nPrivate: {p.PrivateBytes?.ToString() ?? "Unavailable"} bytes; working set: {p.WorkingSetBytes?.ToString() ?? "Unavailable"} bytes; handles: {p.HandleCount?.ToString() ?? "Unavailable"}\n" +
            $"Cleanup: {(eligibility.Eligible ? "Manual review eligible, not a safety guarantee" : string.Join(", ", eligibility.ReasonCodes))}\nLineage:\n" + string.Join("\n  ← ", lineage);
    }
    private void CleanupClick(object sender, RoutedEventArgs e)
    {
        if (Findings.SelectedItem is not FindingRow row) { MessageBox.Show("Select a finding first.", "StaleBread"); return; }
        if (!row.Finding.CleanupEligible) { MessageBox.Show(DiagnosticText.Cleanup(row.Finding), "StaleBread — why cleanup is unavailable"); return; }
        new CleanupDialog(model, row.Finding) { Owner = this }.ShowDialog();
    }
    private async void KeepClick(object sender, RoutedEventArgs e)
    {
        if (Processes.SelectedItem is not ProcessRow row || model.Monitor.State.Graph?.Find(row.Identity)?.Sample.CanonicalImagePath is not { } path) return;
        if (MessageBox.Show("Keep every process at this executable path? For an interpreter this excludes all of its instances.\n\n" + path,
            "Keep this app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await model.KeepAppAsync(path); } catch (Exception ex) { MessageBox.Show(ex.Message, "StaleBread"); }
    }
    private async void IgnoreClick(object sender, RoutedEventArgs e)
    {
        if (Processes.SelectedItem is not ProcessRow row) return;
        try { await model.IgnoreProcessAsync(row.Identity); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "StaleBread"); }
    }
    private async void HistoryClick(object sender, RoutedEventArgs e)
    {
        var entries = (await model.History.ReadAsync()).TakeLast(200).Reverse().ToArray();
        History.Text = entries.Length == 0 ? "No recent history available." : string.Join("\n\n", entries.Select(DiagnosticText.History));
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        var exported = await model.ExportAsync();
        var panel = new DockPanel { Margin = new Thickness(16) };
        var controls = new WrapPanel();
        panel.Children.Add(controls); DockPanel.SetDock(controls, Dock.Bottom);
        var preview = new Window { Title = "Preview sanitized diagnostics", Width = 800, Height = 600, Owner = this, Content = panel };
        controls.Children.Add(Ui.Button("Save JSON…", async () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "StaleBread-diagnostics.json", Filter = "JSON|*.json" }; if (dialog.ShowDialog(preview) == true) await File.WriteAllTextAsync(dialog.FileName, exported.Json); }));
        controls.Children.Add(Ui.Button("Save CSV…", async () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "StaleBread-diagnostics.csv", Filter = "CSV|*.csv" }; if (dialog.ShowDialog(preview) == true) await File.WriteAllTextAsync(dialog.FileName, exported.Csv); }));
        panel.Children.Add(new TextBox { Text = exported.RedactionSummary + "\n\n" + exported.Json, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        preview.Show();
    }
}
