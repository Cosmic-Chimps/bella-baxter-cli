using BellaCli.Commands.Mcp;

namespace BellaBaxter.Cli.Tests.Commands.Mcp;

/// <summary>
/// spec 035 US7 — <c>bella mcp --print-config</c> gains the connector sign-in block: the plane's connector
/// address derived from the configured API origin, the client id, and the fact that no secret is needed.
/// The proxy blocks (Claude Desktop / VS Code) are printed by the same method and are unchanged.
/// </summary>
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
}
