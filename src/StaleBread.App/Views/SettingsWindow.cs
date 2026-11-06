using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StaleBread.Core.Model;
namespace StaleBread.App.Views;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppComposition model)
    {
        Title = "StaleBread — settings"; Width = 540; Height = 700; MinWidth = 420;
        var s = model.Settings; var body = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(Ui.Text("Make yourself at home", 24));
        body.Children.Add(Ui.Text("Watching never stops processes automatically. Identity and active-session protections cannot be disabled."));
        var sound = Ui.Check("Enable toaster sound", s.SoundEnabled); body.Children.Add(sound);
        body.Children.Add(Ui.Text("App sound volume"));
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = s.Volume, TickFrequency = .1, IsSnapToTickEnabled = true, Margin = new Thickness(5, 8, 5, 8) };
        System.Windows.Automation.AutomationProperties.SetName(volume, "Toaster sound volume"); body.Children.Add(volume);
        var popup = Ui.Check("Show notification popups", s.PopupEnabled); body.Children.Add(popup);
        var motion = Ui.Check("Reduce motion (also honors Windows)", s.ReducedMotion); body.Children.Add(motion);
        var companion = Ui.Check("Show companion on launch", s.ShowCompanion); body.Children.Add(companion);
        var startup = Ui.Check("Start at sign-in (off by default)", s.StartAtSignIn); body.Children.Add(startup);
        if (model.Startup.IsStale) body.Children.Add(Ui.Text("The saved startup path is stale. Saving with start-at-sign-in checked updates it to this executable."));
        var quiet = Ui.Check("Use quiet hours in local time", s.QuietHoursEnabled); body.Children.Add(quiet);
        var start = Ui.Field(body, "Quiet hours start (HH:mm)", s.QuietStart.ToString("HH:mm"));
        var end = Ui.Field(body, "Quiet hours end (HH:mm)", s.QuietEnd.ToString("HH:mm"));
        body.Children.Add(Ui.Text("Advanced advisory thresholds", 18));
        var cores = Ui.Field(body, "Busy CPU: core equivalents", s.AdvisoryRules.BusyCores.ToString(CultureInfo.InvariantCulture));
        var duration = Ui.Field(body, "Busy CPU duration: 60–600 seconds", s.AdvisoryRules.BusyDuration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        body.Children.Add(Ui.Text("Sampling uses native snapshots every 5 seconds, or 15 seconds in economy mode. Short-lived processes may be missed. Long count trends reuse those samples."));
        var exclusions = Ui.Field(body, "Kept executable paths (one absolute path per line)", string.Join(Environment.NewLine, s.ExcludedPaths));
        exclusions.AcceptsReturn = true; exclusions.Height = 90; exclusions.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var status = Ui.Text(""); body.Children.Add(status);
        var buttons = new WrapPanel(); body.Children.Add(buttons);
        buttons.Children.Add(Ui.Button("Save", async () =>
        {
            if (!TimeOnly.TryParseExact(start.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !TimeOnly.TryParseExact(end.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ||
                !double.TryParse(cores.Text, CultureInfo.InvariantCulture, out var cpu) ||
                !double.TryParse(duration.Text, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds))
            { status.Text = "Use valid clock times and numbers."; return; }
            var next = s with { SoundEnabled = sound.IsChecked == true, Volume = volume.Value, PopupEnabled = popup.IsChecked == true,
                ReducedMotion = motion.IsChecked == true, ShowCompanion = companion.IsChecked == true, StartAtSignIn = startup.IsChecked == true,
                QuietHoursEnabled = quiet.IsChecked == true, QuietStart = from, QuietEnd = to,
                ExcludedPaths = exclusions.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                AdvisoryRules = s.AdvisoryRules with { BusyCores = cpu, BusyDuration = TimeSpan.FromSeconds(seconds) } };
            var result = await model.SaveSettingsAsync(next, s); status.Text = result.Success ? "Settings saved" : result.Error;
            if (result.Success) s = model.Settings;
        }));
        buttons.Children.Add(Ui.Button("Test notification", () => { model.Preview(); return Task.CompletedTask; }));
        buttons.Children.Add(Ui.Button("Reset defaults", async () =>
        {
            var result = await model.SaveSettingsAsync(new Settings(), s);
            status.Text = result.Success ? "Defaults restored. Reopen Settings to edit." : result.Error;
        }));
        body.Children.Add(Ui.Text("StaleBread local preview · no telemetry, chat reading or runtime AI. Audio and third-party credits are included with the executable.", 12));
    }
}
