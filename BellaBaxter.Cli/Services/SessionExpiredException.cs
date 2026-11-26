using System.Text.RegularExpressions;

namespace BellaCli.Services;

/// <summary>What the token endpoint's refusal of a refresh means, which decides the sentence a person is told.</summary>
public enum SessionEndKind
{
    /// <summary>400 <c>invalid_grant</c>: the session is over (idle or maximum lifespan reached, revoked). Sign in again.</summary>
    Expired,

    /// <summary>401: the server does not accept this CLI's client (<c>unauthorized_client</c> / <c>invalid_client</c>): a server-side misconfiguration.</summary>
    ClientRejected,

    /// <summary>
    /// Any other 4xx (a 403 from a WAF, bot protection or proxy in front of the sign-in server, a 400 that is not
    /// <c>invalid_grant</c>, a 404, a 418 ...). It says nothing about the session, so it is never called an expiry.
    /// </summary>
    ServerRefused,
}

/// <summary>
/// The server REFUSED to renew the stored OAuth session. Thrown by <see cref="AuthService"/> when the token endpoint answers a
/// 4xx to the refresh, so a caller can tell "the credential is dead" apart from "the server is unreachable" without matching on
/// a message (see <see cref="ServerProbe.Classify(Exception)"/>). <see cref="Kind"/> says which refusal it was (#2002):
/// only <c>400 invalid_grant</c> is an expiry; Keycloak never answers 403, so a 403 is something in front of it.
///
/// <para>Derives from <see cref="InvalidOperationException"/> and its message IS the sentence for its kind, so the many
/// commands that <c>catch (InvalidOperationException ex)</c> and print <c>ex.Message</c> say the right thing, and
/// <see cref="Normalize"/> (used by both output writers) reduces any line that carries one of the sentences to exactly it.
/// The token endpoint's error body, a path, a query or a stack trace is never part of it; the host is the only part of the
/// address that is.</para>
/// </summary>
public sealed class SessionExpiredException : InvalidOperationException
{
    /// <summary>What a person is told when their session ran out. The same words on every path, stderr, exit 1.</summary>
    public const string Sentence = "Your session expired. Run `bella login`.";

    private SessionExpiredException(string message, SessionEndKind kind, int? status, string? host, Exception? inner)
        : base(message, inner)
    {
        Kind = kind;
        Status = status;
        Host = host;
    }

    public SessionEndKind Kind { get; }

    /// <summary>The token endpoint's HTTP status, for a caller that wants to branch; printed only inside the sentence.</summary>
    public int? Status { get; }

    /// <summary>The token endpoint's host (no path, no query), when the kind names one.</summary>
    public string? Host { get; }

    /// <summary>The machine code the JSON error carries for this kind.</summary>
    public string Code => Kind switch
    {
        SessionEndKind.Expired => "session_expired",
        SessionEndKind.ClientRejected => "session_client_rejected",
        _ => "session_refresh_refused",
    };

    /// <summary>The exception a refused refresh becomes when the server said <c>invalid_grant</c>. <paramref name="inner"/> is kept for a debugger, never printed.</summary>
    public static SessionExpiredException Refused(Exception? inner = null) =>
        new(Sentence, SessionEndKind.Expired, 400, null, inner);

    /// <summary>The server rejected this CLI's client (401): not an expiry.</summary>
    public static SessionExpiredException ClientRejected(string host, Exception? inner = null) =>
        new(ClientRejectedSentence(host), SessionEndKind.ClientRejected, 401, host, inner);

    /// <summary>Any other 4xx from the token endpoint: not an expiry.</summary>
    public static SessionExpiredException ServerRefused(int status, string host, Exception? inner = null) =>
        new(ServerRefusedSentence(status, host), SessionEndKind.ServerRefused, status, host, inner);

    public static string ClientRejectedSentence(string host) =>
        $"The sign-in server rejected this CLI's client (HTTP 401 from {host}). Run `bella login`; if it repeats, report it.";

    public static string ServerRefusedSentence(int status, string host) =>
        $"The sign-in server refused the refresh (HTTP {status} from {host}). Try again; if it repeats, run `bella login` and report it.";

    /// <summary>
    /// The exception, or the one it wraps, when a refused refresh is anywhere in the chain; null otherwise. A command that
    /// catches <see cref="Exception"/> and a handler that sees a wrapper both find it the same way.
    /// </summary>
    public static SessionExpiredException? Find(Exception? ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SessionExpiredException expired)
                return expired;
        }
        return null;
    }

    // The two sentences that carry a host and a status. The host is whatever the token endpoint's address named: no spaces, no ')'.
    private static readonly Regex ClientRejectedPattern = new(
        @"The sign-in server rejected this CLI's client \(HTTP 401 from [^\s)]+\)\. Run `bella login`; if it repeats, report it\.",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex ServerRefusedPattern = new(
        @"The sign-in server refused the refresh \(HTTP \d{3} from [^\s)]+\)\. Try again; if it repeats, run `bella login` and report it\.",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// A line a command built around a refused refresh ("Failed to list projects: Your session expired. …") is reduced to the
    /// sentence alone; any other message comes back unchanged. The prefix names a command's own step, which adds nothing for a
    /// person who only has to sign in again (or report the refusal).
    /// </summary>
    public static string Normalize(string message)
    {
        if (message.Contains(Sentence, StringComparison.Ordinal))
            return Sentence;
        var match = ClientRejectedPattern.Match(message);
        if (!match.Success)
            match = ServerRefusedPattern.Match(message);
        return match.Success ? match.Value : message;
    }
}
