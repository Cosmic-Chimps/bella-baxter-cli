using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 081 T019 (R9, FR-022) — the Nomad identity file name is written in exactly ONE place, and the Nomad path is
/// resolved only by the agent and <c>whoami</c>.
/// </summary>
/// <remarks>
/// Same shape as <see cref="KubeletPathHasOneAuthorTests"/>: two copies of the file-name pattern are how a detector comes
/// to disagree with the reader about which file is the token. Paths are compared RELATIVE to the source root, because a
/// checkout under <c>.claude/worktrees/</c> once made a repo scan exclude every file and pass having read nothing.
/// </remarks>
public class NomadIdentityPathHasOneAuthorTests
{
    private static readonly string Author = Path.Combine("Services", "Spiffe", "NomadIdentityPath.cs");

    [Fact]
    public void The_identity_file_name_pattern_lives_only_in_NomadIdentityPath()
    {
        var root = CliSourceRoot();
        // The `.jwt` suffix is what makes a string the identity file name; the prefix alone is a common word in prose.
        var offenders = SourceFiles(root)
            .Where(f => File.ReadAllText(f.Full).Contains(".jwt", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .Where(r => r != Author)
            .ToList();

        Assert.True(offenders.Count == 0, $"The Nomad identity file name is written outside NomadIdentityPath.cs: {string.Join(", ", offenders)}");
        // Non-vacuous: the scan really read the author.
        Assert.Contains(".jwt", File.ReadAllText(Path.Combine(root, Author)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_NOMAD_TOKEN_variable_is_not_named_anywhere()
    {
        // `NOMAD_TOKEN` is an ACL token. The only variable this CLI reads is the NAMED identity's, `NOMAD_TOKEN_<name>`.
        var root = CliSourceRoot();
        var offenders = SourceFiles(root)
            .Where(f => Regex.IsMatch(File.ReadAllText(f.Full), "\"NOMAD_TOKEN\""))
            .Select(f => f.Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"The plain NOMAD_TOKEN is named in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Only_the_agent_and_whoami_resolve_a_nomad_identity_path()
    {
        // FR-022: the implicit keyless flows (`run`, `exec`, `sdk run`, `auth oidc`) must never resolve one.
        var root = CliSourceRoot();
        var users = SourceFiles(root)
            .Where(f => File.ReadAllText(f.Full).Contains("NomadIdentityPath", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        var allowed = new[]
        {
            Path.Combine("Commands", "Spiffe", "SpiffeAgentCommand.cs"),
            Path.Combine("Commands", "Spiffe", "SpiffeWhoAmICommand.cs"),
            Path.Combine("Services", "Spiffe", "NomadEvidence.cs"),
            Author,
        }.Order(StringComparer.Ordinal).ToList();

        Assert.Equal(allowed, users);
    }

    [Fact]
    public void The_resolver_never_reads_a_project_file()
    {
        var source = File.ReadAllText(Path.Combine(CliSourceRoot(), Author));

        Assert.DoesNotContain("ReadBellaSetting", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyContextService", source, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Full, string Relative)> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f)))
            .Where(f => !f.Relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static string CliSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}
