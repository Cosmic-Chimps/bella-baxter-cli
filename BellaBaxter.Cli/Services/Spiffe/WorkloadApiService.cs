using System.Text;
using BellaCli.Services.Spiffe.WorkloadApi;
using Google.Protobuf;
using Grpc.Core;

namespace BellaCli.Services.Spiffe;

// Spec 001 T042/T043 (US6, FR-026) — the SPIFFE Workload API, served from the in-memory SVID.
//
// THE POINT OF THIS FILE is that it contains no Bella-specific concepts. A workload using `go-spiffe`,
// `java-spiffe` or `spiffe-helper` talks to this socket and never learns Bella exists. Everything
// Bella-shaped — attestation, bootstrap tokens, environment ids — lives behind `SvidAgent` and
// `IJwtSvidSource`, and stops here.
//
// FOUR RULES THAT ARE EASY TO GET WRONG AND HARD TO NOTICE:
//
//  1. The `workload.spiffe.io: true` metadata header is REQUIRED by the spec and is refused when
//     absent. It is not decoration: it is the spec's guard against a browser or a confused HTTP client
//     being talked into driving the socket, and standard clients always send it. Accepting requests
//     without it would work fine in testing and quietly widen what can reach a private key.
//
//  2. The streams NEVER complete on their own. `FetchX509SVID` and the bundle streams stay open and
//     push on rotation; a server that returned one message and closed would leave every standard
//     client reconnecting in a loop, which reads as an agent crash.
//
//  3. A stream with no identity yet WAITS; it does not error and does not send an empty message. At
//     startup the agent may not have attested. An empty `X509SVIDResponse` is a valid protobuf message
//     that a client reads as "you are entitled to nothing" — a permanent-looking answer to a
//     temporary state.
//
//  4. Bundle maps are keyed by TRUST DOMAIN (`spiffe://acme`), not by the workload's full SPIFFE ID.
//     Getting this wrong produces a response that parses perfectly and in which the client finds no
//     bundle.

/// <summary>Serves the SPIFFE Workload API from the agent's in-memory SVID.</summary>
public sealed class WorkloadApiService(
    SvidAgent agent,
    IJwtSvidSource jwtSource,
    Func<DateTimeOffset>? now = null)
    : SpiffeWorkloadAPI.SpiffeWorkloadAPIBase
{
    /// <summary>The metadata header the SPIFFE spec requires on every Workload API call.</summary>
    public const string SecurityHeader = "workload.spiffe.io";

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public override async Task FetchX509SVID(
        X509SVIDRequest request,
        IServerStreamWriter<X509SVIDResponse> responseStream,
        ServerCallContext context)
    {
        RequireSecurityHeader(context);

        await StreamAsync(
            context,
            svid => responseStream.WriteAsync(BuildX509Response(svid), context.CancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task FetchX509Bundles(
        X509BundlesRequest request,
        IServerStreamWriter<X509BundlesResponse> responseStream,
        ServerCallContext context)
    {
        RequireSecurityHeader(context);

        await StreamAsync(
            context,
            svid => responseStream.WriteAsync(BuildBundleResponse(svid), context.CancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<JWTSVIDResponse> FetchJWTSVID(
        JWTSVIDRequest request, ServerCallContext context)
    {
        RequireSecurityHeader(context);

        if (request.Audience.Count == 0)
        {
            // The spec makes audience required. Minting an audience-less token would defeat the point
            // of audience binding, so this is InvalidArgument rather than a helpful default.
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "At least one audience is required for a JWT-SVID."));
        }

        // An optional spiffe_id narrows which identity to mint for. This agent holds exactly ONE, so a
        // request naming a different identity is refused rather than silently answered with the one we
        // have — a client that asked for workload B and received workload A's token would authenticate
        // as the wrong thing and only find out at the far end.
        var held = agent.CurrentSvid(_now());
        if (!string.IsNullOrEmpty(request.SpiffeId)
            && held is not null
            && !string.Equals(request.SpiffeId, held.SpiffeId, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"This agent holds '{held.SpiffeId}', not '{request.SpiffeId}'. "
                + "One agent serves one identity."));
        }

        var response = new JWTSVIDResponse();

        foreach (var audience in request.Audience)
        {
            JwtSvid token;
            try
            {
                token = await jwtSource.IssueAsync(audience, context.CancellationToken).ConfigureAwait(false);
            }
            catch (SvidAttestationException ex)
            {
                // The operator-facing message is preserved verbatim: it already says what to do, and
                // this may be the only place a sidecar's failure is visible.
                throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            }

            response.Svids.Add(new JWTSVID
            {
                SpiffeId = token.SpiffeId,
                Svid = token.Token,
            });
        }

        return response;
    }

    /// <inheritdoc />
    public override async Task FetchJWTBundles(
        JWTBundlesRequest request,
        IServerStreamWriter<JWTBundlesResponse> responseStream,
        ServerCallContext context)
    {
        RequireSecurityHeader(context);

        // Keyed by trust domain, which requires an identity to know what the trust domain IS. So this
        // waits for the first SVID exactly as the other streams do.
        await StreamAsync(
            context,
            async svid =>
            {
                var jwks = await jwtSource.FetchJwtBundleAsync(context.CancellationToken).ConfigureAwait(false);
                var response = new JWTBundlesResponse();
                response.Bundles.Add(
                    SvidWireFormat.TrustDomainId(svid.SpiffeId),
                    ByteString.CopyFrom(jwks, Encoding.UTF8));
                await responseStream.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<ValidateJWTSVIDResponse> ValidateJWTSVID(
        ValidateJWTSVIDRequest request, ServerCallContext context)
    {
        RequireSecurityHeader(context);

        // Unimplemented, and stated rather than faked. Validating a JWT-SVID means verifying a
        // signature against the JWT bundle — which a caller can do itself with `FetchJWTBundles`, using
        // its own language's JWT library. Returning a made-up "valid" here would be the single worst
        // possible stub in this file, and returning "invalid" would break callers that treat this as
        // authoritative. Unimplemented is the answer the gRPC contract has for exactly this.
        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "ValidateJWTSVID is not served by this agent. Fetch the JWT bundle via FetchJWTBundles and "
            + "validate the token locally."));
    }

    // ===== helpers =====

    /// <summary>
    /// Sends the current identity (waiting for one if needed), then sends again on every rotation,
    /// until the client goes away.
    /// </summary>
    private async Task StreamAsync(ServerCallContext context, Func<AttestedSvid, Task> send)
    {
        var ct = context.CancellationToken;

        // ONE subscription carries both the current identity and every later rotation: the agent
        // offers the held SVID to a new subscriber inside the same lock that attaches it, so the first
        // item to arrive is today's identity and nothing can slip between the read and the attach.
        //
        // That is why there is no "send the current one, then subscribe" here. Doing it in two steps
        // leaves a window where a rotation lands between them and is never delivered — the stream then
        // serves a stale identity until the NEXT rotation, which is the hardest possible version of
        // this bug to observe.
        try
        {
            await foreach (var svid in agent.SubscribeAsync(_now(), ct).WithCancellation(ct)
                               .ConfigureAwait(false))
            {
                await send(svid).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client disconnected. Normal: a Workload API stream ends when the workload stops, and
            // the subscription detaches itself on the way out (SvidSubscription's finally).
        }
    }

    private static X509SVIDResponse BuildX509Response(AttestedSvid svid)
    {
        var response = new X509SVIDResponse();
        response.Svids.Add(new X509SVID
        {
            SpiffeId = svid.SpiffeId,
            X509Svid = ByteString.CopyFrom(SvidWireFormat.CertificateChainDer(svid.Certificate)),
            X509SvidKey = ByteString.CopyFrom(SvidWireFormat.PrivateKeyPkcs8Der(svid.PrivateKey)),
            Bundle = ByteString.CopyFrom(SvidWireFormat.TrustBundleDer(svid.TrustBundle)),
        });
        return response;
    }

    private static X509BundlesResponse BuildBundleResponse(AttestedSvid svid)
    {
        var response = new X509BundlesResponse();
        response.Bundles.Add(
            SvidWireFormat.TrustDomainId(svid.SpiffeId),
            ByteString.CopyFrom(SvidWireFormat.TrustBundleDer(svid.TrustBundle)));
        return response;
    }

    private static void RequireSecurityHeader(ServerCallContext context)
    {
        var value = context.RequestHeaders.GetValue(SecurityHeader);
        if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"The SPIFFE Workload API requires the '{SecurityHeader}: true' metadata header. "
                + "Standard SPIFFE clients send it automatically."));
        }
    }
}
