using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T021 (US2) — the loop's own behaviour, over simulated hours, in milliseconds.
/// </summary>
/// <remarks>
/// The loop is thin because the decisions live elsewhere, so these cover only what is left to get
/// wrong — and each one is a real failure mode rather than a line of coverage:
/// startup must fail closed, a later failure must NOT kill the process, the wait must come from the
/// policy rather than a fixed interval, and a refusing endpoint must not be hammered.
///
/// <para>Time and sleeping are injected. The fake clock ADVANCES when the loop sleeps, which is what
/// makes a 60-minute lifetime testable in one call: the loop believes it waited, and the assertions
/// are on the recorded sequence rather than on wall-clock luck.</para>
/// </remarks>
public class SvidAgentLoopTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Wall-clock cap on every loop run. The fake clock means a correct loop finishes in microseconds,
    /// so this only ever fires on a spin — and it must FAIL rather than hang, because a hanging suite
    /// gives no signal about what broke. This exists because the loop shipped with exactly that spin
    /// and the first symptom was a test run that never returned.
    /// </summary>
    private static readonly TimeSpan SpinGuard = TimeSpan.FromSeconds(5);

    private static async Task RunGuardedAsync(SvidAgentLoop loop, CancellationToken ct)
    {
        var run = loop.RunAsync(ct);
        var finished = await Task.WhenAny(run, Task.Delay(SpinGuard, CancellationToken.None));

        Assert.True(
            ReferenceEquals(finished, run),
            $"the loop did not settle within {SpinGuard.TotalSeconds}s — it is almost certainly spinning");

        await run;
    }

    [Fact]
    public async Task A_failed_FIRST_attestation_stops_the_agent()
    {
        // Fail closed at startup. A workload that cannot prove what it is has no identity, and coming
        // up anyway would mean a deployment that looks healthy while every call it makes is
        // unauthenticated — the exact situation this feature exists to remove.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource { FailFirst = true });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunGuardedAsync(loop, TestContext.Current.CancellationToken));

        Assert.Empty(reporter.Rotations);
    }

    [Fact]
    public async Task A_failure_AFTER_startup_does_not_kill_the_loop()
    {
        // The 20% window's entire purpose: several attempts may fail harmlessly. A loop that exited on
        // the first failure would make the window worthless — the process would be gone before the
        // second attempt.
        var clock = new FakeClock(Start);
        var source = new StubSource
        {
            Queued = [Svid(Start, Start.AddMinutes(60))],
            FailAfterFirst = true,
        };
        var agent = new SvidAgent(source);
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterFailures(cts, 3);

        await RunGuardedAsync(loop, cts.Token);

        // Three failures survived, and the loop was still going when the test stopped it.
        Assert.Equal(3, reporter.Failures.Count);
        Assert.All(reporter.Failures, f => Assert.Contains("refused", f.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failure_reports_whether_the_workload_still_has_an_identity()
    {
        // The distinction an operator needs at 3am. The same message ("attestation refused") is noise
        // inside the renewal window and an outage after expiry, and only the loop knows which.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource
        {
            Queued = [Svid(Start, Start.AddMinutes(60))],
            FailAfterFirst = true,
        });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterFailures(cts, 1);
        await RunGuardedAsync(loop, cts.Token);

        // The first failure happens inside the renewal window, so the SVID is still good.
        Assert.True(Assert.Single(reporter.Failures).IdentityStillValid);
    }

    [Fact]
    public async Task The_wait_comes_from_the_policy_not_a_fixed_interval()
    {
        // A fixed poll interval is the obvious implementation and it is wrong, and the case that
        // proves it is an SVID SHORTER than the poll interval: a 2-minute SVID renews at T+1:36, so a
        // loop sleeping MaxSleep (5 min) would wake up three minutes after it expired.
        //
        // My first version of this test used a 10-minute SVID and asserted an 8-minute wait — which
        // failed, correctly, because 8 minutes is capped to MaxSleep. The cap is right there and the
        // expectation was wrong; the fix is to test the case the comment actually describes.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource
        {
            Queued =
            [
                Svid(Start, Start.AddMinutes(2)),
                Svid(Start.AddSeconds(96), Start.AddSeconds(216)),
            ],
        });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterRotations(cts, 2);
        await RunGuardedAsync(loop, cts.Token);

        var firstWait = reporter.Waits[0];

        // 20% of 2 minutes = 24s, so renewal is due at T+1:36.
        Assert.Equal(TimeSpan.FromSeconds(96), firstWait);
        Assert.True(firstWait < SvidAgentLoop.MaxSleep,
            "an SVID shorter than MaxSleep must be checked before MaxSleep elapses");

        // And it actually rotated BEFORE expiry, which is the operational claim.
        Assert.Equal(2, reporter.Rotations.Count);
    }

    [Fact]
    public async Task A_long_lived_SVID_is_still_checked_periodically()
    {
        // The cap. Without it a 24-hour SVID means 19 hours of the agent never looking at itself.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource
        {
            Queued = [Svid(Start, Start.AddHours(24))],
        });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterWaits(cts, 3);
        await RunGuardedAsync(loop, cts.Token);

        Assert.All(reporter.Waits, w => Assert.Equal(SvidAgentLoop.MaxSleep, w));
    }

    [Fact]
    public async Task A_refusing_endpoint_is_not_hammered()
    {
        // Between failures the loop waits. Without this it would spin as fast as the network allows —
        // turning its own inability to get an identity into load on a platform that is already
        // refusing, and tripping the per-environment lockout that protects the attest endpoint.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource
        {
            Queued = [Svid(Start, Start.AddMinutes(60))],
            FailAfterFirst = true,
        });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterFailures(cts, 2);
        await RunGuardedAsync(loop, cts.Token);

        // Asserted on the SPECIFIC delay, not the cumulative total. The first version of this test
        // checked `TotalSlept >= RetryDelay` and a mutation removing the retry pause entirely still
        // passed it — because the loop's ordinary policy waits already exceed that. A test that cannot
        // fail is worse than no test: it reports coverage of a guard that is not there.
        var retryPauses = clock.Delays.Count(d => d == SvidAgentLoop.RetryDelay);

        // Every failure but the LAST is followed by a pause. The last one is not, because the test
        // cancels on it and the pause is cancelled with it — asserting an exact equality here would be
        // asserting the test's own cancellation timing rather than the loop's behaviour.
        Assert.True(
            retryPauses >= reporter.Failures.Count - 1,
            $"{reporter.Failures.Count} failures produced only {retryPauses} pauses — the loop is retrying without waiting");
    }

    [Fact]
    public async Task A_rotation_is_reported_so_an_operator_can_see_it_happen()
    {
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource
        {
            Queued = [Svid(Start, Start.AddMinutes(10)), Svid(Start.AddMinutes(8), Start.AddMinutes(18))],
        });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterRotations(cts, 2);
        await RunGuardedAsync(loop, cts.Token);

        Assert.Equal(2, reporter.Rotations.Count);
        Assert.All(reporter.Rotations, s => Assert.Equal("spiffe://t/p/e/billing", s.SpiffeId));
    }

    [Fact]
    public async Task Cancellation_is_a_clean_shutdown_not_an_error()
    {
        // A sidecar gets SIGTERM on every ordinary pod termination, rolling update and scale-down. If
        // that surfaced as an unhandled exception, the agent would print a stack trace on every normal
        // stop — and an operator who sees a trace at every shutdown stops reading the logs, which is
        // how the real error later goes unnoticed.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource
        {
            Queued = [Svid(Start, Start.AddHours(24))],
        });
        var reporter = new RecordingReporter();
        var loop = new SvidAgentLoop(agent, reporter, clock.Now, clock.DelayAsync);

        using var cts = new CancellationTokenSource();
        reporter.CancelAfterWaits(cts, 1);

        // No throw, and no Assert.ThrowsAsync wrapper: returning normally IS the assertion.
        await RunGuardedAsync(loop, cts.Token);

        Assert.Single(reporter.Rotations);
    }

    [Fact]
    public async Task A_startup_FAILURE_still_propagates_even_though_cancellation_does_not()
    {
        // The distinction that makes the clean-shutdown behaviour safe: swallowing cancellation must
        // not become swallowing everything. A workload whose first attestation is refused has no
        // identity, and the process must fail rather than sit there looking alive.
        var clock = new FakeClock(Start);
        var agent = new SvidAgent(new StubSource { FailFirst = true });
        var loop = new SvidAgentLoop(agent, new RecordingReporter(), clock.Now, clock.DelayAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunGuardedAsync(loop, TestContext.Current.CancellationToken));
    }

    // ===== helpers =====

    private static AttestedSvid Svid(DateTimeOffset issued, DateTimeOffset expires) =>
        new($"cert-{issued:O}", "key", "ca", "spiffe://t/p/e/billing", issued, expires);

    /// <summary>A clock that advances when the code under test sleeps.</summary>
    private sealed class FakeClock(DateTimeOffset start)
    {
        private DateTimeOffset _now = start;

        public TimeSpan TotalSlept { get; private set; }

        /// <summary>
        /// Every delay the code under test asked for, in order.
        /// </summary>
        /// <remarks>
        /// The total is not enough, and a surviving mutation proved it: the loop sleeps for the policy
        /// wait between iterations, so a cumulative assertion passes whether or not the retry pause
        /// ever happened. Recording each duration is what makes "it paused BETWEEN FAILURES" checkable.
        /// </remarks>
        public List<TimeSpan> Delays { get; } = [];

        public DateTimeOffset Now() => _now;

        public Task DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _now += duration;
            TotalSlept += duration;
            Delays.Add(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class StubSource : ISvidSource
    {
        private int _index;

        public AttestedSvid[] Queued { get; init; } = [];
        public bool FailFirst { get; init; }
        public bool FailAfterFirst { get; init; }
        public int Calls { get; private set; }

        public Task<AttestedSvid> AttestAsync(CancellationToken ct)
        {
            Calls++;
            if (FailFirst || (FailAfterFirst && Calls > 1))
            {
                throw new InvalidOperationException("Attestation refused: selectors did not match.");
            }

            var svid = Queued[Math.Min(_index, Queued.Length - 1)];
            _index++;
            return Task.FromResult(svid);
        }
    }

    private sealed record RecordedFailure(string Reason, bool IdentityStillValid);

    /// <summary>
    /// Records what the loop reported, and stops it once enough has happened.
    /// </summary>
    /// <remarks>
    /// Cancelling from the reporter rather than after a timeout is what keeps these tests
    /// deterministic: the loop runs exactly as far as the assertion needs and not one iteration more.
    /// </remarks>
    private sealed class RecordingReporter : ISvidAgentReporter
    {
        private CancellationTokenSource? _cts;
        private int _stopAfterFailures = int.MaxValue;
        private int _stopAfterRotations = int.MaxValue;
        private int _stopAfterWaits = int.MaxValue;

        public List<AttestedSvid> Rotations { get; } = [];
        public List<RecordedFailure> Failures { get; } = [];
        public List<TimeSpan> Waits { get; } = [];

        public void CancelAfterFailures(CancellationTokenSource cts, int count)
        {
            _cts = cts;
            _stopAfterFailures = count;
        }

        public void CancelAfterRotations(CancellationTokenSource cts, int count)
        {
            _cts = cts;
            _stopAfterRotations = count;
        }

        public void CancelAfterWaits(CancellationTokenSource cts, int count)
        {
            _cts = cts;
            _stopAfterWaits = count;
        }

        public void Rotated(AttestedSvid svid)
        {
            Rotations.Add(svid);
            if (Rotations.Count >= _stopAfterRotations) _cts?.Cancel();
        }

        public void AttestationFailed(string reason, bool identityStillValid)
        {
            Failures.Add(new RecordedFailure(reason, identityStillValid));
            if (Failures.Count >= _stopAfterFailures) _cts?.Cancel();
        }

        public void Waiting(TimeSpan duration)
        {
            Waits.Add(duration);
            if (Waits.Count >= _stopAfterWaits) _cts?.Cancel();
        }
    }
}