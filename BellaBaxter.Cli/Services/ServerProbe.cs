using Microsoft.Kiota.Abstractions;

namespace BellaCli.Services;

/// <summary>
/// Why a call that was supposed to confirm an identity did not.
/// </summary>
public enum ServerProbeFailure
{
    /// <summary>The server answered and refused the credential (401/403) — it is dead.</summary>
    Rejected,

    /// <summary>We never got an answer, or got one that says nothing about the credential.</summary>
    Unreachable,
}

/// <summary>
/// Pilot F9: <c>bella whoami</c> printed a cached identity and exited 0 for a revoked key and for
/// an unreachable server alike, because the only handling was <c>catch { return null; }</c>. Those
/// are different facts and the operator acts differently on each: "your credential is dead, log in
/// again" versus "I could not reach the server, try <c>--offline</c>".
///
/// This is the single place that decides which one an exception is, so no call site has to guess.
/// </summary>
public static class ServerProbe
{
    /// <summary>
    /// A server that answered 401 or 403 has judged the credential. Anything else — a timeout, DNS
    /// failure, a 500, a proxy error — says nothing about it, and must never be reported as a
    /// revocation: that sends the operator to re-authenticate over an outage.
    /// </summary>
    public static ServerProbeFailure Classify(int? httpStatusCode) =>
        httpStatusCode is 401 or 403 ? ServerProbeFailure.Rejected : ServerProbeFailure.Unreachable;

    /// <summary>Classifies whatever the generated client or the refresh handler threw.</summary>
    public static ServerProbeFailure Classify(Exception ex) =>
        ex switch
        {
            // The refresh handler already tried and failed to renew the session.
            SessionExpiredException => ServerProbeFailure.Rejected,
            ApiException api => Classify(api.ResponseStatusCode),
            _ => ServerProbeFailure.Unreachable,
        };
}
