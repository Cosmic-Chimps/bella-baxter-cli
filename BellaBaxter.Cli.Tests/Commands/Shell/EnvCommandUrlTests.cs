using BellaCli.Commands.Shell;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Commands.Shell;

/// <summary>
/// The CLI ships to BOTH planes — the hosted SaaS (`api.bella-baxter.io`) and self-hosted installs,
/// where the configured origin is the operator's own host and, once the PKI topology is deployed, the
/// GATEWAY origin (`gw.&lt;domain&gt;`) because the certificates/scout subtrees exist only there.
///
/// <para>The URL used to be emitted only when it DIFFERED from the hosted default. That left a hole:
/// `eval $(bella env)` in a shell that already carried a stale `BELLA_BAXTER_URL` kept the stale value
/// whenever the configured origin happened to equal the default — so the caller silently talked to a
/// server this CLI was not configured for. It also duplicated the SaaS literal across three commands,
/// baking a SaaS-only assumption into a shared binary.</para>
/// </summary>
public class EnvCommandUrlTests
{
    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void the_url_is_always_exported_even_when_it_is_the_hosted_default(string shell)
    {
        var statement = EnvCommand.BuildExportStatement(
            shell, "bk_key", "proj", "dev", BellaConfig.DefaultApiUrl);

        Assert.Contains("BELLA_BAXTER_URL", statement);
        Assert.Contains(BellaConfig.DefaultApiUrl, statement);
        // Deprecated alias kept for compatibility.
        Assert.Contains("BELLA_API_URL", statement);
    }

    [Theory]
    // A self-hosted origin, and the gateway origin a PKI-topology install needs.
    [InlineData("https://bella.customer.internal")]
    [InlineData("https://gw.bella-baxter.io")]
    public void a_self_hosted_or_gateway_origin_is_exported_verbatim(string url)
    {
        foreach (var shell in new[] { "bash", "fish", "pwsh" })
        {
            var statement = EnvCommand.BuildExportStatement(shell, "bk_key", "proj", "dev", url);
            Assert.Contains(url, statement);
        }
    }

    [Fact]
    public void the_hosted_default_is_defined_in_exactly_one_place()
    {
        // Guards the duplication that caused this: the literal lived in ShellOpenCommand, EnvCommand
        // and McpCommand as well. If someone re-inlines it, this at least pins the canonical value.
        Assert.Equal("https://api.bella-baxter.io", BellaConfig.DefaultApiUrl);
        Assert.Equal(BellaConfig.DefaultApiUrl, new BellaConfig().ApiUrl);
    }
}
