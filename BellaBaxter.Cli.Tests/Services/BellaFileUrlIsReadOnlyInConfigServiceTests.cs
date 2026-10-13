using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10 — the <c>url</c> a working-directory
/// <c>.bella</c> names is READ in exactly one place, <c>ConfigService</c>, because that is where the rule
/// "a server only a .bella names is used once the user has trusted it" runs.
/// </summary>
/// <remarks>
/// A second reader — a command that parses the file for <c>url</c> itself, or asks
/// <c>KeyContextService.ReadBellaSetting("url")</c> — would hand a cloned repository's server to a request
/// without the question ever being asked. <c>KeyContextService</c> may still WRITE a <c>url</c> line (merging a
/// login's context into the file); writing is not reading. Like the other guards in this folder it scans per
/// file, filters on paths relative to the source root, and drops comment lines.
/// </remarks>
public class BellaFileUrlIsReadOnlyInConfigServiceTests
{
    private static readonly Regex ReadsUrlField = new(
        """ReadBellaSetting\(\s*"url"|StartsWith\(\s*"url"|\bkey\s*(==|is)\s*"url"|\bcase\s+"url"|\[\s*"url"\s*\]""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void No_source_outside_ConfigService_reads_the_url_field_of_a_bella_file()
    {
        var root = CliSourceRoot();
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var full in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, full);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            scanned++;
            if (relative == Path.Combine("Services", "ConfigService.cs"))
                continue;

            if (ReadsUrl(File.ReadAllText(full)))
                offenders.Add(relative);
        }

        Assert.True(scanned > 50, $"The scan read only {scanned} files; is the source root right?");
        Assert.True(
            offenders.Count == 0,
            "These files read the `url` of a .bella themselves. That address must come from ConfigService.ApiUrl, "
                + "which asks the user to trust a server only a .bella names:\n  "
                + string.Join("\n  ", offenders.OrderBy(s => s)));
    }

    [Fact]
    public void The_resolver_still_reads_it_and_is_where_the_trust_check_lives()
    {
        // If the reader moved, this guard would pass vacuously. It must stay in ConfigService, next to the check.
        var source = File.ReadAllText(Path.Combine(CliSourceRoot(), "Services", "ConfigService.cs"));

        Assert.True(ReadsUrl(source));
        Assert.Contains("EnsureBellaServerIsKnown(url)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_matcher_still_bites()
    {
        Assert.True(ReadsUrl("var u = KeyContextService.ReadBellaSetting(\"url\");"));
        Assert.True(ReadsUrl("if (!trimmed.StartsWith(\"url\", StringComparison.OrdinalIgnoreCase)) continue;"));
        Assert.True(ReadsUrl("if (key == \"url\") apiUrl = val;"));
        Assert.True(ReadsUrl("var v = values[\"url\"];"));
        Assert.False(ReadsUrl("if (apiUrl is not null && FindLine(lines, \"url\") is null) lines.Add(x);")); // writing
        Assert.False(ReadsUrl("// ReadBellaSetting(\"url\") would be a second reader"));
        Assert.False(ReadsUrl("var u = KeyContextService.ReadBellaSetting(\"node_token_path\");"));
    }

    private static bool ReadsUrl(string source)
    {
        var code = string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Where(l =>
        {
            var t = l.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("*", StringComparison.Ordinal);
        }));
        return ReadsUrlField.IsMatch(code);
    }

    private static string CliSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}
