using System.Net;
using System.Text;
using System.Text.Json;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 065 US2 — both SVID sources present the instance's re-attestation credential with AWS evidence, keep a newly
/// issued one, and send neither for other node types.
/// </summary>
public sealed class SvidSourcesPresentTheNodeCredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-src-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Env = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task The_JWT_source_sends_the_aws_envelope_and_stores_then_presents_the_credential()
    {
        var bella = new FakeBella(issueOnFirst: "bnr-issued");
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        var source = new HttpJwtSvidSource(bella.Client(), Request("aws-iid", store));

        await source.IssueAsync("bella-api", CancellationToken.None);
        await source.IssueAsync("bella-api", CancellationToken.None);

        Assert.Equal("bnr-issued", store.Current);
        var bodies = bella.Bodies.Select(b => JsonDocument.Parse(b).RootElement).ToList();
        Assert.Equal("aws-iid", bodies[0].GetProperty("nodeType").GetString());
        Assert.Equal("envelope", bodies[0].GetProperty("nodeAttestationToken").GetString());
        Assert.False(bodies[0].TryGetProperty("nodeReattestationCredential", out _), "nothing to present on first use");
        Assert.Equal("bnr-issued", bodies[1].GetProperty("nodeReattestationCredential").GetString());
    }

    [Fact]
    public async Task Kubernetes_evidence_sends_no_credential_and_never_touches_a_store()
    {
        var bella = new FakeBella(issueOnFirst: null);
        var request = new SvidAttestationRequest(Env, "billing", "bax-x-y", "k8s", () => "k8s-token");
        var source = new HttpJwtSvidSource(bella.Client(), request);

        await source.IssueAsync("bella-api", CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(bella.Bodies)).RootElement;
        Assert.Equal("k8s-token", body.GetProperty("nodeAttestationToken").GetString());
        Assert.False(body.TryGetProperty("nodeReattestationCredential", out _));
    }

    [Fact]
    public async Task A_refusal_with_no_credential_held_names_the_release_command()
    {
        var bella = new FakeBella(issueOnFirst: null) { Status = HttpStatusCode.Unauthorized };
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        var source = new HttpJwtSvidSource(bella.Client(), Request("aws-iid", store));

        var ex = await Assert.ThrowsAsync<SvidAttestationException>(() => source.IssueAsync("bella-api", CancellationToken.None));

        Assert.Contains("bella spiffe node-bindings release", ex.Message);
        Assert.DoesNotContain("bnr-", ex.Message);
    }

    private static SvidAttestationRequest Request(string nodeType, NodeReattestationCredentialStore store) =>
        new(Env, "billing", "bax-x-y", nodeType, () => null,
            ReadNodeTokenAsync: _ => Task.FromResult<string?>("envelope"),
            Credentials: store);

    private sealed class FakeBella(string? issueOnFirst) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<string> Bodies { get; } = new();

        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://bella.test") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":\"node_attestation_failed\"}") };
            var credential = Bodies.Count == 1 ? issueOnFirst : null;
            var json = JsonSerializer.Serialize(new
            {
                jwtSvid = "eyJ.x.y",
                spiffeId = "spiffe://t/p/e/billing",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                nodeReattestationCredential = credential,
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
