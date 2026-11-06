namespace StaleBread.Windows.Storage;

public sealed class LocalDataPaths
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StaleBread");
    public string Settings => Path.Combine(Root, "settings.json");
    public string History => Path.Combine(Root, "history.json");
    public string Notifications => Path.Combine(Root, "notifications.json");
}
