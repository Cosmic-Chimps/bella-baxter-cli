using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T020 (US2) — the agent renews before expiry and never serves an expired SVID.
/// </summary>
/// <remarks>
/// <para><b>Why there is no clock here.</b> The obvious way to test "renews at 20% remaining" is to
/// issue a short SVID and wait. That test is slow, flaky under load, and — worst — passes for the
/// wrong reason when the timing is off by a little. So the DECISION is a pure function taking
/// <c>now</c>, and every case below is arithmetic: exactly at the boundary, a second either side,
/// already expired, a lifetime of zero, a clock that went backwards. The agent's background loop is
/// then glue over this, with nothing interesting left in it to get wrong.</para>
///
/// <para><b>The two properties that matter operationally.</b> An SVID must be replaced with enough
/// margin that several failures are harmless (FR-017), and an expired one must never be handed to a
/// consumer (FR-018) — presenting it would fail at the peer and look like a networking fault rather
/// than an identity one.</para>
/// </remarks>
public class SvidAgentRotationTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = IssuedAt.AddMinutes(60);

    // 20% of 60 minutes = 12 minutes, so the renewal point is T+48.
    private static readonly DateTimeOffset JustBeforeRenewal = IssuedAt.AddMinutes(47).AddSeconds(59);
    private static readonly DateTimeOffset AtRenewal = IssuedAt.AddMinutes(48);

    // ── the renewal boundary ─────────────────────────────────────────────

    [Fact]
    public void With_most_of_the_lifetime_left_the_SVID_is_kept()
    {
        Assert.Equal(
            SvidRenewalAction.Keep,
            SvidRenewalPolicy.Evaluate(IssuedAt, ExpiresAt, IssuedAt.AddMinutes(10)));
    }

    [Fact]
    public void One_second_before_the_renewal_point_the_SVID_is_still_kept()
    {
        // The precise boundary, asserted from below. A test that only checked "renews eventually"
        // would pass with a 50% threshold, or a 1% one, and neither is the documented contract.
        Assert.Equal(
            SvidRenewalAction.Keep,
            SvidRenewalPolicy.Evaluate(IssuedAt, ExpiresAt, JustBeforeRenewal));
    }

    [Fact]
    public void At_exactly_twenty_percent_remaining_renewal_is_due()
    {
        // Inclusive: at exactly 20% the SVID is due. Originally written the other way ("Keep at the
        // boundary, Renew one second later"), which read fine and made the agent's loop SPIN — see
        // BothViewsOfTheBoundaryAgree below for why the choice is forced rather than a matter of taste.
        Assert.Equal(
            SvidRenewalAction.Renew,
            SvidRenewalPolicy.Evaluate(IssuedAt, ExpiresAt, AtRenewal));
    }

    [Fact]
    public void Both_views_of_the_boundary_agree_at_every_instant()
    {
        // THE test that would have caught the spin, and the reason it exists as an invariant rather
        // than as more boundary cases.
        //
        // Two functions describe the same boundary: Evaluate says whether to act, TimeUntilRenewal
        // says how long until action is needed. If they ever disagree — Evaluate saying Keep while
        // TimeUntilRenewal says zero — the agent's loop asks "how long?", is told "no time at all",
        // does nothing because Evaluate said Keep, and asks again: a tight loop at 100% CPU for the
        // final fifth of every SVID's life. That shipped, and a hanging test found it rather than any
        // of the boundary assertions above, which were individually correct and jointly inconsistent.
        //
        // Swept across the whole lifetime and a margin either side, so no single instant can hide it.
        for (var offset = -5; offset <= 65; offset++)
        {
            var now = IssuedAt.AddMinutes(offset);
            var due = SvidRenewalPolicy.ActionIsDue(IssuedAt, ExpiresAt, now);
            var wait = SvidRenewalPolicy.TimeUntilRenewal(
                IssuedAt, ExpiresAt, now, maxSleep: TimeSpan.FromHours(1));

            Assert.Equal(due, wait == TimeSpan.Zero);
        }
    }

    [Fact]
    public void Past_expiry_the_action_is_Expired_and_not_merely_Renew()
    {
        // The distinction the whole file turns on. "Renew" means the SVID is still usable while a new
        // one is fetched; "Expired" means it must not be served at all. Collapsing them would let a
        // consumer receive a certificate every peer will reject.
        Assert.Equal(
            SvidRenewalAction.Expired,
            SvidRenewalPolicy.Evaluate(IssuedAt, ExpiresAt, ExpiresAt));

        Assert.Equal(
            SvidRenewalAction.Expired,
            SvidRenewalPolicy.Evaluate(IssuedAt, ExpiresAt, ExpiresAt.AddSeconds(1)));
    }

    [Fact]
    public void An_incoherent_lifetime_is_renewed_rather_than_divided_by()
    {
        // expiresAt at or before issuedAt. Real if a clock skews between issuer and agent. The
        // alternative to handling it is a division producing an infinity or a NaN, which would then
        // decide whether a workload has an identity.
        var backwards = SvidRenewalPolicy.Evaluate(IssuedAt, IssuedAt, IssuedAt.AddSeconds(-1));

        Assert.Equal(SvidRenewalAction.Renew, backwards);
    }

    [Fact]
    public void A_clock_that_jumped_backwards_does_not_force_a_renewal()
    {
        // `now` before `issuedAt` makes the remaining time exceed the lifetime. The SVID is younger
        // than expected, not older, so the correct answer is Keep — a renewal storm on every clock
        // correction would hammer the attest endpoint for no benefit.
        Assert.Equal(
            SvidRenewalAction.Keep,
            SvidRenewalPolicy.Evaluate(IssuedAt, ExpiresAt, IssuedAt.AddMinutes(-5)));
    }

    // ── sleep scheduling ─────────────────────────────────────────────────

    [Fact]
    public void The_boundary_invariant_holds_for_short_lifetimes_too()
    {
        // A 30-second SVID has a 6-second window, so every rounding decision lands within one tick of
        // the boundary. If the invariant only held for comfortable lifetimes, the spin would come back
        // for exactly the short TTLs this feature is meant to make viable.
        var expires = IssuedAt.AddSeconds(30);

        for (var second = -2; second <= 32; second++)
        {
            var now = IssuedAt.AddSeconds(second);
            var due = SvidRenewalPolicy.ActionIsDue(IssuedAt, expires, now);
            var wait = SvidRenewalPolicy.TimeUntilRenewal(
                IssuedAt, expires, now, maxSleep: TimeSpan.FromMinutes(5));

            Assert.Equal(due, wait == TimeSpan.Zero);
        }
    }

    [Fact]
    public void The_agent_sleeps_until_the_renewal_point_not_until_expiry()
    {
        // Sleeping until expiry would leave zero margin: the first failed attempt would be an outage.
        var wait = SvidRenewalPolicy.TimeUntilRenewal(
            IssuedAt, ExpiresAt, IssuedAt, maxSleep: TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(48), wait);
    }

    [Fact]
    public void A_long_lived_SVID_still_gets_a_periodic_check()
    {
        // The cap exists so a 24-hour SVID does not mean 19 hours of the agent never looking at
        // itself — a suspended laptop, a changed policy, a revoked identity all want noticing sooner.
        var wait = SvidRenewalPolicy.TimeUntilRenewal(
            IssuedAt, IssuedAt.AddHours(24), IssuedAt, maxSleep: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(5), wait);
    }

    [Fact]
    public void When_action_is_due_the_agent_does_not_sleep_at_all()
    {
        // Guards the arithmetic going negative and being trusted: a caller that slept on a negative
        // TimeSpan would either throw or sleep forever, and both look like a hung agent.
        Assert.Equal(
            TimeSpan.Zero,
            SvidRenewalPolicy.TimeUntilRenewal(
                IssuedAt, ExpiresAt, ExpiresAt.AddMinutes(1), TimeSpan.FromHours(1)));
    }

    // ── the agent itself ─────────────────────────────────────────────────

    [Fact]
    public async Task The_agent_attests_on_first_use_and_serves_the_result()
    {
        var source = new StubSvidSource(Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing"));
        var agent = new SvidAgent(source);

        Assert.Null(agent.CurrentSvid(IssuedAt));

        var rotated = await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        Assert.True(rotated);
        Assert.Equal("spiffe://t/p/e/billing", agent.CurrentSvid(IssuedAt)!.SpiffeId);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_healthy_SVID_is_not_re_attested()
    {
        // Otherwise every loop iteration would mint a new identity, which is both a load problem and
        // a correctness one: consumers holding the previous SVID would see it rotate for no reason.
        var source = new StubSvidSource(Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing"));
        var agent = new SvidAgent(source);
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        var rotated = await agent.EnsureFreshAsync(IssuedAt.AddMinutes(10), TestContext.Current.CancellationToken);

        Assert.False(rotated);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Inside_the_renewal_window_the_agent_rotates_without_a_restart()
    {
        // The headline claim of US2: short TTLs are operationally viable because renewal is invisible.
        var first = Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing");
        var second = Svid(AtRenewal.AddSeconds(1), AtRenewal.AddMinutes(61), "spiffe://t/p/e/billing");
        var source = new StubSvidSource(first, second);
        var agent = new SvidAgent(source);
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        var rotated = await agent.EnsureFreshAsync(AtRenewal.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(rotated);
        Assert.Equal(2, source.Calls);
        Assert.Equal(second.ExpiresAt, agent.CurrentSvid(AtRenewal.AddSeconds(1))!.ExpiresAt);
    }

    [Fact]
    public async Task Rotation_notifies_listeners_so_an_open_stream_can_push()
    {
        // The hook US6's FetchX509SVID stream reuses. A Workload API client holds the stream open and
        // expects a push when the identity changes; without this it would serve a stale SVID until the
        // client happened to reconnect.
        var first = Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing");
        var second = Svid(AtRenewal.AddSeconds(1), AtRenewal.AddMinutes(61), "spiffe://t/p/e/billing");
        var agent = new SvidAgent(new StubSvidSource(first, second));

        var pushed = new List<string>();
        agent.Rotated += svid => pushed.Add(svid.Certificate);

        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);
        await agent.EnsureFreshAsync(AtRenewal.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal([first.Certificate, second.Certificate], pushed);
    }

    [Fact]
    public async Task A_throwing_listener_does_not_break_the_rotation()
    {
        // A subscriber is a consumer of the identity, not a participant in issuing it. If a broken
        // stream client could fail a renewal, one bad consumer would take the workload's identity
        // down with it.
        var agent = new SvidAgent(new StubSvidSource(
            Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing")));

        agent.Rotated += _ => throw new InvalidOperationException("bad subscriber");
        var alsoCalled = false;
        agent.Rotated += _ => alsoCalled = true;

        var rotated = await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        Assert.True(rotated);
        Assert.True(alsoCalled, "a throwing subscriber must not stop the others being told");
        Assert.NotNull(agent.CurrentSvid(IssuedAt));
    }

    // ── expiry is never served (FR-018) ──────────────────────────────────

    [Fact]
    public async Task An_expired_SVID_is_never_handed_out()
    {
        var agent = new SvidAgent(new StubSvidSource(
            Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing")));
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        Assert.NotNull(agent.CurrentSvid(ExpiresAt.AddSeconds(-1)));
        Assert.Null(agent.CurrentSvid(ExpiresAt));
        Assert.Null(agent.CurrentSvid(ExpiresAt.AddHours(1)));
    }

    [Fact]
    public async Task A_failed_renewal_keeps_the_still_valid_SVID()
    {
        // The renewal window exists so failures are harmless. Discarding a valid identity because the
        // platform was briefly unreachable would turn a blip into an outage — and would do it at
        // precisely the moment the platform is already having a bad time.
        var agent = new SvidAgent(new StubSvidSource(
            Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing"))
        {
            FailAfterFirst = true,
        });
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.EnsureFreshAsync(AtRenewal.AddSeconds(1), TestContext.Current.CancellationToken));

        // Still serving, because it is still valid.
        Assert.NotNull(agent.CurrentSvid(AtRenewal.AddSeconds(1)));
    }

    [Fact]
    public async Task A_failed_renewal_past_expiry_serves_nothing()
    {
        // The other half: keeping an unexpired SVID through a failure is right, keeping an expired one
        // is not. After expiry the agent has no identity to offer and must say so.
        var agent = new SvidAgent(new StubSvidSource(
            Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing"))
        {
            FailAfterFirst = true,
        });
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.EnsureFreshAsync(ExpiresAt.AddMinutes(1), TestContext.Current.CancellationToken));

        Assert.Null(agent.CurrentSvid(ExpiresAt.AddMinutes(1)));
    }

    // ── status (T023's data, asserted here where it is cheap) ────────────

    [Fact]
    public async Task Status_distinguishes_no_SVID_from_an_expired_one()
    {
        // Different remedies: absent means attestation has never succeeded, expired means renewal is
        // failing. An operator shown one message for both would debug the wrong thing — the same
        // absent-versus-unreadable distinction spec 026 turned on.
        var agent = new SvidAgent(new StubSvidSource(
            Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing")));

        var before = agent.Describe(IssuedAt);
        Assert.Null(before.SpiffeId);
        Assert.False(before.IsExpired);

        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        var healthy = agent.Describe(IssuedAt.AddMinutes(1));
        Assert.Equal("spiffe://t/p/e/billing", healthy.SpiffeId);
        Assert.False(healthy.IsExpired);

        var expired = agent.Describe(ExpiresAt.AddMinutes(1));
        Assert.Equal("spiffe://t/p/e/billing", expired.SpiffeId);
        Assert.True(expired.IsExpired);
    }

    [Fact]
    public async Task Status_records_why_the_last_attestation_failed()
    {
        // "The agent has no identity" is not actionable. "Attestation was refused because the
        // bootstrap token is unknown" is.
        var agent = new SvidAgent(new StubSvidSource() { FailAfterFirst = true, FailFirst = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken));

        var status = agent.Describe(IssuedAt);
        Assert.Contains("attestation refused", status.LastFailure!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IssuedAt, status.LastAttemptAt);
    }

    [Fact]
    public async Task Status_never_carries_the_private_key()
    {
        // The status record is printed, logged and possibly pasted into a ticket. It has nowhere to put
        // a key by construction; this asserts that stays true.
        var agent = new SvidAgent(new StubSvidSource(
            Svid(IssuedAt, ExpiresAt, "spiffe://t/p/e/billing")));
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(agent.Describe(IssuedAt));

        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
    }

    // ===== helpers =====

    private static AttestedSvid Svid(DateTimeOffset issued, DateTimeOffset expires, string spiffeId) =>
        new(
            Certificate: $"-----BEGIN CERTIFICATE-----cert-{issued:O}-----END CERTIFICATE-----",
            PrivateKey: "-----BEGIN PRIVATE KEY-----k-----END PRIVATE KEY-----",
            TrustBundle: "-----BEGIN CERTIFICATE-----ca-----END CERTIFICATE-----",
            SpiffeId: spiffeId,
            IssuedAt: issued,
            ExpiresAt: expires);

    /// <summary>Serves a queued list of SVIDs, optionally failing.</summary>
    private sealed class StubSvidSource(params AttestedSvid[] queued) : ISvidSource
    {
        private int _index;

        public int Calls { get; private set; }

        /// <summary>Fail every attestation after the first successful one.</summary>
        public bool FailAfterFirst { get; init; }

        /// <summary>Fail the very first attestation too.</summary>
        public bool FailFirst { get; init; }

        public Task<AttestedSvid> AttestAsync(CancellationToken ct)
        {
            Calls++;

            if (FailFirst || (FailAfterFirst && Calls > 1))
            {
                throw new InvalidOperationException("Attestation refused: bootstrap token unknown.");
            }

            var svid = queued[Math.Min(_index, queued.Length - 1)];
            _index++;
            return Task.FromResult(svid);
        }
    }
}
