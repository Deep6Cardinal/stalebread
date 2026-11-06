using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
namespace StaleBread.App.Views;

internal static class Ui
{
    public static TextBlock Text(string value, double size = 14) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(3, 4, 3, 4) };
    public static Button Button(string title, Func<Task> action)
    {
        var b = new Button { Content = title };
        b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception ex) { MessageBox.Show("Action unavailable: " + ex.Message, "StaleBread"); } finally { b.IsEnabled = true; } };
        return b;
    }
    public static CheckBox Check(string label, bool value)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(3, 7, 3, 7) };
        AutomationProperties.SetName(c, label); return c;
    }
    public static TextBox Field(Panel panel, string label, string value)
    {
        panel.Children.Add(Text(label));
        var box = new TextBox { Text = value, Margin = new Thickness(3), Padding = new Thickness(6) };
        AutomationProperties.SetName(box, label); panel.Children.Add(box); return box;
    }
    public static Window TextWindow(string title, string contents, Window? owner = null)
        => new() { Title = title, Width = 760, Height = 560, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = contents, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(16) } };
}
