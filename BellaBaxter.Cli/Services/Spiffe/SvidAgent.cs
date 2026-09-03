namespace BellaCli.Services.Spiffe;

// Spec 001 T021/T022 (US2) — the SVID agent's core.
//
// NAMING, because this is the second collision in one feature. `bella agent` is the shipped
// secrets-sync sidecar and keeps its name; the SVID agent is `bella spiffe agent`. Separately, the
// CLI already has a `WorkloadIdentityService` doing something else entirely (exchanging a platform
// OIDC token for a bax- API key), which is why this lives under Services/Spiffe/ with a name that
// says SVID rather than "workload identity".
//
// WHAT THIS IS AND IS NOT. This is the state machine: hold one SVID in memory, decide when it needs
// replacing, replace it, tell anyone listening. It is not a process, not a socket, and not a Spectre
// command — those are thin shells over this, so the behaviour that matters (does a rotation happen at
// the right moment, is an expired SVID ever served) is testable without spawning anything or waiting
// for a clock.
//
// IN MEMORY ONLY (FR-017). The certificate and its private key never touch disk. That is the whole
// point of short-lived identity: a key that is never written cannot be found later by someone reading
// the filesystem, and a workload that restarts simply attests again. There is deliberately no
// "cache the SVID" option, because the moment one exists someone will enable it to speed up restarts
// and reintroduce exactly the long-lived on-disk credential this feature replaces.

/// <summary>An SVID as issued by <c>/attest</c>, held in memory.</summary>
/// <param name="Certificate">Leaf certificate PEM, carrying the SPIFFE ID as a URI SAN.</param>
/// <param name="PrivateKey">The leaf's private key PEM. Never persisted.</param>
/// <param name="TrustBundle">The issuing CA, for verifying peers.</param>
/// <param name="SpiffeId">The identity this SVID asserts.</param>
/// <param name="IssuedAt">When the agent obtained it — the renewal window is measured from here.</param>
/// <param name="ExpiresAt">When it stops being valid.</param>
public sealed record AttestedSvid(
    string Certificate,
    string PrivateKey,
    string TrustBundle,
    string SpiffeId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Obtains a fresh SVID by attesting to Bella.</summary>
/// <remarks>
/// An interface rather than a concrete HTTP client so the agent's rotation behaviour can be tested
/// against a source that fails, succeeds, or returns a short-lived SVID on demand — none of which is
/// arrangeable against a real endpoint inside a unit test.
/// </remarks>
public interface ISvidSource
{
    /// <summary>Attests and returns a new SVID, or throws if attestation is refused.</summary>
    Task<AttestedSvid> AttestAsync(CancellationToken ct);
}

/// <summary>
/// Holds the current SVID, renews it before it expires, and notifies listeners on rotation.
/// </summary>
public sealed class SvidAgent(ISvidSource source)
{
    private readonly object _gate = new();
    private readonly List<SvidSubscription> _subscribers = [];
    private AttestedSvid? _current;
    private string? _lastFailure;
    private DateTimeOffset? _lastAttemptAt;

    /// <summary>Raised after a successful rotation, with the new SVID.</summary>
    /// <remarks>
    /// The hook US6's gRPC <c>FetchX509SVID</c> stream reuses (T022): a Workload API client holds an
    /// open stream and expects a push when the identity changes, so the rotation point is the only
    /// correct place to signal it. Handlers are invoked outside the lock — a slow or throwing
    /// subscriber must not be able to stall or break a renewal.
    /// </remarks>
    public event Action<AttestedSvid>? Rotated;

    /// <summary>
    /// The SVID to serve, or null when there is none fit to serve.
    /// </summary>
    /// <remarks>
    /// Returns null for an EXPIRED SVID as well as for no SVID at all. Those are different situations
    /// for an operator — hence <see cref="Describe"/> — but identical for a consumer: presenting an
    /// expired certificate fails at the peer, so handing one out would turn a clear "no identity yet"
    /// into a confusing handshake error somewhere else.
    /// </remarks>
    public AttestedSvid? CurrentSvid(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return null;
            }

            return SvidRenewalPolicy.Evaluate(_current.IssuedAt, _current.ExpiresAt, now)
                == SvidRenewalAction.Expired
                ? null
                : _current;
        }
    }

    /// <summary>
    /// Attests if there is no SVID, or renews if the held one is inside its renewal window. Returns
    /// true when a rotation happened.
    /// </summary>
    /// <remarks>
    /// The unit the agent's loop calls, and the unit the tests drive directly with a chosen
    /// <paramref name="now"/>. Keeping the DECISION here rather than in the loop is what makes
    /// "does it renew at 20%" an assertion instead of a timing test.
    ///
    /// <para>A failed renewal does NOT discard the SVID in hand. While it remains unexpired it is
    /// still the workload's valid identity, and dropping it because the platform was briefly
    /// unreachable would convert a recoverable blip into an outage — the renewal window exists
    /// precisely so several attempts can fail harmlessly.</para>
    /// </remarks>
    public async Task<bool> EnsureFreshAsync(DateTimeOffset now, CancellationToken ct)
    {
        AttestedSvid? held;
        lock (_gate)
        {
            held = _current;
        }

        if (held is not null
            && SvidRenewalPolicy.Evaluate(held.IssuedAt, held.ExpiresAt, now) == SvidRenewalAction.Keep)
        {
            return false;
        }

        AttestedSvid issued;
        try
        {
            issued = await source.AttestAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                _lastFailure = ex.Message;
                _lastAttemptAt = now;
            }

            // Rethrow so the caller decides. The loop logs and waits; a startup caller fails closed,
            // because a workload with no identity should not come up pretending to be healthy.
            throw;
        }

        SvidSubscription[] listeners;
        lock (_gate)
        {
            _current = issued;
            _lastFailure = null;
            _lastAttemptAt = now;

            // Offered under the SAME lock that publishes _current, which is what makes SubscribeAsync
            // race-free: a subscriber either sees this SVID as its initial value or receives it here,
            // never neither.
            listeners = [.. _subscribers];
            foreach (var listener in listeners)
            {
                listener.Offer(issued);
            }
        }

        // Outside the lock, and each handler isolated: a subscriber that throws must not prevent the
        // others from learning about a rotation that has already happened.
        foreach (var handler in Rotated?.GetInvocationList().Cast<Action<AttestedSvid>>() ?? [])
        {
            try
            {
                handler(issued);
            }
            catch
            {
                // A broken listener is its own problem. The SVID is rotated either way, and swallowing
                // here is deliberate rather than lazy: the alternative is one bad subscriber breaking
                // renewal for the whole process.
            }
        }

        return true;
    }

    /// <summary>
    /// True when the held SVID needs replacing (or there is none). The loop's floor uses this to tell
    /// "nothing to do" apart from "something to do but zero wait", which is the pair that spun.
    /// </summary>
    public bool NeedsAttention(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _current is null
                || SvidRenewalPolicy.ActionIsDue(_current.IssuedAt, _current.ExpiresAt, now);
        }
    }

    /// <summary>How long the agent may sleep before it next needs to act.</summary>
    public TimeSpan TimeUntilNextAction(DateTimeOffset now, TimeSpan maxSleep)
    {
        lock (_gate)
        {
            return _current is null
                ? TimeSpan.Zero
                : SvidRenewalPolicy.TimeUntilRenewal(
                    _current.IssuedAt, _current.ExpiresAt, now, maxSleep);
        }
    }

    /// <summary>
    /// Subscribes to the SVID: yields the current one immediately (if any), then each rotation.
    /// </summary>
    /// <remarks>
    /// <para>Spec 001 T022. The shape US6's <c>FetchX509SVID</c> stream needs, and the reason it is a
    /// method here rather than "read CurrentSvid then attach to Rotated" at each call site: that
    /// two-step has a RACE. A rotation landing between the read and the attach is missed entirely, and
    /// the consumer then serves the previous SVID until the NEXT rotation — up to a full lifetime of
    /// using an identity that has been replaced. Doing both under one lock closes it.</para>
    ///
    /// <para>An expired SVID is not yielded as the initial value, for the same reason
    /// <see cref="CurrentSvid"/> withholds it: a consumer that presented it would fail at its peer.
    /// The subscriber simply waits for the next rotation, which is already imminent by definition.</para>
    ///
    /// <para>The channel is unbounded but each write is a DROP-OLDEST replace: a stream consumer only
    /// ever wants the newest identity, so a slow reader should fall behind by zero rotations rather
    /// than accumulate a backlog of superseded SVIDs it will never usefully send.</para>
    /// </remarks>
    public IAsyncEnumerable<AttestedSvid> SubscribeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var subscription = new SvidSubscription();

        lock (_gate)
        {
            // Attach FIRST, then read, both inside the lock. A rotation cannot slip between them
            // because EnsureFreshAsync takes the same lock to publish.
            _subscribers.Add(subscription);

            if (_current is not null
                && SvidRenewalPolicy.Evaluate(_current.IssuedAt, _current.ExpiresAt, now)
                    != SvidRenewalAction.Expired)
            {
                subscription.Offer(_current);
            }
        }

        return subscription.ReadAllAsync(() => Detach(subscription), ct);
    }

    private void Detach(SvidSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }

    /// <summary>How many consumers are currently attached — shown by <c>bella spiffe status</c>.</summary>
    public int SubscriberCount
    {
        get { lock (_gate) { return _subscribers.Count; } }
    }

    /// <summary>A snapshot for <c>bella spiffe status</c> — never includes the private key.</summary>
    public SvidAgentStatus Describe(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return new SvidAgentStatus(null, null, null, false, _lastFailure, _lastAttemptAt);
            }

            var action = SvidRenewalPolicy.Evaluate(_current.IssuedAt, _current.ExpiresAt, now);

            return new SvidAgentStatus(
                _current.SpiffeId,
                _current.IssuedAt,
                _current.ExpiresAt,
                action == SvidRenewalAction.Expired,
                _lastFailure,
                _lastAttemptAt);
        }
    }
}

/// <summary>What the agent will tell an operator. Deliberately carries no key material.</summary>
/// <param name="SpiffeId">The identity held, or null if none.</param>
/// <param name="IssuedAt">When it was obtained.</param>
/// <param name="ExpiresAt">When it stops being valid.</param>
/// <param name="IsExpired">
/// True when an SVID is held but past expiry. Distinguished from "no SVID" because the remedies
/// differ: expired means renewal is failing, absent means attestation has not succeeded yet.
/// </param>
/// <param name="LastFailure">Why the last attestation failed, if it did.</param>
/// <param name="LastAttemptAt">When the agent last tried.</param>
public sealed record SvidAgentStatus(
    string? SpiffeId,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    bool IsExpired,
    string? LastFailure,
    DateTimeOffset? LastAttemptAt);
