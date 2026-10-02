using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 065 US2 (research R9) — how the agent reads this EC2 instance's identity over the metadata service.
/// </summary>
/// <remarks>
/// IMDSv2 only: a session token from a PUT, presented on every read, and no IMDSv1 fallback, ever. IMDSv1 is what a
/// server-side request forgery reaches, and an instance that requires IMDSv2 has said so deliberately. The envelope
/// must be byte-for-byte what Bella's attestor decodes, or every attestation fails as "malformed".
/// </remarks>
public class AwsInstanceEvidenceTests
{
    private const string Document = "{\"accountId\":\"123456789012\",\"region\":\"eu-west-1\",\"instanceId\":\"i-0abc\",\"pendingTime\":\"2026-09-30T08:00:00Z\"}";
    private const string Signature = "c2lnbmF0dXJl\nbW9yZQ==";

    [Fact]
    public async Task It_gets_a_session_token_then_reads_document_and_signature_with_it()
    {
        var imds = new FakeImds();
        var envelope = await new AwsInstanceEvidence(imds.Client()).ReadEnvelopeAsync(CancellationToken.None);

        var token = Assert.Single(imds.Requests, r => r.Path == "/latest/api/token");
        Assert.Equal(HttpMethod.Put, token.Method);
        Assert.Equal("60", token.Headers["X-aws-ec2-metadata-token-ttl-seconds"]);
        Assert.All(imds.Requests.Where(r => r.Path != "/latest/api/token"),
            r => Assert.Equal(FakeImds.Token, r.Headers["X-aws-ec2-metadata-token"]));

        // The envelope is what AwsIidNodeAttestor decodes: base64url JSON {document, signature}, the signature with
        // IMDS's line breaks removed.
        var b = envelope.Replace('-', '+').Replace('_', '/');
        b += (b.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b)));
        Assert.Equal(Document, json.RootElement.GetProperty("document").GetString());
        Assert.Equal("c2lnbmF0dXJlbW9yZQ==", json.RootElement.GetProperty("signature").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task No_token_means_no_read_at_all_and_names_IMDSv2_and_the_hop_limit(HttpStatusCode tokenStatus)
    {
        var imds = new FakeImds { TokenStatus = tokenStatus };

        var ex = await Assert.ThrowsAsync<AwsInstanceEvidenceException>(
            () => new AwsInstanceEvidence(imds.Client()).ReadEnvelopeAsync(CancellationToken.None));

        Assert.Contains("IMDSv2", ex.Message);
        Assert.Contains("HttpPutResponseHopLimit", ex.Message);
        Assert.Contains("never falls back", ex.Message);
        // No IMDSv1-style read was attempted.
        Assert.DoesNotContain(imds.Requests, r => r.Path != "/latest/api/token");
    }

    [Fact]
    public async Task An_unreachable_metadata_service_is_bounded_and_explained()
    {
        var imds = new FakeImds { Hang = true };

        var ex = await Assert.ThrowsAsync<AwsInstanceEvidenceException>(
            () => new AwsInstanceEvidence(imds.Client()).ReadEnvelopeAsync(CancellationToken.None));

        Assert.Contains("Is this an EC2 instance?", ex.Message);
    }

    [Fact]
    public void The_metadata_client_never_uses_a_proxy()
    {
        var handler = Assert.IsType<SocketsHttpHandler>(AwsInstanceEvidence.CreateHandler());
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(2), AwsInstanceEvidence.CallTimeout);
        Assert.Equal("169.254.169.254", AwsInstanceEvidence.BaseAddress.Host);
    }

    [Fact]
    public async Task The_facts_for_display_never_include_the_signature()
    {
        var facts = await new AwsInstanceEvidence(new FakeImds().Client()).ReadFactsAsync(CancellationToken.None);

        Assert.Equal(new AwsInstanceFactsView("123456789012", "eu-west-1", "i-0abc", "2026-09-30T08:00:00Z"), facts);
        Assert.DoesNotContain("c2ln", JsonSerializer.Serialize(facts));
    }

    internal sealed class FakeImds : HttpMessageHandler
    {
        public const string Token = "imds-session-token";
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public bool Hang { get; init; }
        public ConcurrentQueue<(HttpMethod Method, string Path, Dictionary<string, string> Headers)> Queue { get; } = new();
        public IReadOnlyList<(HttpMethod Method, string Path, Dictionary<string, string> Headers)> Requests => Queue.ToArray();

        public HttpClient Client() => new(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Queue.Enqueue((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
            if (Hang)
                await Task.Delay(Timeout.Infinite, ct);
            return request.RequestUri!.AbsolutePath switch
            {
                "/latest/api/token" => new HttpResponseMessage(TokenStatus) { Content = new StringContent(TokenStatus == HttpStatusCode.OK ? Token : "") },
                "/latest/dynamic/instance-identity/document" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Document) },
                "/latest/dynamic/instance-identity/signature" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Signature) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }
    }
}
