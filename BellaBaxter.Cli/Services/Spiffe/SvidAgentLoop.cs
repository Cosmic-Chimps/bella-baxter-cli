namespace BellaCli.Services.Spiffe;

// Spec 001 T021 (US2) — the background cadence.
//
// Thin on purpose. Every decision worth testing already lives in SvidRenewalPolicy (when) and
// SvidAgent (what), so this is: ask, act, report, wait, repeat. What is left to get wrong is
// therefore small and specific, and each of those things has a test:
//
//   - a failed renewal must not kill the loop (the whole point of the 20% window is that failures
//     are survivable, so the process must still be there to try again);
//   - the wait must come from the policy, not from a fixed interval, or a short-TTL SVID expires
//     between checks;
//   - a failure must be reported once per attempt rather than spun on — a tight retry loop against a
//     refusing endpoint is how an agent turns its own outage into the platform's.
//
// Time and sleeping are injected, so the loop's behaviour over hours is a test that runs in
// milliseconds and asserts on a recorded sequence rather than on wall-clock luck.

/// <summary>What the loop tells the operator as it runs.</summary>
public interface ISvidAgentReporter
{
    /// <summary>A new SVID is in hand.</summary>
    void Rotated(AttestedSvid svid);

    /// <summary>An attestation attempt failed. The loop continues.</summary>
    void AttestationFailed(string reason, bool identityStillValid);

    /// <summary>The loop is about to wait.</summary>
    void Waiting(TimeSpan duration);
}

/// <summary>Drives an <see cref="SvidAgent"/> on the cadence its policy asks for.</summary>
public sealed class SvidAgentLoop(
    SvidAgent agent,
    ISvidAgentReporter reporter,
    Func<DateTimeOffset> now,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>
    /// Longest the loop will sleep before looking at itself again, however long the SVID lives.
    /// </summary>
    /// <remarks>
    /// A 24-hour SVID would otherwise mean 19 hours of not noticing a suspended machine, a revoked
    /// identity, or a clock that moved. Five minutes is short enough to catch those and long enough
    /// that the loop is not a source of load.
    /// </remarks>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Waited after a failed attestation before trying again.
    /// </summary>
    /// <remarks>
    /// Deliberately a flat, short pause and NOT exponential backoff. Inside the renewal window there
    /// is a bounded, known amount of time to succeed in — on a 45-minute SVID, nine minutes — and
    /// backing off exponentially would spend that budget waiting rather than retrying. The public
    /// attest endpoint has its own per-environment rate limiting and lockout (spec clarification
    /// 2026-06-19), so restraint belongs on the server where it can see every client, not here where
    /// it can only see one.
    /// </remarks>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Floor on an idle iteration, so a policy/agent disagreement cannot become a busy loop.
    /// </summary>
    public static readonly TimeSpan MinimumIdleWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Attests once, then keeps the SVID fresh until cancelled.
    /// </summary>
    /// <remarks>
    /// The FIRST attestation is allowed to throw. A workload that cannot prove what it is has no
    /// identity, and coming up anyway — logging a warning and serving nothing — is how a deployment
    /// looks healthy while every call it makes is unauthenticated. Fail closed at startup; survive
    /// failures afterwards, because by then there is a valid SVID to keep serving.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is a SHUTDOWN, not a failure. A sidecar receives SIGTERM on every ordinary
            // pod termination, rolling update and scale-down; letting that surface as an unhandled
            // exception would print a stack trace on every normal stop. Operators then learn that the
            // agent's logs are noise, which is exactly how a real error later goes unread.
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        // Startup: propagate. Nothing to fall back on, so nothing to keep running for.
        await agent.EnsureFreshAsync(now(), ct).ConfigureAwait(false);
        reporter.Rotated(agent.CurrentSvid(now())!);

        while (!ct.IsCancellationRequested)
        {
            var wait = agent.TimeUntilNextAction(now(), MaxSleep);

            // Defence in depth against the bug this loop shipped with for an hour: the policy returned
            // Zero at the renewal instant while Evaluate still said Keep, so EnsureFreshAsync did
            // nothing and the loop asked again immediately — a tight spin for the last fifth of every
            // SVID's life. Fixed at the source (the two now agree on one boundary, pinned by a test),
            // and floored here as well: a future disagreement should make the agent slow, never make
            // it burn a core. Never a substitute for the invariant, and the comment says so.
            if (wait <= TimeSpan.Zero && !agent.NeedsAttention(now()))
            {
                await delay(MinimumIdleWait, ct).ConfigureAwait(false);
                continue;
            }

            if (wait > TimeSpan.Zero)
            {
                reporter.Waiting(wait);
                await delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (await agent.EnsureFreshAsync(now(), ct).ConfigureAwait(false))
                {
                    reporter.Rotated(agent.CurrentSvid(now())!);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Survivable by design. Reported with whether the workload still HAS an identity,
                // because those are different situations: a failure inside the renewal window is
                // noise, and the same failure after expiry is an outage.
                var stillValid = agent.CurrentSvid(now()) is not null;
                reporter.AttestationFailed(ex.Message, stillValid);

                await delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
