using StaleBread.Core.Graph;
using StaleBread.Core.Model;

namespace StaleBread.Core.Tests;

public class LaunchEvidenceTests
{
    [Theory]
    [InlineData("-noe")] [InlineData("-NoEx")] [InlineData("-NoExi")]
    [InlineData("/NoExit")] [InlineData("-Interactive")] [InlineData("-inter")]
    [InlineData("–noe")] [InlineData("—noe")]
    [InlineData(" -noe ")] [InlineData("\t/NoExit\t")]
    public void PowerShellStayInteractiveSpellingsOverrideCommand(string flag)
        => Assert.Equal(LaunchMode.Interactive, LaunchEvidence.Parse("pwsh.exe", [flag, "-Command", "Get-Date"]));

    [Theory] [InlineData("-File")] [InlineData("-Command")]
    public void PowerShellStdinModeIsNotPositiveNoninteractiveEvidence(string option)
        => Assert.Equal(LaunchMode.Unknown, LaunchEvidence.Parse("pwsh.exe", [option, "-"]));

    [Theory]
    [InlineData("pwsh.exe", "-Command", "Write-Output hi", LaunchMode.NonInteractive)]
    [InlineData("pwsh.exe", "-Command", "-NoExit", LaunchMode.Interactive)]
    [InlineData("cmd.exe", "/c", "/k", LaunchMode.Interactive)]
    [InlineData("cmd.exe", "/c", "echo hi", LaunchMode.NonInteractive)]
    [InlineData("node.exe", @"C:\tools\script.js", "", LaunchMode.NonInteractive)]
    [InlineData("python.exe", "-c", "-i", LaunchMode.Interactive)]
    [InlineData("bash.exe", "-c", "echo hi", LaunchMode.NonInteractive)]
    [InlineData("node_repl.exe", "-c", "", LaunchMode.Interactive)]
    [InlineData("pwsh.exe", "", "", LaunchMode.Unknown)]
    public void InteractiveFlagsOverrideNoninteractiveEvidence(string name, string a, string b, LaunchMode expected)
        => Assert.Equal(expected, LaunchEvidence.Parse(name, [a, b]));
}
