using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BellaCli.Services.Spiffe;

// Spec 001 T021 (US2) — the real attestation call behind ISvidSource.
//
// A plain HttpClient rather than the Kiota-generated BellaClient, and that is a deliberate choice
// rather than a shortcut. The generated client is built around a logged-in USER: it carries the
// credential store, the token-refresh handler, and the context service. A workload has none of those
// — it holds a bootstrap token and proves what it is. Routing an anonymous attestation through the
// user client would either drag that machinery into a sidecar that must not depend on a human having
// run `bella login`, or require a special "no auth" mode on a client whose entire purpose is auth.
//
// FAILURES ARE MESSAGES, NOT STATUS CODES. This runs unattended in a pod; whatever it throws is what
// an operator reads at 3am, possibly the only thing they get. So each status the attest endpoint can
// return is translated into what to DO about it. A bare "attestation failed: 401" would start an
// investigation that the words "the bootstrap token was rejected" would have ended.

/// <summary>Attests to Bella over HTTP and returns the issued SVID.</summary>
public sealed class HttpSvidSource(
    HttpClient httpClient,
    SvidAttestationRequest request,
    Func<DateTimeOffset>? now = null) : ISvidSource
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<AttestedSvid> AttestAsync(CancellationToken ct)
    {
        // Node evidence is read on EVERY attestation, not cached from startup. A Kubernetes projected
        // service-account token is refreshed by the kubelet on its own schedule, so a token captured
        // at startup is stale by the time the first renewal comes round — and the resulting failure
        // reads as "signature invalid", pointing at cluster trust rather than at a stale read.
        var nodeToken = request.ReadNodeToken();

        var body = new AttestBody(
            request.WorkloadName,
            request.BootstrapToken,
            AttestationClaims: null,
            NodeAttestationToken: nodeToken,
            NodeType: nodeToken is null ? null : request.NodeType);

        var url = $"/api/v1/environments/{request.EnvironmentId:D}/workload-identities/attest";

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SvidAttestationException(
                $"Could not reach Bella at {httpClient.BaseAddress} to attest. The agent will retry. "
                + $"Cause: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            var issued = await response.Content
                .ReadFromJsonAsync<AttestResponseBody>(JsonOptions, ct)
                .ConfigureAwait(false)
                ?? throw new SvidAttestationException(
                    "Bella accepted the attestation but returned no SVID.");

            // IssuedAt is OUR clock, not the certificate's notBefore, and that is on purpose: the
            // renewal window is measured against the same clock that later asks "is it time yet". A CA
            // that backdates notBefore by a few minutes (most do, for skew tolerance) would otherwise
            // make every SVID look older than it is and trigger early renewals for its whole life.
            return new AttestedSvid(
                issued.Certificate,
                issued.PrivateKey,
                issued.TrustBundle,
                issued.SpiffeId,
                IssuedAt: _now(),
                ExpiresAt: issued.ExpiresAt);
        }

        throw new SvidAttestationException(await DescribeFailureAsync(response, ct).ConfigureAwait(false));
    }

    /// <summary>Turns a refusal into something an operator can act on.</summary>
    private async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Read the body for context, but never let a parse failure replace the diagnosis.
        var detail = string.Empty;
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                detail = $" Server said: {raw.Trim()}";
            }
        }
        catch
        {
            // Context only.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Attestation was refused: the bootstrap token was rejected. Check the token passed to "
                + "the agent, and that the API key it names is still active."
                + detail,

            HttpStatusCode.Forbidden =>
                "Attestation was refused: the node evidence did not satisfy this environment's policy. "
                + $"Run `bella spiffe whoami` to see what evidence this host can present, and compare it "
                + "with the workload's selectors."
                + detail,

            HttpStatusCode.NotFound =>
                $"Attestation was refused: no workload named '{request.WorkloadName}' is registered in "
                + $"environment {request.EnvironmentId:D} — or the environment has no PKI store bound. "
                + "Both return 404 deliberately, so an unauthenticated caller cannot tell which."
                + detail,

            HttpStatusCode.TooManyRequests =>
                "Attestation was rate-limited. Repeated failures from one source trigger a temporary "
                + "lockout, so this usually means earlier attempts were being refused — fix those "
                + "rather than retrying harder."
                + detail,

            _ => $"Attestation failed with HTTP {(int)response.StatusCode}.{detail}",
        };
    }

    private sealed record AttestBody(
        string WorkloadName,
        string BootstrapToken,
        object? AttestationClaims,
        string? NodeAttestationToken,
        string? NodeType);

    private sealed record AttestResponseBody(
        string Certificate,
        string PrivateKey,
        string TrustBundle,
        string SpiffeId,
        DateTimeOffset ExpiresAt);
}

/// <summary>Everything the agent needs to prove what it is.</summary>
/// <param name="EnvironmentId">The environment the workload is registered in.</param>
/// <param name="WorkloadName">The registered workload name.</param>
/// <param name="BootstrapToken">The <c>bax-</c> bootstrap token.</param>
/// <param name="NodeType">Node attestor kind (<c>k8s</c>, <c>aws-iid</c>).</param>
/// <param name="ReadNodeToken">
/// Reads the node evidence fresh on each call. A function rather than a value because a Kubernetes
/// projected token is rotated by the kubelet underneath us.
/// </param>
public sealed record SvidAttestationRequest(
    Guid EnvironmentId,
    string WorkloadName,
    string BootstrapToken,
    string NodeType,
    Func<string?> ReadNodeToken);

/// <summary>Attestation was refused or could not be attempted. The message is operator-facing.</summary>
public sealed class SvidAttestationException(string message, Exception? inner = null)
    : Exception(message, inner);
