using Microsoft.Win32;
using StaleBread.Core.Storage;
namespace StaleBread.Windows.Desktop;

public sealed class StartupRegistration(string executable, Func<string?>? read = null, Action<string?>? write = null)
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read() => read is not null ? read() : Registry.GetValue(@"HKEY_CURRENT_USER\" + Key, "StaleBread", null) as string;
    public bool IsStale => Read() is { } value && value != Command(executable);
    public static string Command(string executable)
    {
        if (executable.Contains('"') || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Startup requires an absolute executable path");
        return $"\"{executable}\" --minimized";
    }
    public StorageResult SetEnabled(bool enabled)
    {
        try
        {
            string? value = enabled ? Command(executable) : null;
            if (write is not null) write(value);
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                if (value is null) key.DeleteValue("StaleBread", false); else key.SetValue("StaleBread", value);
            }
            return new(true);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
        { return new(false, "Startup setting not changed: " + e.GetType().Name); }
    }
}
