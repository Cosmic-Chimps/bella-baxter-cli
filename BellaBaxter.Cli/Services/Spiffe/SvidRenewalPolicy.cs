namespace BellaCli.Services.Spiffe;

// Spec 001 T021 (US2) — when to re-attest.
//
// A PURE function, deliberately, and separated from the agent that calls it. The rule is "renew once
// less than 20% of the lifetime remains" (research R8, FR-017), and the interesting cases are all
// boundaries: exactly at 20%, one second either side, an already-expired SVID, a clock that jumped
// backwards, a lifetime so short that 20% is under a second. None of those want a background timer,
// a container, or a sleeping test to check — they want arithmetic with an argument for `now`.
//
// The agent's loop is then glue: ask this, act, wait. That split is what makes the rotation test in
// T020 a set of assertions rather than a race.

/// <summary>What the agent should do with the SVID it currently holds.</summary>
public enum SvidRenewalAction
{
    /// <summary>Plenty of life left — keep serving it.</summary>
    Keep,

    /// <summary>Inside the renewal window — re-attest, but the current SVID is still usable.</summary>
    Renew,

    /// <summary>Past expiry — re-attest, and the current SVID MUST NOT be served.</summary>
    Expired,
}

/// <summary>Decides when a held SVID needs replacing.</summary>
public static class SvidRenewalPolicy
{
    /// <summary>
    /// Renew once less than this fraction of the SVID's lifetime remains (FR-017).
    /// </summary>
    /// <remarks>
    /// 20% leaves four fifths of the lifetime as ordinary operation and the last fifth as the window
    /// in which several attempts can fail before anything breaks — on a 45-minute SVID that is nine
    /// minutes of retries. Raising it renews more often for no gain; lowering it narrows the margin
    /// for a platform that is briefly unreachable, which is the failure this window exists to absorb.
    /// </remarks>
    public const double RenewAtRemainingFraction = 0.20;

    /// <summary>
    /// What to do with an SVID issued at <paramref name="issuedAt"/> and expiring at
    /// <paramref name="expiresAt"/>, as of <paramref name="now"/>.
    /// </summary>
    public static SvidRenewalAction Evaluate(
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        // Expiry is checked FIRST and on its own. An expired SVID is not "very much due for renewal",
        // it is unusable — a workload that keeps presenting one gets rejected by every peer, and the
        // agent must stop serving it rather than serve it while a renewal is in flight.
        if (now >= expiresAt)
        {
            return SvidRenewalAction.Expired;
        }

        var lifetime = expiresAt - issuedAt;

        // A non-positive lifetime means the issuer handed back something incoherent (expiresAt at or
        // before issuedAt). Treated as due for renewal rather than dividing by it: the alternative is
        // an infinity or a NaN deciding whether a workload has an identity.
        if (lifetime <= TimeSpan.Zero)
        {
            return SvidRenewalAction.Renew;
        }

        var remaining = expiresAt - now;

        // A clock that jumped backwards can put `now` before `issuedAt`, making `remaining` exceed the
        // lifetime. That is not a reason to renew — the SVID is younger than we thought, not older —
        // so it falls out of the comparison below naturally rather than being special-cased.
        // `<=`, not `<`, and the equals matters more than it looks. TimeUntilRenewal computes the
        // renewal instant and returns Zero once it is reached; if Evaluate still said Keep at exactly
        // that instant, the agent's loop would ask "how long to wait" (zero), do nothing, and ask
        // again — a tight spin at 100% CPU for the whole final fifth of every SVID's life. Caught by a
        // hanging test, and the fix belongs here rather than as a floor in the loop: the two functions
        // must agree about one boundary, which is what BothViewsOfTheBoundaryAgree pins.
        return remaining <= lifetime * RenewAtRemainingFraction
            ? SvidRenewalAction.Renew
            : SvidRenewalAction.Keep;
    }

    /// <summary>
    /// True when <see cref="TimeUntilRenewal"/> must return zero — i.e. there is work to do now.
    /// </summary>
    /// <remarks>
    /// Exists so the invariant tying the two functions together can be asserted directly rather than
    /// inferred from a pair of boundary cases that happen to line up today.
    /// </remarks>
    public static bool ActionIsDue(
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset now) =>
        Evaluate(issuedAt, expiresAt, now) != SvidRenewalAction.Keep;

    /// <summary>
    /// How long the agent may sleep before it next needs to act, capped by
    /// <paramref name="maxSleep"/> so a long-lived SVID still gets a periodic health check.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="TimeSpan.Zero"/> when action is already due, so a caller cannot accidentally
    /// sleep past a renewal by trusting an arithmetic result that went negative.
    /// </remarks>
    public static TimeSpan TimeUntilRenewal(
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        TimeSpan maxSleep)
    {
        if (ActionIsDue(issuedAt, expiresAt, now))
        {
            return TimeSpan.Zero;
        }

        var lifetime = expiresAt - issuedAt;
        var renewAt = expiresAt - (lifetime * RenewAtRemainingFraction);
        var wait = renewAt - now;

        return wait <= TimeSpan.Zero ? TimeSpan.Zero : (wait < maxSleep ? wait : maxSleep);
    }
}
