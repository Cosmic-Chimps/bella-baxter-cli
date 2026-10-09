using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Advisory clients-1 — every source that handles a credential AND reads the server address picks that
/// address through the origin rules (<c>ApiUrlFor</c>, <c>ApiUrlForStoredCredential</c>,
/// <c>ApiUrlForSuppliedCredential</c>, <c>StoredCredentialMayGoTo</c>), not bare <c>config.ApiUrl</c>.
/// </summary>
/// <remarks>
/// The defect sat in a dozen places that each paired "the stored key" with "the configured address".
/// Fixing them protects only them; a new command written the old way would send the login to whatever a
/// <c>.bella</c> names. The scan is per file, like the API's single-author guards, and filters on paths
/// relative to the source root (a checkout under <c>.claude/worktrees/</c> once made a scan read nothing).
/// </remarks>
public class CredentialSendersAreOriginBoundTests
{
    /// <summary>
    /// Files that read the bare address for something other than presenting a credential, each with why.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // Discovers the login endpoints of the server being logged IN to (no credential exists yet for
        // it) and records that server as the new credential's origin; its refresh path is bound.
        [Path.Combine("Services", "AuthService.cs")] = "login discovery",
        // Write the address into a new .bella; they send nothing.
        [Path.Combine("Commands", "Shell", "ContextCommand.cs")] = "writes .bella",
        [Path.Combine("Services", "ContextService.cs")] = "writes .bella",
        [Path.Combine("Commands", "LoginCommand.cs")] = "writes .bella",
    };

    private static readonly Regex BareAddress = new(@"\bconfig\.ApiUrl\b", RegexOptions.Compiled);

    private static readonly Regex CredentialMaterial = new(
        @"LoadApiKey\(|LoadTokens\(|EnsureValidTokenAsync\(|\.Raw\b|\.AccessToken\b|\.RefreshToken\b|BELLA_BAXTER_API_KEY|BELLA_BAXTER_ACCESS_TOKEN",
        RegexOptions.Compiled);

    private static readonly Regex Bound = new(
        @"ApiUrlFor\(|ApiUrlForStoredCredential\(|ApiUrlForSuppliedCredential\(|StoredCredentialMayGoTo\(",
        RegexOptions.Compiled);

    [Fact]
    public void Every_source_that_sends_a_credential_chooses_its_address_through_the_origin_rules()
    {
        var root = CliSourceRoot();
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var full in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, full);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Exempt.ContainsKey(relative))
                continue;

            scanned++;
            if (Unbound(File.ReadAllText(full)))
                offenders.Add(relative);
        }

        Assert.True(scanned > 50, $"The scan read only {scanned} files; is the source root right?");
        Assert.True(
            offenders.Count == 0,
            "These files handle a credential and read config.ApiUrl without the origin rules — a .bella in a "
                + "cloned repository would choose where the credential goes. Use config.ApiUrlFor(key|tokens) or "
                + "config.ApiUrlForSuppliedCredential():\n  " + string.Join("\n  ", offenders.OrderBy(s => s)));
    }

    [Fact]
    public void The_matcher_still_bites()
    {
        Assert.True(Unbound("var key = credentials.LoadApiKey(); Send(key.Raw, config.ApiUrl);"));
        Assert.False(Unbound("var key = credentials.LoadApiKey(); Send(key.Raw, config.ApiUrlFor(key));"));
        Assert.False(Unbound("Show(config.ApiUrlAsConfigured);"));
        Assert.False(Unbound("// credentials.LoadApiKey() and config.ApiUrl in a comment"));
    }

    [Fact]
    public void The_exempted_files_exist()
    {
        var root = CliSourceRoot();
        foreach (var relative in Exempt.Keys)
            Assert.True(File.Exists(Path.Combine(root, relative)), relative);
    }

    private static bool Unbound(string source)
    {
        var code = string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Where(l =>
        {
            var t = l.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("*", StringComparison.Ordinal);
        }));

        return BareAddress.IsMatch(code) && CredentialMaterial.IsMatch(code) && !Bound.IsMatch(code);
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
