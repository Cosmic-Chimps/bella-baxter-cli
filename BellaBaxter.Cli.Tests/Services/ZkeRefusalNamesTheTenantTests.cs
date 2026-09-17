using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// #820 — the ZKE refusal must name the tenant that refused, at the call site that actually runs.
/// </summary>
/// <remarks>
/// <para><b>Why a source guard.</b> <see cref="BellaCli.Services.ZkeClientSelection"/> reaches this
/// sentence only after a real <c>getZkeStatus</c> call against a tenant that enforces, with a key
/// registered somewhere else — there is no seam in this project that reproduces that without a server.
/// The defect is an omitted argument, so the argument is what is pinned (the
/// <c>IssuePresentsTheDeviceKeyTests</c> shape).</para>
///
/// <para><b>And why the pure tests were not enough.</b> <c>ZkeGateTests</c> covered
/// <c>MessageFor(outcome, "acme")</c> — the branch no production code took. The only call site passed
/// no slug at all, so a green suite reported a feature nobody could get. A test for a defaulted
/// parameter has to exercise the default, or the call site, or both.</para>
/// </remarks>
public class ZkeRefusalNamesTheTenantTests
{
    private static string Selection() =>
        File.ReadAllText(
            Path.Combine(RepoRoot(), "BellaBaxter.Cli", "Services", "ZkeClientSelection.cs"));

    [Fact]
    public void The_gate_refusal_is_told_which_tenant_refused()
    {
        var source = Selection();

        Assert.Matches(new Regex(@"ZkeGate\.MessageFor\(\s*outcome\s*,\s*\w"), source);
    }

    [Fact]
    public void No_call_site_falls_back_to_the_unnamed_message()
    {
        // `MessageFor(outcome)` compiles — the slug parameter is optional so the gate stays callable
        // from the pure tests — which is exactly how the omission survived review.
        var source = Selection();

        Assert.DoesNotMatch(new Regex(@"ZkeGate\.MessageFor\(\s*outcome\s*\)"), source);
    }

    [Fact]
    public void The_slug_comes_from_the_stored_tokens_the_rest_of_the_CLI_reads()
    {
        // `bella auth status` and `auth setup` both name the tenant from tokens.OrgSlug. A second
        // source for the same fact is how two screens come to disagree about which org you are in.
        var source = Selection();

        Assert.Contains("LoadTokens()?.OrgSlug", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli.slnx"))
                               && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
