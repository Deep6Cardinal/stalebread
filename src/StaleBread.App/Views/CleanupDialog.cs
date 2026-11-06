using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StaleBread.Core.Cleanup;
using StaleBread.Core.Model;
namespace StaleBread.App.Views;

public sealed class CleanupDialog : Window
{
    public CleanupDialog(AppComposition model, Finding finding)
    {
        Title = "Review exact processes — StaleBread"; Width = 680; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text("Review before force-stopping", 24));
        panel.Children.Add(Ui.Text("Close the originating app normally when possible. Forced termination can lose work, unsaved state, output and locks. It cannot be undone. Evidence does not prove disposability."));
        var selections = new List<(ProcessIdentity Id, CheckBox Check)>();
        foreach (var id in finding.Members)
        {
            var name = model.Monitor.State.Graph?.Find(id)?.Sample.Name ?? "Unavailable";
            var check = Ui.Check($"{name} · PID {id.Pid} · created {DateTime.FromFileTimeUtc(id.CreationFileTimeUtc):O}", false);
            selections.Add((id, check)); panel.Children.Add(check);
        }
        var evidence = Ui.Text(string.Join(" · ", finding.ReasonCodes)); panel.Children.Add(evidence);
        var status = Ui.Text("Nothing selected. No background stopping."); panel.Children.Add(status);
        CleanupProposal? proposal = null;
        var cancel = new CancellationTokenSource();
        var actions = new WrapPanel(); panel.Children.Add(actions);
        var confirm = new Button { Content = "Force stop selected (0)", IsEnabled = false };
        actions.Children.Add(Ui.Button("Review selected identities", () =>
        {
            var graph = model.Monitor.State.Graph ?? throw new InvalidOperationException("No current graph");
            proposal = model.Cleanup.Propose(selections.Where(x => x.Check.IsChecked == true).Select(x => x.Id).ToArray(), graph);
            foreach (var item in selections) item.Check.IsEnabled = false;
            confirm.Content = $"Force stop selected ({proposal.Identities.Length})"; confirm.IsEnabled = true;
            status.Text = "Selection frozen. Review expires in 30 seconds. Confirming requests fresh identity and policy checks.";
            return Task.CompletedTask;
        }));
        actions.Children.Add(confirm);
        confirm.Click += async (_, _) =>
        {
            if (proposal is null) return;
            confirm.IsEnabled = false;
            try
            {
                using var prepared = await model.Cleanup.PrepareAsync(proposal, new(proposal.Id, TimeProvider.System.GetTimestamp()), cancel.Token);
                var result = await model.Cleanup.ExecuteAsync(prepared, cancel.Token);
                status.Text = string.Join("\n", result.Processes.Select(p => $"PID {p.Identity.Pid}: {p.Result} — {p.Reason}")) +
                    (result.Partial ? "\nPartial outcome. Remaining processes need a new review." : "") +
                    (!result.AuditRecorded ? "\nAudit could not be completed; further cleanup is blocked." : "");
                model.ObserveAfterCleanup(result);
            }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
            { status.Text = "Nothing further will be stopped. Review failed: " + e.Message; }
            proposal = null;
        };
        actions.Children.Add(Ui.Button("Cancel / close", () => { cancel.Cancel(); Close(); return Task.CompletedTask; }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (proposal is not null && TimeProvider.System.GetTimestamp() > proposal.Expires)
            { confirm.IsEnabled = false; status.Text = "Review expired. Close and open a fresh review."; } };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); cancel.Cancel(); cancel.Dispose(); };
    }
}
