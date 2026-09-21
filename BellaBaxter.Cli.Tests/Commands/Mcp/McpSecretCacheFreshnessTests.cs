using System.Net;
using BellaCli.Commands.Mcp;

namespace BellaBaxter.Cli.Tests.Commands.Mcp;

/// <summary>
/// Issue #831 — <c>bella mcp</c> kept serving cached secrets to the AI assistant after the API key
/// was revoked, because its freshness check failed OPEN.
/// </summary>
/// <remarks>
/// <para>The check returned a two-valued <c>(bool Changed, string? NewETag)</c>, and both error paths
/// returned the same value as a genuine 304. A revoked key produces a 401, not a 304 — so the 401
/// landed in the error path, was read as "nothing changed", and the cached result was served without
/// the request ever reaching the API. With no TTH and no other eviction path than a successful 200,
/// the stale answers continued for the life of the process.</para>
///
/// <para>These tests pin the CLASSIFICATION, which is where the defect lived. The permissive answer
/// (<c>Unchanged</c>) must be reachable only from an actual 304 — every other outcome, including the
/// ones that look like success, must be <c>Unverifiable</c>, which the caller answers by evicting the
/// entry and forwarding upstream so the server re-authenticates and records the call.</para>
/// </remarks>
public class McpSecretCacheFreshnessTests
{
    [Fact]
    public async Task A_304_is_the_only_thing_that_authorises_serving_from_cache()
    {
        var (freshness, etag) = await CheckAsync(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        Assert.Equal(McpCommand.EtagFreshness.Unchanged, freshness);
        Assert.Null(etag);
    }

    [Fact]
    public async Task A_200_with_a_new_etag_reports_the_secrets_moved()
    {
        var (freshness, etag) = await CheckAsync(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v2\"");
            return r;
        });

        Assert.Equal(McpCommand.EtagFreshness.Changed, freshness);
        Assert.Equal("\"v2\"", etag);
    }

    /// <summary>THE defect. A revoked key answers 401, and 401 is not "nothing changed".</summary>
    [Fact]
    public async Task A_401_from_a_revoked_key_is_unverifiable_and_never_unchanged()
    {
        var (freshness, _) = await CheckAsync(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Assert.Equal(McpCommand.EtagFreshness.Unverifiable, freshness);

        Assert.NotEqual(McpCommand.EtagFreshness.Unchanged, freshness);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task No_other_status_establishes_freshness_either(HttpStatusCode status)
    {
        var (freshness, _) = await CheckAsync(_ => new HttpResponseMessage(status));

        Assert.Equal(McpCommand.EtagFreshness.Unverifiable, freshness);
    }

    [Fact]
    public async Task A_transport_fault_is_unverifiable_not_a_licence_to_serve()
    {
        var (freshness, _) = await CheckAsync(_ => throw new HttpRequestException("no route to host"));

        Assert.Equal(McpCommand.EtagFreshness.Unverifiable, freshness);
    }

    /// <summary>
    /// A 200 carrying no ETag leaves the cache with nothing to anchor on. Before #831 it was cached
    /// under <c>""</c>, so an entry holding a secret existed that no check could ever confirm.
    /// </summary>
    [Fact]
    public async Task A_200_with_no_etag_cannot_anchor_the_cache()
    {
        var (freshness, etag) = await CheckAsync(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(McpCommand.EtagFreshness.Unverifiable, freshness);
        Assert.Null(etag);
    }

    private static async Task<(McpCommand.EtagFreshness Freshness, string? NewETag)> CheckAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using var client = new HttpClient(new StubHandler(respond));

        return await McpCommand.CheckETagAsync(
            "https://api.example.com",
            "proj/prod/vault",
            "\"v1\"",
            client,
            TestContext.Current.CancellationToken);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
