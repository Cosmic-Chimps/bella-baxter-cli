using System.Threading.Channels;

namespace BellaCli.Services.Spiffe;

// Spec 001 T022 (US2) — one consumer's view of the rotating SVID.
//
// DROP-OLDEST, deliberately. A stream consumer only ever wants the NEWEST identity: an SVID that has
// already been superseded is of no use to anyone, and sending a backlog of them to a slow reader would
// deliver a sequence of certificates that are wrong by the time they arrive. So the channel holds
// capacity one and replaces rather than queues — a reader that falls behind loses intermediate SVIDs
// and receives the current one, which is exactly what it would have asked for.
//
// The alternative, an unbounded queue, is worse in the way that matters: under a fast rotation cadence
// it grows without limit while every entry in it is stale, and the consumer works through history to
// reach the present.

/// <summary>A single subscriber's channel of SVID updates.</summary>
internal sealed class SvidSubscription
{
    // Capacity 1 + DropOldest: keep the newest, discard what it supersedes. FullMode is what makes
    // Offer non-blocking, which matters because Offer is called while the agent holds its lock —
    // a blocking write there would let one slow consumer stall every renewal.
    private readonly Channel<AttestedSvid> _channel =
        Channel.CreateBounded<AttestedSvid>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Offers an SVID. Never blocks, never throws.</summary>
    internal void Offer(AttestedSvid svid) => _channel.Writer.TryWrite(svid);

    /// <summary>Yields SVIDs until cancelled, detaching on the way out.</summary>
    /// <param name="onCompleted">
    /// Runs when the consumer stops, however it stops. Detaching in a finally rather than after the
    /// loop is what prevents a leak: a consumer that disconnects mid-await (the normal case for a
    /// cancelled gRPC stream) would otherwise stay in the agent's subscriber list forever, and the
    /// agent would keep offering to a channel nobody reads.
    /// </param>
    internal async IAsyncEnumerable<AttestedSvid> ReadAllAsync(
        Action onCompleted,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var svid in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return svid;
            }
        }
        finally
        {
            onCompleted();
        }
    }
}
