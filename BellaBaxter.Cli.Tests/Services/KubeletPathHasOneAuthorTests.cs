using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 064 T033 — the kubelet's default token path is written in exactly ONE place, <c>NodeTokenPath.cs</c>.
/// </summary>
/// <remarks>
/// Before spec 064 the literal had two homes: platform detection checked one copy and the keyless exchange read
/// another, while the agent read a third (a constant). A projected token at another path then made the detector
/// say "no platform" while nothing else noticed. Everything that asks "which token" now goes through
/// <see cref="NodeTokenPath.Resolve"/>; this scan fails on a new copy, with an EMPTY allow-list. It filters on
/// paths RELATIVE to the source root, because a checkout under <c>.claude/worktrees/</c> once made a repo scan
/// exclude every file and pass having read nothing.
/// </remarks>
public class KubeletPathHasOneAuthorTests
{
    [Fact]
    public void The_kubelet_token_path_literal_lives_only_in_NodeTokenPath()
    {
        var root = CliSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f)))
            .Where(f => !f.Relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f.Full).Contains(NodeTokenPath.KubeletDefault, StringComparison.Ordinal))
            .Select(f => f.Relative)
            .Where(r => r != Path.Combine("Services", "Spiffe", "NodeTokenPath.cs"))
            .ToList();

        Assert.True(offenders.Count == 0, $"The kubelet token path is written outside NodeTokenPath.cs: {string.Join(", ", offenders)}");
        // Non-vacuous: the scan really read the author.
        Assert.Contains(NodeTokenPath.KubeletDefault, File.ReadAllText(Path.Combine(root, "Services", "Spiffe", "NodeTokenPath.cs")));
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
