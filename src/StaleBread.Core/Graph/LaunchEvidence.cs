using StaleBread.Core.Model;

namespace StaleBread.Core.Graph;

public static class LaunchEvidence
{
    public static LaunchMode Parse(string imageName, IReadOnlyList<string> arguments)
    {
        var name = imageName.ToLowerInvariant();
        var args = arguments.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        bool Has(params string[] values) => args.Any(a => values.Contains(a, StringComparer.OrdinalIgnoreCase));
        bool powershell = name is "pwsh.exe" or "powershell.exe";
        // PowerShell accepts abbreviated switches and Unicode dash prefixes. Check
        // every token conservatively: script text resembling a switch may veto cleanup,
        // but must never turn a stay-open session into a disposable helper.
        if (powershell && args.Any(a =>
        {
            a = a.Trim();
            if (a[0] is not ('-' or '/' or '\u2013' or '\u2014' or '\u2015')) return false;
            string key = a.TrimStart('-', '/', '\u2013', '\u2014', '\u2015');
            return key.Length >= 3 && "noexit".StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                key.Length >= 1 && "interactive".StartsWith(key, StringComparison.OrdinalIgnoreCase);
        })) return LaunchMode.Interactive;
        if (name == "node_repl.exe" || Has("-NoExit", "/k", "-i", "--interactive"))
            return LaunchMode.Interactive;
        if (name == "cmd.exe" && Has("/c")) return LaunchMode.NonInteractive;
        if (powershell && Has("-Command", "-File"))
        {
            int optionIndex = Array.FindIndex(args, a => a.Equals("-Command", StringComparison.OrdinalIgnoreCase) || a.Equals("-File", StringComparison.OrdinalIgnoreCase));
            return optionIndex + 1 < args.Length && args[optionIndex + 1] != "-" ? LaunchMode.NonInteractive : LaunchMode.Unknown;
        }
        if ((name is "bash.exe" or "sh.exe" or "python.exe" or "pythonw.exe") && Has("-c"))
            return LaunchMode.NonInteractive;
        if ((name is "node.exe" or "python.exe" or "pythonw.exe") && args.FirstOrDefault() is { } entry &&
            !entry.StartsWith('-') && (entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            entry.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) ||
            entry.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
            return LaunchMode.NonInteractive;
        return LaunchMode.Unknown;
    }
}
