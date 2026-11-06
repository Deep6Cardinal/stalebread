using System.Collections.Frozen;
using StaleBread.Core.Model;

namespace StaleBread.Core.Graph;

public sealed class ClassifierCatalog(string userProfile)
{
    public string Version => "1";
    public static FrozenSet<string> WatchNames { get; } = new[] {
        "conhost.exe", "OpenConsole.exe", "WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "claude.exe", "codex.exe", "bash.exe", "sh.exe", "node.exe", "node_repl.exe", "python.exe",
        "pythonw.exe", "uv.exe", "uvx.exe", "git.exe", "rg.exe", "testhost.exe"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public static bool IsAgent(string name) => name.ToLowerInvariant() is "claude.exe" or "codex.exe" or "code.exe" or "cursor.exe";
    public static bool IsBrowser(string name) => name.ToLowerInvariant() is "chrome.exe" or "msedge.exe" or "firefox.exe";
    // Diagnostic collection is broader than the fixed session/name count catalog.
    // Browsers only become findings when the graph associates them with an AI root.
    public static bool NeedsDiagnostics(ProcessSample sample) => WatchNames.Contains(sample.Name) ||
        IsAgent(sample.Name) || IsBrowser(sample.Name) || KnownMarker(sample.KnownAiMarker);
    public static bool IsConsole(string name) => name.ToLowerInvariant() is "conhost.exe" or "openconsole.exe" or "windowsterminal.exe";
    public static bool IsInterpreter(string name) => name.ToLowerInvariant() is "cmd.exe" or "powershell.exe" or "pwsh.exe" or "bash.exe" or "sh.exe" or "node.exe" or "node_repl.exe" or "python.exe" or "pythonw.exe";
    public static bool KnownMarker(string? marker) => marker is "@anthropic-ai/claude-code" or "@openai/codex";

    public OwnerKind Owner(ProcessSample p)
    {
        var path = p.CanonicalImagePath?.Replace('/', '\\');
        if (p.Name.Equals("Code.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Cursor.exe", StringComparison.OrdinalIgnoreCase))
            return OwnerKind.Ide;
        if (KnownMarker(p.KnownAiMarker)) return OwnerKind.Cli;
        if (path is null) return OwnerKind.Unknown;
        var profile = userProfile.TrimEnd('\\', '/');
        string[] cli = [profile + @"\.local\bin\claude.exe", profile + @"\.codex\bin\codex.exe"];
        if (cli.Contains(path, StringComparer.OrdinalIgnoreCase)) return OwnerKind.Cli;
        if (path.Equals(profile + @"\AppData\Local\AnthropicClaude\Claude.exe", StringComparison.OrdinalIgnoreCase))
            return OwnerKind.Desktop;
        return OwnerKind.Unknown;
    }
}
public sealed class AssociationClassifier(ClassifierCatalog catalog, string currentSid, int sessionId,
    IReadOnlySet<ProcessIdentity>? selfFamily = null, IReadOnlySet<string>? exclusions = null)
{
    public string CatalogVersion => catalog.Version;
    public AssociationEvidence Classify(ProcessSample sample, GraphSnapshot graph)
    {
        var reasons = new List<string>();
        bool agent = ClassifierCatalog.IsAgent(sample.Name) || ClassifierCatalog.KnownMarker(sample.KnownAiMarker);
        bool console = ClassifierCatalog.IsConsole(sample.Name);
        bool interactive = ClassifierCatalog.IsInterpreter(sample.Name) &&
            (sample.LaunchMode == LaunchMode.Interactive || sample.Name.Equals("node_repl.exe", StringComparison.OrdinalIgnoreCase));
        bool unknownMode = ClassifierCatalog.IsInterpreter(sample.Name) && sample.LaunchMode == LaunchMode.Unknown;
        bool protect = agent || console || interactive || unknownMode;
        if (agent) reasons.Add("AgentAlwaysProtected");
        if (console) reasons.Add("ConsoleHostNeverTargeted");
        if (interactive || unknownMode) reasons.Add("InteractiveOrUnknownMode");
        if (sample.OwnerSid != currentSid || sample.SessionId != sessionId || sample.SessionId == 0)
        { protect = true; reasons.Add("OwnershipUnverifiedOrOutOfScope"); }
        if (sample.Identity is not { } id)
            return new(ProcessRole.Unknown, true, OwnerKind.Unknown, null, null, sample.LaunchMode, false, ["MissingIdentity"]);
        if (selfFamily?.Contains(id) == true ||
            (sample.CanonicalImagePath is { } path && exclusions?.Any(x => x.Equals(path, StringComparison.OrdinalIgnoreCase)) == true))
        { protect = true; reasons.Add("SelfOrExcluded"); }

        ProcessIdentity? root = null;
        OwnerKind owner = OwnerKind.Unknown;
        var seen = new HashSet<ProcessIdentity>();
        var cursor = id;
        bool complete = true;
        bool selfRelated = false;
        while (true)
        {
            if (!seen.Add(cursor)) { complete = false; reasons.Add("AncestryCycle"); break; }
            var node = graph.Find(cursor);
            if (node is null) { complete = false; reasons.Add("AncestryEvicted"); break; }
            if (selfFamily?.Contains(cursor) == true) selfRelated = true;
            if (cursor != id && (ClassifierCatalog.IsAgent(node.Sample.Name) || ClassifierCatalog.KnownMarker(node.Sample.KnownAiMarker)))
            {
                if (root is null) { root = cursor; owner = catalog.Owner(node.Sample); }
                if (node.ExitedAt is null) reasons.Add("LiveAgentAncestor");
            }
            if (node.Sample.ParentPid == 0) break;
            if (!node.ParentKnown || node.Parent is not { } parent)
            { complete = false; reasons.Add(root is null ? "UnknownAncestry" : "UnknownAncestryAboveAgentRoot"); break; }
            cursor = parent;
        }
        // Explicit self identities also protect their ancestors, not merely descendants.
        if (!selfRelated && selfFamily is not null)
            foreach (var self in selfFamily)
            {
                var walk = self;
                var visited = new HashSet<ProcessIdentity>();
                while (visited.Add(walk) && graph.Find(walk) is { } node)
                {
                    if (walk == id) { selfRelated = true; break; }
                    if (node.Parent is not { } parent) break;
                    walk = parent;
                }
            }
        if (selfRelated) { protect = true; reasons.Add("SelfFamily"); }
        var role = agent ? ProcessRole.AgentRoot : console ? ProcessRole.ConsoleHost :
            interactive || unknownMode && root is null ? ProcessRole.InteractiveHost :
            root is not null && (ClassifierCatalog.WatchNames.Contains(sample.Name) || ClassifierCatalog.IsBrowser(sample.Name))
                ? ProcessRole.Helper : ProcessRole.Unknown;
        if (agent) owner = catalog.Owner(sample);
        return new(role, protect, owner, root, sample.KnownAiMarker, sample.LaunchMode, complete, reasons.ToArray());
    }
}
