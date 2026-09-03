using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T022 (US2) — the local serving surface US6's <c>FetchX509SVID</c> stream sits on.
/// </summary>
/// <remarks>
/// The interesting properties are all about the seams, not the happy path: a rotation landing between
/// "read the current SVID" and "attach for updates" must not be lost, a consumer that disconnects
/// mid-await must not leak, and a slow consumer must receive the NEWEST identity rather than work
/// through superseded ones.
///
/// <para>Every test carries an explicit <c>Timeout</c>. These exercise async streams, where the
/// classic mistake — disposing an enumerator with a <c>MoveNextAsync</c> still in flight — deadlocks
/// rather than fails. It cost two stalled suite runs while writing this file, and a stalled run says
/// nothing about which test broke.</para>
/// </remarks>
public class SvidSubscriptionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 10_000)]
    public async Task A_subscriber_receives_the_current_SVID_immediately()
    {
        // Otherwise a consumer starting up after the agent would block until the first rotation —
        // potentially most of a lifetime — while a perfectly good identity sat in memory.
        var agent = new SvidAgent(new QueueSource(Svid(Start, Start.AddMinutes(60), "first")));
        await agent.EnsureFreshAsync(Start, Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var received = new List<string>();

        await foreach (var svid in agent.SubscribeAsync(Start, cts.Token))
        {
            received.Add(svid.Certificate);
            break;
        }

        Assert.Equal(["first"], received);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_subscriber_attached_before_the_first_SVID_receives_it_on_arrival()
    {
        // The agent may not have attested yet when a consumer connects — a sidecar and its application
        // start together. The subscriber must wait rather than be told "no identity" and give up.
        var agent = new SvidAgent(new QueueSource(Svid(Start, Start.AddMinutes(60), "first")));

        var stream = agent.SubscribeAsync(Start, Ct).GetAsyncEnumerator(Ct);
        var pending = stream.MoveNextAsync();

        Assert.False(pending.IsCompleted, "nothing to yield before the agent has attested");

        await agent.EnsureFreshAsync(Start, Ct);

        // Awaited before disposing — the pending read completes here, so the dispose below is safe.
        Assert.True(await pending);
        Assert.Equal("first", stream.Current.Certificate);
        await stream.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task A_rotation_between_reading_and_attaching_is_NOT_lost()
    {
        // THE race this method exists to close. Written as "read CurrentSvid, then += Rotated" at the
        // call site, a rotation landing between the two steps is missed entirely — and the consumer
        // then serves the PREVIOUS identity until the next rotation, i.e. up to a full lifetime of
        // using an SVID that has been replaced.
        //
        // The property is therefore "the consumer ends up on the NEWEST identity", not "it sees every
        // intermediate one". My first version of this test asserted first-then-second and hung: it
        // encoded QUEUE semantics against a deliberately DROP-OLDEST channel. Both goals are real but
        // they point at different assertions, and this is the one that matters — a superseded SVID is
        // of no use to whoever receives it.
        var agent = new SvidAgent(new QueueSource(
            Svid(Start, Start.AddMinutes(60), "first"),
            Svid(Start.AddMinutes(48), Start.AddMinutes(108), "second")));
        await agent.EnsureFreshAsync(Start, Ct);

        var stream = agent.SubscribeAsync(Start, Ct).GetAsyncEnumerator(Ct);

        // Rotate straight away — the window the race would live in.
        await agent.EnsureFreshAsync(Start.AddMinutes(48), Ct);

        // Something arrives (the race would yield nothing), and it is the CURRENT identity.
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("second", stream.Current.Certificate);

        await stream.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task An_EXPIRED_SVID_is_not_offered_as_the_initial_value()
    {
        // Same rule as CurrentSvid. A consumer handed an expired certificate fails at its peer, and
        // the resulting error names TLS rather than identity. Waiting for the imminent rotation is
        // strictly better than being given something that cannot work.
        var agent = new SvidAgent(new QueueSource(Svid(Start, Start.AddMinutes(60), "first")));
        await agent.EnsureFreshAsync(Start, Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var stream = agent
            .SubscribeAsync(Start.AddMinutes(61), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var pending = stream.MoveNextAsync();

        Assert.False(pending.IsCompleted, "an expired SVID must not be yielded");

        // Release the outstanding read BEFORE disposing. Disposing an async enumerator with a
        // MoveNextAsync still in flight deadlocks — which is how the first version of this test hung
        // the whole suite rather than failing.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await stream.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task Every_subscriber_receives_every_rotation()
    {
        // Several consumers is the normal case: an app, a sidecar proxy, spiffe-helper. One of them
        // reading must not consume the update the others need.
        var agent = new SvidAgent(new QueueSource(
            Svid(Start, Start.AddMinutes(60), "first"),
            Svid(Start.AddMinutes(48), Start.AddMinutes(108), "second")));
        await agent.EnsureFreshAsync(Start, Ct);

        var a = agent.SubscribeAsync(Start, Ct).GetAsyncEnumerator(Ct);
        var b = agent.SubscribeAsync(Start, Ct).GetAsyncEnumerator(Ct);

        Assert.True(await a.MoveNextAsync());
        Assert.True(await b.MoveNextAsync());
        Assert.Equal("first", a.Current.Certificate);
        Assert.Equal("first", b.Current.Certificate);

        await agent.EnsureFreshAsync(Start.AddMinutes(48), Ct);

        Assert.True(await a.MoveNextAsync());
        Assert.True(await b.MoveNextAsync());
        Assert.Equal("second", a.Current.Certificate);
        Assert.Equal("second", b.Current.Certificate);

        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task A_slow_consumer_gets_the_NEWEST_SVID_not_a_backlog()
    {
        // Drop-oldest. A superseded SVID is useless to whoever eventually reads it, so a reader that
        // falls behind should skip to the present rather than work through history — and the queue
        // must not grow while every entry in it is already wrong.
        var agent = new SvidAgent(new QueueSource(
            Svid(Start, Start.AddMinutes(60), "first"),
            Svid(Start.AddMinutes(48), Start.AddMinutes(108), "second"),
            Svid(Start.AddMinutes(96), Start.AddMinutes(156), "third")));
        await agent.EnsureFreshAsync(Start, Ct);

        var stream = agent.SubscribeAsync(Start, Ct).GetAsyncEnumerator(Ct);

        // Three rotations while nobody reads.
        await agent.EnsureFreshAsync(Start.AddMinutes(48), Ct);
        await agent.EnsureFreshAsync(Start.AddMinutes(96), Ct);

        Assert.True(await stream.MoveNextAsync());

        Assert.Equal("third", stream.Current.Certificate);
        await stream.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task A_disconnected_consumer_does_not_leak()
    {
        // A cancelled gRPC stream disconnects mid-await, which is the normal way a Workload API client
        // goes away. Detaching only after a clean loop exit would leave it in the subscriber list
        // forever, and the agent would keep offering to a channel nobody reads — an unbounded leak in
        // a long-running sidecar, driven by ordinary client churn.
        var agent = new SvidAgent(new QueueSource(Svid(Start, Start.AddMinutes(60), "first")));
        await agent.EnsureFreshAsync(Start, Ct);

        Assert.Equal(0, agent.SubscriberCount);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var stream = agent.SubscribeAsync(Start, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(1, agent.SubscriberCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
        await stream.DisposeAsync();

        Assert.Equal(0, agent.SubscriberCount);
    }

    // ===== helpers =====

    private static AttestedSvid Svid(DateTimeOffset issued, DateTimeOffset expires, string tag) =>
        new(tag, "key", "ca", "spiffe://t/p/e/billing", issued, expires);

    private sealed class QueueSource(params AttestedSvid[] queued) : ISvidSource
    {
        private int _index;

        public Task<AttestedSvid> AttestAsync(CancellationToken ct)
        {
            var svid = queued[Math.Min(_index, queued.Length - 1)];
            _index++;
            return Task.FromResult(svid);
        }
    }
}
