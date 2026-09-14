using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// Issue #742 — a command may not write progress straight at the console.
/// </summary>
/// <remarks>
/// <para>Deleting the 35 <c>AnsiConsole.Status()</c> calls only protects the call sites that existed.
/// The pattern is the natural thing to reach for — it is what every existing command did, and it
/// reads perfectly well — so the 36th gets written by someone copying a neighbouring file, and it
/// reintroduces the bug in one command while the behavioural tests keep passing.</para>
///
/// <para>This is the same instrument as the API's <c>ClientAddressIsSingleReaderTests</c> and
/// <c>PresentedKeyIsReadOnlyByTheGateTests</c>, with the same empty allow-list, for the same reason:
/// the rule is about a PATTERN in source, and no runtime test can see a call site it never executes.</para>
/// </remarks>
public class NoDirectConsoleStatusTests
{
    /// <summary>
    /// The one file allowed to call it: the writer, which is where the decision now lives.
    /// <para><b>This list is empty of commands and must stay that way.</b> Adding a file here means a
    /// command decides for itself whether progress is allowed on stdout, which is precisely the split
    /// that produced #742.</para>
    /// </summary>
    private static readonly string[] Allowed = ["OutputWriter.cs"];

    private static DirectoryInfo CliRoot()
    {
        // Walk up from the test assembly to the repo, the way the sibling suites anchor themselves,
        // so this does not depend on the runner's working directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return new DirectoryInfo(Path.Combine(dir!.FullName, "BellaBaxter.Cli"));
    }

    [Fact]
    public void No_command_calls_AnsiConsole_Status_directly()
    {
        var root = CliRoot();
        var offenders = new List<string>();

        foreach (var file in root.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (Allowed.Contains(file.Name))
                continue;

            var source = File.ReadAllText(file.Path());

            // Comments legitimately mention it — this file's own rationale does. Strip line comments
            // before matching so documentation of the rule does not violate it.
            var code = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

            if (Regex.IsMatch(code, @"AnsiConsole\s*\.\s*Status\s*\("))
                offenders.Add(Path.GetRelativePath(root.FullName, file.FullName));
        }

        Assert.True(
            offenders.Count == 0,
            "These write progress straight to stdout, which breaks `--json | jq` (#742):\n"
            + string.Join("\n", offenders.Select(o => "  " + o))
            + "\n\nUse `output.StatusAsync(\"message\", async () => { … })` instead. The writer shows a "
            + "spinner for a human and stays silent in JSON mode, so the command does not have to know "
            + "which mode it is in.");
    }

    [Fact]
    public void The_scan_actually_reads_files()
    {
        // A guard whose file walk silently finds nothing passes forever. This fails if the layout
        // moves out from under the anchor above.
        var count = CliRoot().GetFiles("*.cs", SearchOption.AllDirectories).Length;
        Assert.True(count > 50, $"only {count} source files found — the CLI root anchor has drifted");
    }
}

file static class FileInfoExtensions
{
    /// <summary>Full path, named so the call above reads as intent rather than as plumbing.</summary>
    public static string Path(this FileInfo file) => file.FullName;
}
