using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BellaBaxter.Client;

namespace BellaCli.Services.Spiffe;

// Spec 001 T043 (US6) — the JWT half of the Workload API, over Bella's HTTP surface.
//
// Separate from ISvidSource because the two answer different questions and fail differently. An X.509
// SVID is the agent's own identity, fetched once and rotated on a schedule; a JWT-SVID is minted per
// AUDIENCE, on demand, whenever a workload is about to call something. Folding them together would
// mean either caching JWT-SVIDs (and serving one for the wrong audience) or re-attesting for X.509 on
// every JWT request.
//
// NOT CACHED, deliberately. `FetchJWTSVID` is a unary call a client makes immediately before using the
// token; a cache would hand out a token closer to expiry than the client expects and turn a working
// call into an intermittent 401 at the far end. Bella's issuance is a single round trip.

/// <summary>A JWT-SVID for one audience.</summary>
/// <param name="Token">JWS compact serialization.</param>
/// <param name="SpiffeId">The <c>sub</c> — the workload this token asserts.</param>
/// <param name="ExpiresAt">When it stops being accepted.</param>
public sealed record JwtSvid(string Token, string SpiffeId, DateTimeOffset ExpiresAt);

/// <summary>Mints JWT-SVIDs and reads the JWT bundle.</summary>
/// <remarks>
/// An interface so the gRPC service can be tested without a Bella instance — the wire-shape rules
/// (audience handling, trust-domain keying) are what the tests are about, not HTTP.
/// </remarks>
public interface IJwtSvidSource
{
    /// <summary>Mints a JWT-SVID for one audience.</summary>
    Task<JwtSvid> IssueAsync(string audience, CancellationToken ct);

    /// <summary>The JWKS that validates this trust domain's JWT-SVIDs.</summary>
    Task<string> FetchJwtBundleAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class HttpJwtSvidSource(
    HttpClient httpClient,
    SvidAttestationRequest request) : IJwtSvidSource
{
    // Backlog §2.31 — the bootstrap token goes out and the SVID private key comes back over this client,
    // so an http address off this machine is refused at CONSTRUCTION, before anything can be sent. The
    // rule is BellaApiAddress's (shared with the .NET SPIFFE SDK); the command reports it first, with
    // the configuration source named, so an operator never meets this exception.
    private readonly Uri _bella = BellaApiAddress.RequireAcceptable(
        httpClient.BaseAddress?.ToString(), "The Bella API address the SVID agent attests to");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<JwtSvid> IssueAsync(string audience, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            // The SPIFFE spec requires at least one audience, and an empty one is not a harmless
            // default: a token with no audience is accepted by anything that does not check, which is
            // the failure mode audience binding exists to prevent.
            throw new SvidAttestationException("A JWT-SVID requires an audience.");
        }

        // Node evidence is re-read per call for the same reason as attestation: a projected
        // service-account token is rotated by the kubelet, and a value captured at startup fails later
        // as a signature error that points at cluster trust rather than at a stale read.
        var nodeToken = await request.ReadEvidenceAsync(ct).ConfigureAwait(false);

        // Spec 065 — the same credential store as the X.509 source, so neither can race the other into a lockout.
        if (request.Credentials is { } credentials && nodeToken is not null)
            return await credentials.AttestAsync(c => IssueOnceAsync(audience, nodeToken, c, ct), ct).ConfigureAwait(false);
        return (await IssueOnceAsync(audience, nodeToken, null, ct).ConfigureAwait(false)).Result;
    }

    private async Task<(JwtSvid Result, string? Issued)> IssueOnceAsync(
        string audience, string? nodeToken, string? credential, CancellationToken ct)
    {
        var body = new JwtSvidBody(
            request.WorkloadName,
            request.BootstrapToken,
            AttestationClaims: null,
            NodeAttestationToken: nodeToken,
            NodeType: nodeToken is null ? null : request.NodeType,
            Audience: audience,
            NodeReattestationCredential: credential);

        var url = $"/api/v1/environments/{request.EnvironmentId:D}/workload-identities/jwt-svid";

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SvidAttestationException(
                $"Could not reach Bella at {_bella} to mint a JWT-SVID for "
                + $"audience '{audience}'. Cause: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SvidAttestationException(await DescribeAsync(response, audience, ct).ConfigureAwait(false)
                + HttpSvidSource.NodeBindingHint(request, credential));
        }

        var issued = await response.Content
            .ReadFromJsonAsync<JwtSvidResponseBody>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new SvidAttestationException("Bella accepted the request but returned no JWT-SVID.");

        return (new JwtSvid(issued.JwtSvid, issued.SpiffeId, issued.ExpiresAt), issued.NodeReattestationCredential);
    }

    /// <inheritdoc />
    public async Task<string> FetchJwtBundleAsync(CancellationToken ct)
    {
        var url = $"/api/v1/environments/{request.EnvironmentId:D}/jwt-bundle";

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SvidAttestationException(
                $"Could not reach Bella at {_bella} to fetch the JWT bundle. "
                + $"Cause: {ex.Message}", ex);
        }

        // Bella answers an environment with no signing key yet with 200 and `{"keys":[]}` (#709), so a
        // 503 now means its key store could not be read. Servers released before #709 also used 503 for
        // "no key yet", which is why both are named.
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new SvidAttestationException(
                "Bella could not serve the JWT bundle (503): its JWT-SVID signing keys could not be read. "
                + "(Bella versions before #709 also answered 503 when no JWT-SVID had been issued yet.)");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SvidAttestationException(
                $"Bella refused the JWT bundle request with {(int)response.StatusCode}.");
        }

        var bundle = await response.Content
            .ReadFromJsonAsync<JwtBundleBody>(JsonOptions, ct)
            .ConfigureAwait(false);

        // A missing or empty JWKS is a FAILURE, not an empty bundle. Serving `{}` to a client would
        // make it reject every JWT-SVID as unverifiable and blame the token.
        return string.IsNullOrWhiteSpace(bundle?.Jwks)
            ? throw new SvidAttestationException("Bella returned an empty JWT bundle.")
            : bundle.Jwks;
    }

    private static async Task<string> DescribeAsync(
        HttpResponseMessage response, string audience, CancellationToken ct)
    {
        var detail = string.Empty;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                detail = $" Response: {text.Trim()}";
            }
        }
        catch
        {
            // The status alone still says something actionable.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Bella refused to mint a JWT-SVID: attestation failed. The bootstrap token was rejected, "
                + "or this workload's registered selectors do not match the evidence this host can "
                + $"present. Run 'bella spiffe whoami' to see the evidence.{detail}",
            HttpStatusCode.NotFound =>
                $"Bella does not know environment {response.RequestMessage?.RequestUri}. Check the "
                + $"--environment-id the agent was started with.{detail}",
            HttpStatusCode.TooManyRequests =>
                "Bella is rate-limiting attestation for this environment. Repeated failures from this "
                + $"source trigger a lockout; fix the cause rather than retrying harder.{detail}",
            _ => $"Bella refused to mint a JWT-SVID for audience '{audience}' with "
                 + $"{(int)response.StatusCode}.{detail}",
        };
    }

    private sealed record JwtSvidBody(
        string WorkloadName,
        string BootstrapToken,
        Dictionary<string, string>? AttestationClaims,
        string? NodeAttestationToken,
        string? NodeType,
        string Audience,
        string? NodeReattestationCredential = null);

    private sealed record JwtSvidResponseBody(
        string JwtSvid, string SpiffeId, DateTimeOffset ExpiresAt, string? NodeReattestationCredential = null);

    private sealed record JwtBundleBody(string? Jwks, string? TenantSlug, string? TrustDomain);
}
