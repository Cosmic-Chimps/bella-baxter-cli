using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BellaBaxter.Client;

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
    public async Task<AttestedSvid> AttestAsync(CancellationToken ct)
    {
        // Node evidence is read on EVERY attestation, not cached from startup. A Kubernetes projected
        // service-account token is refreshed by the kubelet on its own schedule, so a token captured
        // at startup is stale by the time the first renewal comes round — and the resulting failure
        // reads as "signature invalid", pointing at cluster trust rather than at a stale read.
        var nodeToken = await request.ReadEvidenceAsync(ct).ConfigureAwait(false);

        // Spec 065 — an AWS-evidence attestation goes through the instance's credential store: the held credential is
        // presented, a newly issued one is kept, and with none held only one attestation runs at a time.
        if (request.Credentials is { } credentials && nodeToken is not null)
            return await credentials.AttestAsync(c => AttestOnceAsync(nodeToken, c, ct), ct).ConfigureAwait(false);
        return (await AttestOnceAsync(nodeToken, null, ct).ConfigureAwait(false)).Result;
    }

    private async Task<(AttestedSvid Result, string? Issued)> AttestOnceAsync(
        string? nodeToken, string? credential, CancellationToken ct)
    {
        var body = new AttestBody(
            request.WorkloadName,
            request.BootstrapToken,
            AttestationClaims: null,
            NodeAttestationToken: nodeToken,
            NodeType: nodeToken is null ? null : request.NodeType,
            NodeReattestationCredential: credential);

        var url = $"/api/v1/environments/{request.EnvironmentId:D}/workload-identities/attest";

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SvidAttestationException(
                $"Could not reach Bella at {_bella} to attest. The agent will retry. "
                + $"Cause: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            var issued = await response.Content
                .ReadFromJsonAsync<AttestResponseBody>(JsonOptions, ct)
                .ConfigureAwait(false)
                ?? throw new SvidAttestationException(
                    "Bella accepted the attestation but returned no SVID.");

            // Backlog §2.31 — the key is converted to the PKCS#8 DER the Workload API serves HERE, once,
            // into a buffer the agent can clear. The PEM `string` it came from cannot be cleared (JSON
            // materialises it as an immutable .NET string); this is the earliest point at which the key
            // exists in a form that can be. A key that cannot be read is an attestation failure now,
            // rather than a malformed push to every workload later.
            SvidPrivateKey key;
            try
            {
                key = SvidPrivateKey.FromPem(issued.PrivateKey);
            }
            catch (InvalidOperationException ex)
            {
                throw new SvidAttestationException(
                    "Bella issued an SVID whose private key the agent cannot serve. " + ex.Message, ex);
            }

            // IssuedAt is OUR clock, not the certificate's notBefore, and that is on purpose: the
            // renewal window is measured against the same clock that later asks "is it time yet". A CA
            // that backdates notBefore by a few minutes (most do, for skew tolerance) would otherwise
            // make every SVID look older than it is and trigger early renewals for its whole life.
            return (new AttestedSvid(
                issued.Certificate,
                key,
                issued.TrustBundle,
                issued.SpiffeId,
                IssuedAt: _now(),
                ExpiresAt: issued.ExpiresAt), issued.NodeReattestationCredential);
        }

        throw new SvidAttestationException(await DescribeFailureAsync(response, ct).ConfigureAwait(false)
            + NodeBindingHint(request, credential));
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

    /// <summary>
    /// Spec 065 — the one case the agent can name: AWS evidence presented with NO credential, so a bound instance would
    /// refuse it. The agent cannot tell a lost credential from a forged one (the refusal body is generic on purpose),
    /// so this describes the operator's options rather than claiming which happened.
    /// </summary>
    internal static string NodeBindingHint(SvidAttestationRequest request, string? credential) =>
        request.Credentials is not null && credential is null
            ? $" If this instance is already bound to workload '{request.WorkloadName}', the agent no longer holds its "
              + $"re-attestation credential (looked in {request.Credentials.StateDirectory}). An operator can release the "
              + "binding with `bella spiffe node-bindings release <id>`."
            : string.Empty;

    private sealed record AttestBody(
        string WorkloadName,
        string BootstrapToken,
        object? AttestationClaims,
        string? NodeAttestationToken,
        string? NodeType,
        string? NodeReattestationCredential = null);

    // PrivateKey is a `string` here because that is what the JSON body holds and what the serializer
    // produces — it is never kept: AttestAsync converts it to a clearable SvidPrivateKey immediately.
    private sealed record AttestResponseBody(
        string Certificate,
        string PrivateKey,
        string TrustBundle,
        string SpiffeId,
        DateTimeOffset ExpiresAt,
        string? NodeReattestationCredential = null);
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
/// <param name="ReadNodeTokenAsync">
/// Spec 065 — an asynchronous reader, used instead of <paramref name="ReadNodeToken"/> when set (the AWS identity
/// document comes from the instance metadata service, over the network).
/// </param>
/// <param name="Credentials">
/// Spec 065 — the instance's re-attestation credential store, for AWS evidence. Null for every other node type.
/// </param>
public sealed record SvidAttestationRequest(
    Guid EnvironmentId,
    string WorkloadName,
    string BootstrapToken,
    string NodeType,
    Func<string?> ReadNodeToken,
    Func<CancellationToken, Task<string?>>? ReadNodeTokenAsync = null,
    NodeReattestationCredentialStore? Credentials = null)
{
    /// <summary>Reads the node evidence fresh: the async reader when set, else the synchronous one.</summary>
    public Task<string?> ReadEvidenceAsync(CancellationToken ct) =>
        ReadNodeTokenAsync is { } read ? read(ct) : Task.FromResult(ReadNodeToken());
}

/// <summary>Attestation was refused or could not be attempted. The message is operator-facing.</summary>
public sealed class SvidAttestationException(string message, Exception? inner = null)
    : Exception(message, inner);
