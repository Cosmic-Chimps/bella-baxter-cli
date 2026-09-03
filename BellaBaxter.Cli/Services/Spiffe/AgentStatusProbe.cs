using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BellaCli.Services.Spiffe.WorkloadApi;
using Grpc.Core;
using Grpc.Net.Client;

namespace BellaCli.Services.Spiffe;

// Spec 001 T023 (US2) — the half of `bella spiffe status` that was structurally blocked.
//
// WHY IT COULD NOT BE WRITTEN BEFORE. `status` reports on an agent running in ANOTHER process: a
// sidecar in the same pod, or a daemon on the host. The information lives in that process's memory
// (SvidAgent), so until the agent exposed a local endpoint there was nothing to ask. That endpoint now
// exists (T042), and `status` is a client of it — which is the right shape anyway: it uses exactly the
// same interface a workload uses, so if `status` says the identity is fine, a workload will agree.
//
// IT ASKS, IT DOES NOT GUESS. There is no shared file, no pid file, no state directory. The only
// question is "does something answer the Workload API on this socket", and the answer distinguishes
// four cases an operator acts on differently:
//
//   NoSocket    — nothing at the path. The agent is not running, or is running with a different
//                 --socket / SPIFFE_ENDPOINT_SOCKET. Both are configuration.
//   NotServing  — the socket file is there but nothing accepts. A crashed agent left it behind; the
//                 next start will clear it. Distinct from NoSocket because it means the agent DID run.
//   NoIdentity  — an agent is serving but has no SVID yet. Attestation has not succeeded. This is the
//                 one that used to be indistinguishable from "healthy" in a naive check.
//   Serving     — an identity is held; the countdown is real.
//
// Collapsing any pair of these into "not running" is what makes an operator restart a healthy agent.

/// <summary>What the local Workload API endpoint reports.</summary>
public enum AgentStatusKind
{
    /// <summary>No socket file at the resolved path.</summary>
    NoSocket,

    /// <summary>A socket file exists but nothing is accepting connections on it.</summary>
    NotServing,

    /// <summary>An agent is serving, but holds no SVID yet.</summary>
    NoIdentity,

    /// <summary>An agent is serving a current identity.</summary>
    Serving,
}

/// <summary>The observed state of a local agent.</summary>
/// <param name="Kind">Which of the four situations this is.</param>
/// <param name="SocketPath">Where we looked.</param>
/// <param name="SocketSource">How that path was chosen — usually the actual problem.</param>
/// <param name="SpiffeId">The identity served, when there is one.</param>
/// <param name="ExpiresAt">When it stops being valid.</param>
/// <param name="TrustDomain">The trust domain the bundle is keyed by.</param>
/// <param name="Detail">Anything else worth printing, e.g. a gRPC failure message.</param>
public sealed record AgentStatus(
    AgentStatusKind Kind,
    string SocketPath,
    SvidSocketPathSource SocketSource,
    string? SpiffeId = null,
    DateTimeOffset? ExpiresAt = null,
    string? TrustDomain = null,
    string? Detail = null)
{
    /// <summary>How long the served identity has left, or null when there is none.</summary>
    public TimeSpan? TimeRemaining(DateTimeOffset now) =>
        ExpiresAt is null ? null : ExpiresAt.Value - now;

    /// <summary>What an operator should do about it. Null when nothing is wrong.</summary>
    public string? Advice => Kind switch
    {
        AgentStatusKind.NoSocket =>
            $"No agent is listening at {SocketPath} (path chosen from {SocketSource}). Start one with "
            + "'bella spiffe agent', or point this command at the right path with --socket / "
            + "SPIFFE_ENDPOINT_SOCKET.",
        AgentStatusKind.NotServing =>
            $"A socket file exists at {SocketPath} but nothing is accepting connections — an agent ran "
            + "here and stopped without cleaning up. Starting a new agent will clear it.",
        AgentStatusKind.NoIdentity =>
            "The agent is running but holds no SVID: attestation has not succeeded. Run "
            + "'bella spiffe whoami' to check the node evidence this host can present, and check the "
            + "agent's own output for the refusal reason.",
        _ => null,
    };
}

/// <summary>Asks a local agent what it is serving, over its own Workload API.</summary>
public sealed class AgentStatusProbe(TimeSpan? timeout = null)
{
    // Short by design. This is an interactive command against a socket on the same host: if an agent
    // is there it answers immediately, and a long timeout only makes a dead agent feel like a hang.
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);

    /// <summary>Probes the endpoint and reports what it found.</summary>
    public async Task<AgentStatus> ProbeAsync(SvidSocketLocation location, CancellationToken ct)
    {
        if (!File.Exists(location.Path))
        {
            return new AgentStatus(AgentStatusKind.NoSocket, location.Path, location.Source);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancel) =>
                {
                    var socket = new Socket(
                        AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(location.Path), cancel)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            },
        });

        var client = new SpiffeWorkloadAPI.SpiffeWorkloadAPIClient(channel);
        var headers = new Metadata { { WorkloadApiService.SecurityHeader, "true" } };

        try
        {
            using var call = client.FetchX509SVID(
                new X509SVIDRequest(), headers, cancellationToken: deadline.Token);

            if (!await call.ResponseStream.MoveNext(deadline.Token).ConfigureAwait(false))
            {
                return new AgentStatus(
                    AgentStatusKind.NoIdentity, location.Path, location.Source,
                    Detail: "The agent closed the stream without sending an identity.");
            }

            var svid = call.ResponseStream.Current.Svids.FirstOrDefault();
            if (svid is null)
            {
                return new AgentStatus(AgentStatusKind.NoIdentity, location.Path, location.Source);
            }

            // Expiry is read from the CERTIFICATE, not from anything the agent says about it. That is
            // the number a peer will enforce, and reading it here means status cannot disagree with
            // reality even if the agent's own bookkeeping were wrong.
            using var leaf = X509CertificateLoader.LoadCertificate(svid.X509Svid.ToByteArray());
            var expires = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);

            return new AgentStatus(
                AgentStatusKind.Serving,
                location.Path,
                location.Source,
                SpiffeId: svid.SpiffeId,
                ExpiresAt: expires,
                TrustDomain: SafeTrustDomain(svid.SpiffeId));
        }
        // ORDER MATTERS HERE, and getting it wrong is a real bug this caught in review-by-test.
        //
        // gRPC does not surface a client-side timeout as a bare OperationCanceledException — it wraps it
        // as RpcException(Cancelled). With the RpcException clause first, a healthy agent that simply
        // had not attested yet was reported as a STALE SOCKET, which sends the operator to restart a
        // process that is working and ignore the attestation failure that is not. Precisely the
        // conflation this class exists to prevent.
        //
        // Both spellings of "our deadline elapsed" are therefore matched BEFORE the transport clause,
        // and both mean NoIdentity: the connection SUCCEEDED, so something is there — it just produced
        // no SVID in the window, which is what a fresh agent still attesting looks like.
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded
                                      && !ct.IsCancellationRequested)
        {
            return new AgentStatus(
                AgentStatusKind.NoIdentity, location.Path, location.Source,
                Detail: $"The agent accepted the connection but sent no identity within {_timeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AgentStatus(
                AgentStatusKind.NoIdentity, location.Path, location.Source,
                Detail: $"The agent accepted the connection but sent no identity within {_timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is RpcException or SocketException or IOException or HttpRequestException)
        {
            // A socket file whose owner is gone. Kept distinct from "no socket" because it tells the
            // operator the agent DID run here, which changes what they check next.
            return new AgentStatus(
                AgentStatusKind.NotServing, location.Path, location.Source, Detail: ex.Message);
        }
    }

    private static string? SafeTrustDomain(string spiffeId)
    {
        try
        {
            return SvidWireFormat.TrustDomainId(spiffeId);
        }
        catch (InvalidOperationException)
        {
            // A malformed id is worth reporting as an identity anyway — status exists to show what IS
            // being served, including something wrong. Throwing here would hide the whole answer.
            return null;
        }
    }
}
