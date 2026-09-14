using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Commands.Issue;

/// <summary>
/// #725 — <c>bella issue</c> must present the operator's REGISTERED device key, not an ephemeral one.
/// </summary>
/// <remarks>
/// <para><b>What happened.</b> The command built its client through <c>CreateClientWrapper()</c>, whose
/// bearer branches call <c>CreateWithBearerToken</c> — and that factory chains an
/// <c>E2EEncryptionHandler</c>, which mints a FRESH P-256 key per client. Since #635 widened
/// <c>ZkePresentedKey.ShouldPresent</c> to every <c>/api/</c> path, that ephemeral key travelled as
/// <c>X-E2E-Public-Key</c> to <c>tokens/issue</c>, which carries <c>[RequireRegisteredDevice]</c>. The
/// gate looked it up, found a key registered to nobody, and refused. Correctly — so no scoped token
/// could be minted from the CLI in any enforcing tenant.</para>
///
/// <para><b>Why these are source guards.</b> The defect is a choice of factory method, and it is
/// invisible at runtime without a packet capture: the request looks perfectly well-formed and the
/// server's 403 is right. There is no seam to assert against that does not amount to re-reading this
/// choice, so the choice is what is pinned.</para>
/// </remarks>
public class IssuePresentsTheDeviceKeyTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    private static string IssueCommand() =>
        Read("BellaBaxter.Cli", "Commands", "Issue", "IssueCommand.cs");

    /// <summary>
    /// The file with `//` comments stripped.
    /// </summary>
    /// <remarks>
    /// A guard that reads prose fails on correct code: the first version of the
    /// CreateClientWrapper assertion below tripped on the comment EXPLAINING that the call used to be
    /// there. A guard that flags an accurate comment gets deleted, and then it protects nothing.
    /// </remarks>
    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));

    [Fact]
    public void Issue_selects_its_client_through_the_shared_device_gate()
    {
        var source = IssueCommand();

        Assert.Contains("ZkeClientSelection", source, StringComparison.Ordinal);
        Assert.Contains("zkeSelection.SelectAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Issue_does_not_build_its_own_client_with_an_ephemeral_key()
    {
        // CreateClientWrapper is exactly the call that produced the ephemeral key. `bella issue` needs
        // no raw access token — it only ever used the wrapper's client — so there is no reason for it
        // to come back.
        var code = CodeOnly(IssueCommand());

        Assert.DoesNotContain("CreateClientWrapper", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Issue_renders_a_gate_refusal_instead_of_a_bare_status_code()
    {
        // The reported symptom was "API error (403)" and nothing else: no detail, no next step. An
        // operator can also enable enforcement between the status call and this request, so the 403
        // path has to stay handled even though the command asks first.
        var source = IssueCommand();

        Assert.Contains("zkeSelection.RenderRefusal", source, StringComparison.Ordinal);
        Assert.Contains("ZkeClientSelection.RefusedExitCode", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_body_key_stays_separate_from_the_presented_key()
    {
        // Two different machines by design: the operator's registered device proves WHO is asking (the
        // header), while the body's publicKey names the CI runner the minted token is bound to (spec
        // 040). Collapsing them would either bind the token to the wrong machine or re-break the gate.
        var source = IssueCommand();

        Assert.Contains("PublicKey = devicePublicKey", source, StringComparison.Ordinal);
        Assert.Contains("privateKeyOverride: null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_bearer_client_can_log_when_debug_is_enabled()
    {
        // BELLA_BAXTER_DEBUG=1 printed nothing on any OAuth path: the single handler slot was taken by
        // the token refresher, or on the env-token branch left empty, while the HMAC branches always
        // passed the logger. That asymmetry is why a wrong header on one request needed a packet
        // capture to find.
        var provider = Read("BellaBaxter.Cli", "Services", "BellaClientProvider.cs");

        var bearerCalls = Regex.Matches(provider, @"CreateWithBearerToken(AndZke)?\(", RegexOptions.None).Count;
        var debugChained = Regex.Matches(provider, @"DebugHandler\(\)", RegexOptions.None).Count - 1; // minus the definition

        Assert.True(bearerCalls > 0, "expected the provider to build bearer clients");
        Assert.True(
            debugChained >= bearerCalls,
            $"every bearer client must be able to log: {bearerCalls} bearer call(s), {debugChained} with a debug handler");
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
