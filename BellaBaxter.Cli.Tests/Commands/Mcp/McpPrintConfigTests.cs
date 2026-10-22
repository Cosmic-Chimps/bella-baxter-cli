using BellaBaxter.Cli.Tests.Services;
using BellaCli.Commands.Mcp;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Commands.Mcp;

/// <summary>
/// spec 035 US7 — <c>bella mcp --print-config</c> gains the connector sign-in block: the plane's connector
/// address derived from the configured API origin, the client id, and the fact that no secret is needed.
/// The proxy blocks (Claude Desktop / VS Code) are printed by the same method and are unchanged.
/// </summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class McpPrintConfigTests
{
    [Fact]
    public void The_connector_block_names_the_address_the_client_id_and_no_secret()
    {
        var block = McpCommand.RenderConnectorBlock("https://api.bella-baxter.io");

        Assert.Contains("https://api.bella-baxter.io/mcp", block);
        Assert.Contains("bella-mcp-connector", block);
        Assert.Contains("no client secret", block);
        Assert.Contains("Connected AI clients", block);
        Assert.Contains("5 minutes", block);
    }

    [Fact]
    public void A_trailing_slash_on_the_api_origin_does_not_double_up()
    {
        var block = McpCommand.RenderConnectorBlock("https://bella.example.com/");
        Assert.Contains("https://bella.example.com/mcp", block);
        Assert.DoesNotContain("com//mcp", block);
    }

    [Fact]
    public void A_self_hosted_api_origin_is_used_verbatim()
    {
        // The PKI-topology planes publish the connector on the API domain, not the gateway (research R10).
        var block = McpCommand.RenderConnectorBlock("https://api.customer.internal");
        Assert.Contains("Connector address : https://api.customer.internal/mcp", block);
    }

    // ── #1483 — the proxy block uses only names the CLI reads ────────────────────────────────

    [Fact]
    public void A_self_hosted_proxy_block_names_the_canonical_variables_only()
    {
        var env = McpCommand.ProxyEnvironment("https://bella.customer.example");

        // BELLA_API_URL / BELLA_API_KEY are aliases the CLI INJECTS into children; it never reads them,
        // so a pasted block that used them pointed the proxy at the hosted service.
        Assert.Equal(["BELLA_BAXTER_API_KEY", "BELLA_BAXTER_URL"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_address_in_the_printed_block_is_the_one_the_cli_resolves_when_it_is_exported()
    {
        const string SelfHosted = "https://bella.customer.example";
        var saved = Environment.GetEnvironmentVariable("BELLA_BAXTER_URL");
        var savedLegacy = Environment.GetEnvironmentVariable("BAXTER_URL");
        try
        {
            Environment.SetEnvironmentVariable("BAXTER_URL", null);
            Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", null);
            foreach (var (name, value) in McpCommand.ProxyEnvironment(SelfHosted).Where(kv => kv.Key.EndsWith("_URL", StringComparison.Ordinal)))
                Environment.SetEnvironmentVariable(name, value);

            // The machine default is the hosted service: if the printed name is not one ConfigService reads,
            // this is what comes back instead of the self-hosted address.
            var config = new ConfigService(new BellaConfig("https://api.bella-baxter.io"));

            Assert.Equal(SelfHosted, config.ApiUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", saved);
            Environment.SetEnvironmentVariable("BAXTER_URL", savedLegacy);
        }
    }

    [Fact]
    public void The_hosted_default_prints_no_address_override()
    {
        var env = McpCommand.ProxyEnvironment(BellaConfig.DefaultApiUrl);

        Assert.Equal(["BELLA_BAXTER_API_KEY"], env.Keys);
    }
}
